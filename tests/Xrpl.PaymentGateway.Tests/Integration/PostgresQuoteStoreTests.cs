using Npgsql;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Postgres;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

[Trait("Category", "Integration")]
public class PostgresQuoteStoreTests : QuoteStoreContract, IAsyncDisposable
{
    /// <summary>
    /// Override with <c>XRPLPG_POSTGRES</c> to point at a database on a different port, so this
    /// repository's stand can run beside another project's. The default is what CI and Compose use.
    /// </summary>
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("XRPLPG_POSTGRES")
        ?? "Host=localhost;Port=55432;Username=xrplpg;Password=xrplpg;Database=xrplpg;Include Error Detail=true";

    // A schema per class run, so a failure leaves nothing behind for the next one to trip over.
    private readonly string _schema = "quotes_" + Guid.NewGuid().ToString("N");

    private bool _created;

    protected override async Task<IQuoteStore> CreateAsync()
    {
        await SkipUnlessDatabaseIsReachableAsync();

        PostgresQuoteStore store = new PostgresQuoteStore(ConnectionString, _schema);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;
        return store;
    }

    protected override Task<IQuoteStore?> ReopenAsync(IQuoteStore store) =>
        Task.FromResult<IQuoteStore?>(new PostgresQuoteStore(ConnectionString, _schema));

    [Fact]
    public async Task CreatingTheSchemaTwiceIsNotAnError()
    {
        // EnsureSchemaAsync runs on every start; that is the whole migration story.
        await SkipUnlessDatabaseIsReachableAsync();

        PostgresQuoteStore store = new PostgresQuoteStore(ConnectionString, _schema);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;
    }

    [Fact]
    public async Task EnsureSchemaAsyncAddsMissingColumnsToAValuationsTableThatPredatesTheStateColumn()
    {
        // "state" and the failure/write-off columns beside it were added to the valuations table's
        // CREATE TABLE IF NOT EXISTS body only. On a database where the table already existed before that
        // shipped — every earlier gateway version wrote a table shaped like this one — CREATE TABLE IF NOT
        // EXISTS is a no-op, and EnsureSchemaAsync would report success while every later valuation query
        // failed with "column state does not exist". Every other test in this class builds its schema from
        // EnsureSchemaAsync itself, so the table always already has the columns — this is the only shape of
        // test that would have caught the gap: build the pre-existing table by hand, without them, seed it
        // with rows the way a real database would already hold, run EnsureSchemaAsync, and prove both the
        // backfill and the new queue methods work afterwards.
        await SkipUnlessDatabaseIsReachableAsync();

        await using (NpgsqlConnection connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new NpgsqlCommand(
                $"""
                CREATE SCHEMA IF NOT EXISTS "{_schema}";
                CREATE TABLE "{_schema}".valuations (
                    transaction_hash      TEXT        PRIMARY KEY,
                    queued_seq            BIGSERIAL   NOT NULL,
                    pair_key              TEXT        NOT NULL,
                    amount                NUMERIC     NOT NULL,
                    payment_ledger_index  BIGINT      NOT NULL,
                    destination_tag       BIGINT      NULL,
                    enqueued_at           TIMESTAMPTZ NOT NULL,
                    last_attempt_at       TIMESTAMPTZ NULL,
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
                -- Seeded directly, the way a real pre-migration database already holds rows: one already
                -- valued, one still pending. The backfill must tell them apart correctly.
                INSERT INTO "{_schema}".valuations
                    (transaction_hash, pair_key, amount, payment_ledger_index, enqueued_at, valued_at, quote_amount, delivered)
                VALUES
                    ('PRE-EXISTING-VALUED', 'PAIR', 10, 1, now(), now(), 1.5, TRUE);
                INSERT INTO "{_schema}".valuations
                    (transaction_hash, pair_key, amount, payment_ledger_index, enqueued_at)
                VALUES
                    ('PRE-EXISTING-PENDING', 'PAIR', 20, 2, now());
                """,
                connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        _created = true;

        PostgresQuoteStore store = new PostgresQuoteStore(ConnectionString, _schema);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);

        // The backfill: a row that already had valued_at must read back Valued, not the column's own
        // default of Pending; a row that never had one must stay Pending.
        PaymentValuation? valued = await store.GetValuationAsync(
            "PRE-EXISTING-VALUED", TestContext.Current.CancellationToken);
        Assert.NotNull(valued);
        Assert.Equal(ValuationState.Valued, valued!.State);
        Assert.Equal(1.5m, valued.QuoteAmount);

        PaymentValuation? stillPending = await store.GetValuationAsync(
            "PRE-EXISTING-PENDING", TestContext.Current.CancellationToken);
        Assert.NotNull(stillPending);
        Assert.Equal(ValuationState.Pending, stillPending!.State);
        Assert.Contains(
            await store.GetPendingValuationsAsync(10, TestContext.Current.CancellationToken),
            v => v.TransactionHash == "PRE-EXISTING-PENDING");

        // The new queue methods, exercised end to end on the migrated table.
        PaymentValuation freshlyQueued = new PaymentValuation
        {
            TransactionHash = "PRE-EXISTING-SCHEMA",
            PairKey = "PAIR",
            Amount = 10m,
            PaymentLedgerIndex = 3,
            EnqueuedAt = DateTimeOffset.UtcNow,
        };
        Assert.True(await store.TryEnqueueValuationAsync(freshlyQueued, TestContext.Current.CancellationToken));

        await store.SaveValuationFailureAsync(
            "PRE-EXISTING-SCHEMA", "no liquidity", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(1, await store.CountFailedValuationsAsync(TestContext.Current.CancellationToken));

        await store.SaveWriteOffAsync(
            "PRE-EXISTING-SCHEMA", "dust", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        PaymentValuation? read = await store.GetValuationAsync(
            "PRE-EXISTING-SCHEMA", TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read!.State);
        Assert.Equal("dust", read.WriteOffReason);
    }

    [Fact]
    public void ASchemaNameThatIsNotAPlainIdentifierIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new PostgresQuoteStore(ConnectionString, "bad\"; DROP SCHEMA public CASCADE; --"));
    }

    private static async Task SkipUnlessDatabaseIsReachableAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            Assert.Skip("no postgres on localhost:55432; start .ci-config/docker-compose.ci.yml");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        await using NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
