using Microsoft.Extensions.Options;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentGatewayOptionsValidatorTests
{
    private static PaymentGatewayOptions Valid() => new PaymentGatewayOptions
    {
        Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        Nodes = new[] { new Uri("ws://localhost:6006") },
    };

    private static ValidateOptionsResult Validate(PaymentGatewayOptions options) =>
        new PaymentGatewayOptionsValidator().Validate(Options.DefaultName, options);

    [Fact]
    public void AFullyConfiguredOptionsObjectPasses()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void AMissingAddressFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Address = string.Empty;

        Assert.Contains("Address", Validate(options).FailureMessage);
    }

    [Fact]
    public void AnEmptyNodePoolFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Nodes = Array.Empty<Uri>();

        Assert.Contains("Nodes", Validate(options).FailureMessage);
    }

    [Fact]
    public void ANonWebSocketNodeFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Nodes = new[] { new Uri("https://localhost:5005") };

        Assert.Contains("ws", Validate(options).FailureMessage);
    }

    [Fact]
    public void DestinationTagZeroFails()
    {
        PaymentGatewayOptions options = Valid();
        options.FirstDestinationTag = 0;

        Assert.Contains("FirstDestinationTag", Validate(options).FailureMessage);
    }

    [Fact]
    public void AZeroLedgerLagToleranceFails()
    {
        // The cursor trails the last validated ledger by one in normal operation, so a tolerance of zero
        // would make the health report permanently unhealthy with nothing actually wrong.
        PaymentGatewayOptions options = Valid();
        options.MaxAcceptableLedgerLag = 0;

        Assert.Contains("MaxAcceptableLedgerLag", Validate(options).FailureMessage);
    }

    [Fact]
    public void ANonPositiveStallTimeoutFails()
    {
        PaymentGatewayOptions options = Valid();
        options.LedgerStallTimeout = TimeSpan.Zero;

        Assert.Contains("LedgerStallTimeout", Validate(options).FailureMessage);
    }
}
