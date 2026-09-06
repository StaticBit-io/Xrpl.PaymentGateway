using Npgsql;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Postgres;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// The same directory contract against a real database. The one that decides whether the SQL behind the
/// listings says what the contract says — a LEFT JOIN whose filter is inverted, or a COUNT that inherits
/// the page's LIMIT, is invisible against an in-memory store.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresPaymentDirectoryTests : PaymentDirectoryContract, IAsyncDisposable
{
    /// <summary>
    /// Override with <c>XRPLPG_POSTGRES</c> to point at a database on a different port, so this
    /// repository's stand can run beside another project's. The default is what CI and Compose use.
    /// </summary>
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("XRPLPG_POSTGRES")
        ?? "Host=localhost;Port=55432;Username=xrplpg;Password=xrplpg;Database=xrplpg;Include Error Detail=true";

    // A schema per test class run, so a failure leaves nothing behind for the next one to trip over.
    private readonly string _schema = "test_" + Guid.NewGuid().ToString("N");

    private bool _created;

    protected override async Task<(IPaymentStore Store, IPaymentDirectory Directory)> CreateAsync()
    {
        await SkipUnlessDatabaseIsReachableAsync();

        PostgresPaymentStore store = new PostgresPaymentStore(ConnectionString, _schema);
        await store.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        _created = true;
        return (store, store);
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

    public async ValueTask DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        try
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new NpgsqlCommand(
                $"""DROP SCHEMA IF EXISTS "{_schema}" CASCADE""", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // The database is gone, which is the only reason cleanup can fail and also makes it moot.
        }
    }
}
