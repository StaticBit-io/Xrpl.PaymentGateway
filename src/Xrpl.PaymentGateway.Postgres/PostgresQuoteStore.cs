using Npgsql;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Postgres;

/// <summary>
/// An <see cref="IQuoteStore"/> on PostgreSQL. Uniqueness of the valuation hash is enforced by the
/// database, so the queue holds one entry per payment however many components offer it.
/// </summary>
/// <remarks>
/// Its own tables, in the same schema as the payment store but written by their own class. Quotes are an
/// optional addition and have no business touching the code that records money.
/// </remarks>
public sealed class PostgresQuoteStore : IQuoteStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="schema">Schema to keep the tables in. Must be a plain SQL identifier.</param>
    public PostgresQuoteStore(string connectionString, string schema = "xrpl_payment_gateway")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SchemaName.Validate(schema, nameof(schema));

        _connectionString = connectionString;
        _schema = schema;
    }

    /// <summary>Creates the tables if they are not there. Safe on every start, and meant to run there.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string sql = $"""
            CREATE SCHEMA IF NOT EXISTS "{_schema}";

            CREATE TABLE IF NOT EXISTS "{_schema}".quotes (
                pair_key             TEXT        PRIMARY KEY,
                currency             TEXT        NOT NULL,
                issuer               TEXT        NULL,
                quote_currency       TEXT        NOT NULL,
                quote_issuer         TEXT        NULL,
                marginal_price       NUMERIC     NULL,
                ledger_index         BIGINT      NULL,
                captured_at          TIMESTAMPTZ NULL,
                last_attempt_at      TIMESTAMPTZ NOT NULL,
                consecutive_failures INTEGER     NOT NULL DEFAULT 0,
                last_error           TEXT        NULL
            );

            CREATE TABLE IF NOT EXISTS "{_schema}".valuations (
                transaction_hash      TEXT        PRIMARY KEY,
                queued_seq            BIGSERIAL   NOT NULL,
                pair_key              TEXT        NOT NULL,
                amount                NUMERIC     NOT NULL,
                payment_ledger_index  BIGINT      NOT NULL,
                destination_tag       BIGINT      NULL,
                enqueued_at           TIMESTAMPTZ NOT NULL,
                state                 INTEGER     NOT NULL DEFAULT 0,
                valued_at             TIMESTAMPTZ NULL,
                quote_amount          NUMERIC     NULL,
                effective_price       NUMERIC     NULL,
                marginal_price        NUMERIC     NULL,
                slippage_percent      NUMERIC     NULL,
                fully_filled          BOOLEAN     NOT NULL DEFAULT FALSE,
                book_truncated        BOOLEAN     NOT NULL DEFAULT FALSE,
                route                 TEXT        NULL,
                snapshot_ledger_index BIGINT      NULL,
                snapshot_captured_at  TIMESTAMPTZ NULL,
                failed_at             TIMESTAMPTZ NULL,
                failure_reason        TEXT        NULL,
                written_off_at        TIMESTAMPTZ NULL,
                write_off_reason      TEXT        NULL,
                delivered             BOOLEAN     NOT NULL DEFAULT FALSE
            );

            -- EnsureSchemaAsync is the whole migration story for this store, not just table creation: on
            -- a database where "valuations" already existed before a column below was introduced, the
            -- CREATE TABLE IF NOT EXISTS above is a no-op and would otherwise leave it missing, so every
            -- valuation query naming it fails from then on. Must run before the indexes below, which name
            -- "state" and "failed_at" in their predicate — on a table that predates those columns, an index
            -- built before this would fail with "column does not exist" the same way a query would.
            ALTER TABLE "{_schema}".valuations ADD COLUMN IF NOT EXISTS state INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE "{_schema}".valuations ADD COLUMN IF NOT EXISTS failed_at TIMESTAMPTZ NULL;
            ALTER TABLE "{_schema}".valuations ADD COLUMN IF NOT EXISTS failure_reason TEXT NULL;
            ALTER TABLE "{_schema}".valuations ADD COLUMN IF NOT EXISTS written_off_at TIMESTAMPTZ NULL;
            ALTER TABLE "{_schema}".valuations ADD COLUMN IF NOT EXISTS write_off_reason TEXT NULL;

            -- Backfill for a table that predates the "state" column: ADD COLUMN above defaults every
            -- existing row to 0 (Pending), which is right for a row that was never priced but wrong for
            -- one that was — before this column existed, the only way to reach valued_at was the automatic
            -- pipeline, so a non-null valued_at means Valued (1). Idempotent: once a row is migrated its
            -- state is no longer 0, so a later run touches nothing.
            UPDATE "{_schema}".valuations SET state = 1 WHERE state = 0 AND valued_at IS NOT NULL;

            -- GetPendingValuationsAsync now filters by pair_key as well as state, so the pending index
            -- from before that change — (queued_seq) WHERE state = 0, no pair_key — no longer matches its
            -- predicate. CREATE INDEX IF NOT EXISTS on the old name would silently keep the old, wrong
            -- shape on a database that already has it, so the old one is dropped by name first and the
            -- replacement takes a new name; the DROP is a cheap no-op on every start after the first.
            DROP INDEX IF EXISTS "{_schema}".valuations_pending;

            -- Partial indexes: both queues are short and the table is not, so a full scan per poll
            -- would grow with history rather than with work outstanding. State values are
            -- ValuationState's ordinals: 0 Pending, 1 Valued, 2 ValuedManually, 3 Failed, 4 WrittenOff.
            CREATE INDEX IF NOT EXISTS valuations_pending_by_pair
                ON "{_schema}".valuations (pair_key, queued_seq) WHERE state = 0;

            CREATE INDEX IF NOT EXISTS valuations_undelivered
                ON "{_schema}".valuations (queued_seq) WHERE state <> 0 AND delivered = FALSE;

            CREATE INDEX IF NOT EXISTS valuations_failed
                ON "{_schema}".valuations (failed_at, queued_seq) WHERE state = 3;
            """;

        await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".quotes (
                pair_key, currency, issuer, quote_currency, quote_issuer,
                marginal_price, ledger_index, captured_at, last_attempt_at, consecutive_failures, last_error)
            VALUES (@key, @cur, @iss, @qcur, @qiss, @price, @ledger, @captured, @attempt, @failures, @error)
            ON CONFLICT (pair_key) DO UPDATE SET
                currency = EXCLUDED.currency,
                issuer = EXCLUDED.issuer,
                quote_currency = EXCLUDED.quote_currency,
                quote_issuer = EXCLUDED.quote_issuer,
                marginal_price = EXCLUDED.marginal_price,
                ledger_index = EXCLUDED.ledger_index,
                captured_at = EXCLUDED.captured_at,
                last_attempt_at = EXCLUDED.last_attempt_at,
                consecutive_failures = EXCLUDED.consecutive_failures,
                last_error = EXCLUDED.last_error
            """,
            connection);

        command.Parameters.AddWithValue("key", quote.PairKey);
        command.Parameters.AddWithValue("cur", quote.Currency);
        command.Parameters.AddWithValue("iss", (object?)quote.Issuer ?? DBNull.Value);
        command.Parameters.AddWithValue("qcur", quote.QuoteCurrency);
        command.Parameters.AddWithValue("qiss", (object?)quote.QuoteIssuer ?? DBNull.Value);
        command.Parameters.AddWithValue("price", (object?)quote.MarginalPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("ledger", quote.LedgerIndex is { } l ? (long)l : (object)DBNull.Value);
        command.Parameters.AddWithValue("captured", (object?)quote.CapturedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt", quote.LastAttemptAt);
        command.Parameters.AddWithValue("failures", quote.ConsecutiveFailures);
        command.Parameters.AddWithValue("error", (object?)quote.LastError ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT {QuoteColumns} FROM "{_schema}".quotes WHERE pair_key = @key""", connection);
        command.Parameters.AddWithValue("key", pairKey);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadQuote(reader) : null;
    }

    public async Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT {QuoteColumns} FROM "{_schema}".quotes ORDER BY pair_key""", connection);

        List<StoredQuote> result = new List<StoredQuote>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadQuote(reader));
        }

        return result;
    }

    public async Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".valuations (
                transaction_hash, pair_key, amount, payment_ledger_index, destination_tag, enqueued_at)
            VALUES (@hash, @key, @amount, @ledger, @tag, @enqueued)
            ON CONFLICT (transaction_hash) DO NOTHING
            """,
            connection);

        command.Parameters.AddWithValue("hash", pending.TransactionHash);
        command.Parameters.AddWithValue("key", pending.PairKey);
        command.Parameters.AddWithValue("amount", pending.Amount);
        command.Parameters.AddWithValue("ledger", (long)pending.PaymentLedgerIndex);
        command.Parameters.AddWithValue(
            "tag", pending.DestinationTag is { } t ? (long)t : (object)DBNull.Value);
        command.Parameters.AddWithValue("enqueued", pending.EnqueuedAt);

        // DO NOTHING is right here, unlike in tag allocation: the loser needs no row back, only the
        // knowledge that somebody else already queued this payment.
        int inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        return QueryValuationsAsync(
            $"{StateFilter(ValuationState.Pending)} AND pair_key = @pairKey",
            "queued_seq",
            limit,
            offset: 0,
            cancellationToken,
            bind: command => command.Parameters.AddWithValue("pairKey", pairKey));
    }

    public async Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            SELECT pair_key, COUNT(*), MIN(enqueued_at)
            FROM "{_schema}".valuations
            WHERE {StateFilter(ValuationState.Pending)}
            GROUP BY pair_key
            """,
            connection);

        List<PendingValuationsByPair> result = new List<PendingValuationsByPair>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PendingValuationsByPair
            {
                PairKey = reader.GetString(0),
                Count = (int)reader.GetInt64(1),
                OldestEnqueuedAt = reader.GetFieldValue<DateTimeOffset>(2),
            });
        }

        return result;
    }

    public async Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            UPDATE "{_schema}".valuations SET
                state = @state,
                valued_at = @valued,
                quote_amount = @quote,
                effective_price = @effective,
                marginal_price = @marginal,
                slippage_percent = @slippage,
                fully_filled = @filled,
                book_truncated = @truncated,
                route = @route,
                snapshot_ledger_index = @snapLedger,
                snapshot_captured_at = @snapAt,
                failed_at = NULL,
                failure_reason = NULL,
                delivered = FALSE
            WHERE transaction_hash = @hash AND (state = @pendingState OR state = @failedState)
            """,
            connection);

        command.Parameters.AddWithValue("hash", valuation.TransactionHash);
        command.Parameters.AddWithValue("state", (int)valuation.State);
        command.Parameters.AddWithValue("valued", (object?)valuation.ValuedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("quote", (object?)valuation.QuoteAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("effective", (object?)valuation.EffectivePrice ?? DBNull.Value);
        command.Parameters.AddWithValue("marginal", (object?)valuation.MarginalPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("slippage", (object?)valuation.SlippagePercent ?? DBNull.Value);
        command.Parameters.AddWithValue("filled", valuation.FullyFilled);
        command.Parameters.AddWithValue("truncated", valuation.BookTruncated);
        command.Parameters.AddWithValue("route", (object?)valuation.Route ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "snapLedger", valuation.SnapshotLedgerIndex is { } s ? (long)s : (object)DBNull.Value);
        command.Parameters.AddWithValue("snapAt", (object?)valuation.SnapshotCapturedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("pendingState", (int)ValuationState.Pending);
        command.Parameters.AddWithValue("failedState", (int)ValuationState.Failed);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            UPDATE "{_schema}".valuations SET
                state = @failedState,
                failed_at = @failedAt,
                failure_reason = @reason
            WHERE transaction_hash = @hash AND state = @pendingState
            """,
            connection);

        command.Parameters.AddWithValue("hash", transactionHash);
        command.Parameters.AddWithValue("failedState", (int)ValuationState.Failed);
        command.Parameters.AddWithValue("pendingState", (int)ValuationState.Pending);
        command.Parameters.AddWithValue("failedAt", failedAt);
        command.Parameters.AddWithValue("reason", reason);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            UPDATE "{_schema}".valuations SET
                state = @writtenOffState,
                written_off_at = @writtenOffAt,
                write_off_reason = @reason,
                delivered = FALSE
            WHERE transaction_hash = @hash AND state = @failedState
            """,
            connection);

        command.Parameters.AddWithValue("hash", transactionHash);
        command.Parameters.AddWithValue("writtenOffState", (int)ValuationState.WrittenOff);
        command.Parameters.AddWithValue("failedState", (int)ValuationState.Failed);
        command.Parameters.AddWithValue("writtenOffAt", writtenOffAt);
        command.Parameters.AddWithValue("reason", reason);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken) =>
        QueryValuationsAsync(
            StateFilter(ValuationState.Failed), "failed_at, queued_seq", limit, offset, cancellationToken, bind: null);

    public async Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT COUNT(*) FROM "{_schema}".valuations WHERE {StateFilter(ValuationState.Failed)}""", connection);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)result!;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        QueryValuationsAsync(
            $"state <> {(int)ValuationState.Pending} AND delivered = FALSE",
            "queued_seq",
            limit,
            offset: 0,
            cancellationToken,
            bind: null);

    /// <summary>A SQL predicate on the "state" column for one <see cref="ValuationState"/>.</summary>
    /// <remarks>
    /// Interpolated directly rather than parameterized: the value always comes from this enum, never from
    /// a caller, so there is nothing here for a parameter to protect against.
    /// </remarks>
    private static string StateFilter(ValuationState state) => $"state = {(int)state}";

    public async Task MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Guarded by state: only applies when the row is still in the state actually handed to the host
        // handler, so an operator's resolution racing a slow delivery call for stale Failed content is not
        // marked delivered on the resolution's behalf — see IQuoteStore.MarkValuationDeliveredAsync.
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            UPDATE "{_schema}".valuations SET delivered = TRUE
            WHERE transaction_hash = @hash AND state = @deliveredState
            """,
            connection);
        command.Parameters.AddWithValue("hash", transactionHash);
        command.Parameters.AddWithValue("deliveredState", (int)deliveredState);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT {ValuationColumns} FROM "{_schema}".valuations WHERE transaction_hash = @hash""", connection);
        command.Parameters.AddWithValue("hash", transactionHash);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadValuation(reader) : null;
    }

    private const string QuoteColumns =
        "pair_key, currency, issuer, quote_currency, quote_issuer, marginal_price, ledger_index, " +
        "captured_at, last_attempt_at, consecutive_failures, last_error";

    private const string ValuationColumns =
        "transaction_hash, pair_key, amount, payment_ledger_index, destination_tag, enqueued_at, " +
        "state, valued_at, quote_amount, effective_price, marginal_price, slippage_percent, " +
        "fully_filled, book_truncated, route, snapshot_ledger_index, snapshot_captured_at, " +
        "failed_at, failure_reason, written_off_at, write_off_reason, delivered";

    private async Task<IReadOnlyList<PaymentValuation>> QueryValuationsAsync(
        string predicate,
        string orderBy,
        int limit,
        int offset,
        CancellationToken cancellationToken,
        Action<NpgsqlCommand>? bind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            SELECT {ValuationColumns} FROM "{_schema}".valuations
            WHERE {predicate}
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """,
            connection);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        bind?.Invoke(command);

        List<PaymentValuation> result = new List<PaymentValuation>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadValuation(reader));
        }

        return result;
    }

    private static StoredQuote ReadQuote(NpgsqlDataReader reader) => new StoredQuote
    {
        PairKey = reader.GetString(0),
        Currency = reader.GetString(1),
        Issuer = reader.IsDBNull(2) ? null : reader.GetString(2),
        QuoteCurrency = reader.GetString(3),
        QuoteIssuer = reader.IsDBNull(4) ? null : reader.GetString(4),
        MarginalPrice = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
        LedgerIndex = reader.IsDBNull(6) ? null : (uint)reader.GetInt64(6),
        CapturedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        LastAttemptAt = reader.GetFieldValue<DateTimeOffset>(8),
        ConsecutiveFailures = reader.GetInt32(9),
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static PaymentValuation ReadValuation(NpgsqlDataReader reader) => new PaymentValuation
    {
        TransactionHash = reader.GetString(0),
        PairKey = reader.GetString(1),
        Amount = reader.GetDecimal(2),
        PaymentLedgerIndex = (uint)reader.GetInt64(3),
        DestinationTag = reader.IsDBNull(4) ? null : (uint)reader.GetInt64(4),
        EnqueuedAt = reader.GetFieldValue<DateTimeOffset>(5),
        State = (ValuationState)reader.GetInt32(6),
        ValuedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        QuoteAmount = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        EffectivePrice = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
        MarginalPrice = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
        SlippagePercent = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        FullyFilled = reader.GetBoolean(12),
        BookTruncated = reader.GetBoolean(13),
        Route = reader.IsDBNull(14) ? null : reader.GetString(14),
        SnapshotLedgerIndex = reader.IsDBNull(15) ? null : (uint)reader.GetInt64(15),
        SnapshotCapturedAt = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
        FailedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        FailureReason = reader.IsDBNull(18) ? null : reader.GetString(18),
        WrittenOffAt = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
        WriteOffReason = reader.IsDBNull(20) ? null : reader.GetString(20),
        Delivered = reader.GetBoolean(21),
    };

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
