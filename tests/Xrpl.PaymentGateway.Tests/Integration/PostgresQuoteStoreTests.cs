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
    public async Task EnsureSchemaAsyncAddsAMissingColumnToAValuationsTableThatPredatesIt()
    {
        // last_attempt_at was added to the valuations table's CREATE TABLE IF NOT EXISTS body only. On a
        // database where the table already existed before that column shipped, CREATE TABLE IF NOT EXISTS
        // is a no-op, and EnsureSchemaAsync used to report success while every later valuation query
        // failed with "column last_attempt_at does not exist". Every other test in this class builds its
        // schema from EnsureSchemaAsync itself, so the table always already has the column — this is the
        // only shape of test that would have caught the gap: build the pre-existing table by hand, without
        // the column, then run EnsureSchemaAsync and prove the queue actually works afterwards.
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
                """,
                connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        _created = true;

        PostgresQuoteStore store = new PostgresQuoteStore(ConnectionString, _schema);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);

        PaymentValuation pending = new PaymentValuation
        {
            TransactionHash = "PRE-EXISTING-SCHEMA",
            PairKey = "PAIR",
            Amount = 10m,
            PaymentLedgerIndex = 1,
            EnqueuedAt = DateTimeOffset.UtcNow,
        };
        Assert.True(await store.TryEnqueueValuationAsync(pending, TestContext.Current.CancellationToken));
        Assert.Single(await store.GetPendingValuationsAsync(10, TestContext.Current.CancellationToken));

        await store.MarkValuationAttemptedAsync(
            "PRE-EXISTING-SCHEMA", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        PaymentValuation? read = await store.GetValuationAsync(
            "PRE-EXISTING-SCHEMA", TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        Assert.NotNull(read!.LastAttemptAt);
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
