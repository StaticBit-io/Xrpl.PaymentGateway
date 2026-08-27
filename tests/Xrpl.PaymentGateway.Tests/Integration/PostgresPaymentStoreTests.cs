using Npgsql;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Postgres;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// The same contract as every other store, against a real database. This is the one that decides whether
/// the interface's atomicity requirements are actually satisfiable, because here the races are real.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresPaymentStoreTests : PaymentStoreContract, IAsyncDisposable
{
    private const string ConnectionString =
        "Host=localhost;Port=55432;Username=xrplpg;Password=xrplpg;Database=xrplpg;Include Error Detail=true";

    // A schema per test class run, so a failure leaves nothing behind for the next one to trip over.
    private readonly string _schema = "test_" + Guid.NewGuid().ToString("N");

    private bool _created;

    protected override async Task<IPaymentStore> CreateAsync(uint firstDestinationTag = 1)
    {
        await SkipUnlessDatabaseIsReachableAsync();

        PostgresPaymentStore store = new PostgresPaymentStore(ConnectionString, _schema, firstDestinationTag);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;
        return store;
    }

    protected override Task<IPaymentStore?> ReopenAsync(IPaymentStore store)
    {
        // A restart: a brand new store object over the same schema, holding nothing in memory.
        PostgresPaymentStore reopened = new PostgresPaymentStore(ConnectionString, _schema);
        return Task.FromResult<IPaymentStore?>(reopened);
    }

    [Fact]
    public async Task CreatingTheSchemaTwiceIsNotAnError()
    {
        await SkipUnlessDatabaseIsReachableAsync();
        PostgresPaymentStore store = new PostgresPaymentStore(ConnectionString, _schema);

        // Hosts are told to call this on every start, so it has to be idempotent.
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;

        await store.SetLastProcessedLedgerAsync(7, TestContext.Current.CancellationToken);
        Assert.Equal(7u, await store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TagZeroIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PostgresPaymentStore(ConnectionString, _schema, firstDestinationTag: 0));
    }

    [Fact]
    public async Task TwoSchemasInOneDatabaseKeepSeparateTagCounters()
    {
        await SkipUnlessDatabaseIsReachableAsync();

        string other = _schema + "_other";
        PostgresPaymentStore first = new PostgresPaymentStore(ConnectionString, _schema);
        PostgresPaymentStore second = new PostgresPaymentStore(ConnectionString, other);
        await first.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        await second.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;

        try
        {
            // Two receiving accounts sharing a database must not share a tag space.
            Assert.Equal(1u, await first.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken));
            Assert.Equal(1u, await second.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken));
        }
        finally
        {
            await DropSchemaAsync(other);
        }
    }

    private static async Task SkipUnlessDatabaseIsReachableAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.OpenAsync(cts.Token);
        }
        catch (Exception)
        {
            Assert.Skip("no PostgreSQL on localhost:55432; start .ci-config/docker-compose.ci.yml");
        }
    }

    private static async Task DropSchemaAsync(string schema)
    {
        try
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new NpgsqlCommand(
                $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // The database is gone, which is the only reason cleanup can fail and also makes it moot.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_created)
        {
            await DropSchemaAsync(_schema);
        }
    }
}
