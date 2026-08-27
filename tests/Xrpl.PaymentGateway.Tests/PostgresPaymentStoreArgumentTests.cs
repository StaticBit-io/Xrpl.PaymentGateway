using Xrpl.PaymentGateway.Postgres;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// Constructor validation, which needs no database. The store's behaviour against a real one is covered
/// by <c>PostgresPaymentStoreTests</c> through the shared store contract.
/// </summary>
public class PostgresPaymentStoreArgumentTests
{
    private const string AnyConnectionString = "Host=localhost;Username=u;Password=p;Database=d";

    [Theory]
    [InlineData("public\"; DROP SCHEMA public CASCADE; --")]
    [InlineData("has space")]
    [InlineData("1leading_digit")]
    [InlineData("dash-separated")]
    public void ASchemaNameThatIsNotAPlainIdentifierIsRejected(string schema)
    {
        // The schema is an identifier, so it is quoted into the SQL rather than parameterised. A name
        // carrying a quote would break out of that quoting, so it never reaches a statement.
        Assert.Throws<ArgumentException>(() => new PostgresPaymentStore(AnyConnectionString, schema));
    }

    [Theory]
    [InlineData("xrpl_payment_gateway")]
    [InlineData("_private")]
    [InlineData("Tenant42")]
    public void APlainIdentifierIsAccepted(string schema)
    {
        PostgresPaymentStore store = new PostgresPaymentStore(AnyConnectionString, schema);

        Assert.NotNull(store);
    }

    [Fact]
    public void DestinationTagZeroIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PostgresPaymentStore(AnyConnectionString, "xrpl_payment_gateway", 0));
    }
}
