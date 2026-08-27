using Npgsql;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Postgres;

/// <summary>
/// An <see cref="IPaymentStore"/> on PostgreSQL. The two requirements the interface calls hard — atomic
/// tag allocation and uniqueness of the transaction hash — are handed to the database rather than to
/// application locking, so they hold across processes and restarts alike.
/// </summary>
/// <remarks>
/// Tag numbers come from a sequence, so they are unique and increasing but not guaranteed gapless: a
/// value is consumed when two callers race for the same new buyer and one of them loses. Nothing depends
/// on the numbering being dense, and a buyer's tag never changes once issued.
/// </remarks>
public sealed class PostgresPaymentStore : IPaymentStore
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly uint _firstDestinationTag;

    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="schema">
    /// The schema to keep the tables in. Separate schemas let several receiving accounts share one
    /// database without sharing a tag counter.
    /// </param>
    /// <param name="firstDestinationTag">
    /// The first tag to issue, applied when the schema is created. Zero is rejected: many wallets read it
    /// as "no tag".
    /// </param>
    public PostgresPaymentStore(string connectionString, string schema = "xrpl_payment_gateway", uint firstDestinationTag = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (firstDestinationTag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDestinationTag), "destination tag 0 is not issued");
        }

        _connectionString = connectionString;
        _schema = schema;
        _firstDestinationTag = firstDestinationTag;
    }

    /// <summary>
    /// Creates the schema if it is not there. Safe to call on every start, and doing so is the intended
    /// use: it is the only migration this store has.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string sql = $"""
            CREATE SCHEMA IF NOT EXISTS "{_schema}";

            CREATE SEQUENCE IF NOT EXISTS "{_schema}".destination_tag_seq
                AS BIGINT START WITH {_firstDestinationTag} MINVALUE {_firstDestinationTag} NO CYCLE;

            CREATE TABLE IF NOT EXISTS "{_schema}".buyers (
                buyer_id        TEXT   PRIMARY KEY,
                destination_tag BIGINT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS "{_schema}".payments (
                transaction_hash TEXT        PRIMARY KEY,
                recorded_seq     BIGSERIAL   NOT NULL,
                transaction_type TEXT        NOT NULL,
                sender           TEXT        NOT NULL,
                destination_tag  BIGINT      NULL,
                currency         TEXT        NOT NULL,
                issuer           TEXT        NULL,
                value            NUMERIC     NOT NULL,
                ledger_index     BIGINT      NOT NULL,
                processed_at     TIMESTAMPTZ NOT NULL,
                handled          BOOLEAN     NOT NULL DEFAULT FALSE
            );

            -- Reconciliation asks only for what is still undelivered, and on a healthy gateway that is
            -- almost nothing while the table grows without bound.
            CREATE INDEX IF NOT EXISTS payments_unhandled_idx
                ON "{_schema}".payments (recorded_seq) WHERE NOT handled;

            CREATE TABLE IF NOT EXISTS "{_schema}".cursor (
                id           BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (id),
                ledger_index BIGINT  NOT NULL
            );
            """;

        await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // The common case by far is a buyer who already has a tag, and answering that without touching
        // the sequence keeps checkout from burning a number on every visit.
        await using (NpgsqlCommand existing = new NpgsqlCommand(
            $"""SELECT destination_tag FROM "{_schema}".buyers WHERE buyer_id = @buyer""", connection))
        {
            existing.Parameters.AddWithValue("buyer", buyerId);
            object? found = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (found is long tag)
            {
                return (uint)tag;
            }
        }

        // DO UPDATE rather than DO NOTHING: on a race the losing caller still needs the row back, and
        // DO NOTHING returns nothing at all.
        await using NpgsqlCommand insert = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".buyers (buyer_id, destination_tag)
            VALUES (@buyer, nextval('"{_schema}".destination_tag_seq'))
            ON CONFLICT (buyer_id) DO UPDATE SET buyer_id = EXCLUDED.buyer_id
            RETURNING destination_tag
            """,
            connection);
        insert.Parameters.AddWithValue("buyer", buyerId);

        object? assigned = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"the database did not return a destination tag for buyer '{buyerId}'");

        long value = (long)assigned;
        if (value > uint.MaxValue)
        {
            throw new InvalidOperationException("the destination tag space is exhausted");
        }

        return (uint)value;
    }

    public async Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT buyer_id FROM "{_schema}".buyers WHERE destination_tag = @tag""", connection);
        command.Parameters.AddWithValue("tag", (long)tag);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".payments
                (transaction_hash, transaction_type, sender, destination_tag, currency, issuer, value, ledger_index, processed_at)
            VALUES
                (@hash, @type, @sender, @tag, @currency, @issuer, @value, @ledger, @processed)
            ON CONFLICT (transaction_hash) DO NOTHING
            """,
            connection);

        command.Parameters.AddWithValue("hash", record.TransactionHash);
        command.Parameters.AddWithValue("type", record.TransactionType);
        command.Parameters.AddWithValue("sender", record.Sender);
        command.Parameters.AddWithValue("tag", record.DestinationTag is { } tag ? (long)tag : DBNull.Value);
        command.Parameters.AddWithValue("currency", record.Currency);
        command.Parameters.AddWithValue("issuer", (object?)record.Issuer ?? DBNull.Value);
        command.Parameters.AddWithValue("value", record.Value);
        command.Parameters.AddWithValue("ledger", (long)record.LedgerIndex);
        command.Parameters.AddWithValue("processed", record.ProcessedAt);

        // Zero rows means the hash was already there, which is the duplicate the contract asks about.
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""UPDATE "{_schema}".payments SET handled = TRUE WHERE transaction_hash = @hash""", connection);
        command.Parameters.AddWithValue("hash", transactionHash);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            SELECT transaction_hash, transaction_type, sender, destination_tag, currency, issuer, value, ledger_index, processed_at
            FROM "{_schema}".payments
            WHERE NOT handled
            ORDER BY recorded_seq
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("limit", limit);

        List<PaymentRecord> records = new List<PaymentRecord>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new PaymentRecord
            {
                TransactionHash = reader.GetString(0),
                TransactionType = reader.GetString(1),
                Sender = reader.GetString(2),
                DestinationTag = reader.IsDBNull(3) ? null : (uint)reader.GetInt64(3),
                Currency = reader.GetString(4),
                Issuer = reader.IsDBNull(5) ? null : reader.GetString(5),
                Value = reader.GetDecimal(6),
                LedgerIndex = (uint)reader.GetInt64(7),
                ProcessedAt = reader.GetFieldValue<DateTimeOffset>(8),
            });
        }

        return records;
    }

    public async Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""SELECT ledger_index FROM "{_schema}".cursor WHERE id""", connection);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long ledger ? (uint)ledger : null;
    }

    public async Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".cursor (id, ledger_index) VALUES (TRUE, @ledger)
            ON CONFLICT (id) DO UPDATE SET ledger_index = EXCLUDED.ledger_index
            """,
            connection);
        command.Parameters.AddWithValue("ledger", (long)ledgerIndex);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
