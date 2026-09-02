using Npgsql;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Postgres;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

[Trait("Category", "Integration")]
public class PostgresQuoteStoreTests : QuoteStoreContract, IAsyncDisposable
{
    private const string ConnectionString =
        "Host=localhost;Port=55432;Username=xrplpg;Password=xrplpg;Database=xrplpg;Include Error Detail=true";

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
