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
                delivered             BOOLEAN     NOT NULL DEFAULT FALSE
            );

            -- Partial indexes: both queues are short and the table is not, so a full scan per poll
            -- would grow with history rather than with work outstanding.
            CREATE INDEX IF NOT EXISTS valuations_pending
                ON "{_schema}".valuations (queued_seq) WHERE valued_at IS NULL;

            CREATE INDEX IF NOT EXISTS valuations_undelivered
                ON "{_schema}".valuations (queued_seq) WHERE valued_at IS NOT NULL AND delivered = FALSE;
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

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        QueryValuationsAsync("valued_at IS NULL", limit, cancellationToken);

    public async Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            UPDATE "{_schema}".valuations SET
                valued_at = @valued,
                quote_amount = @quote,
                effective_price = @effective,
                marginal_price = @marginal,
                slippage_percent = @slippage,
                fully_filled = @filled,
                book_truncated = @truncated,
                route = @route,
                snapshot_ledger_index = @snapLedger,
                snapshot_captured_at = @snapAt
            WHERE transaction_hash = @hash
            """,
            connection);

        command.Parameters.AddWithValue("hash", valuation.TransactionHash);
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

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        QueryValuationsAsync("valued_at IS NOT NULL AND delivered = FALSE", limit, cancellationToken);

    public async Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""UPDATE "{_schema}".valuations SET delivered = TRUE WHERE transaction_hash = @hash""", connection);
        command.Parameters.AddWithValue("hash", transactionHash);

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
        "valued_at, quote_amount, effective_price, marginal_price, slippage_percent, fully_filled, " +
        "book_truncated, route, snapshot_ledger_index, snapshot_captured_at, delivered";

    private async Task<IReadOnlyList<PaymentValuation>> QueryValuationsAsync(
        string predicate,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            SELECT {ValuationColumns} FROM "{_schema}".valuations
            WHERE {predicate}
            ORDER BY queued_seq
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("limit", limit);

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
        ValuedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        QuoteAmount = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        EffectivePrice = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        MarginalPrice = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
        SlippagePercent = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
        FullyFilled = reader.GetBoolean(11),
        BookTruncated = reader.GetBoolean(12),
        Route = reader.IsDBNull(13) ? null : reader.GetString(13),
        SnapshotLedgerIndex = reader.IsDBNull(14) ? null : (uint)reader.GetInt64(14),
        SnapshotCapturedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        Delivered = reader.GetBoolean(16),
    };

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
