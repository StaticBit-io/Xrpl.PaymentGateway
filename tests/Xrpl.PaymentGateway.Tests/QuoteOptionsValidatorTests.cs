using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteOptionsValidatorTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static QuoteOptions Valid() => new QuoteOptions
    {
        Pairs = new[] { new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer) },
    };

    private static ValidateOptionsResult Validate(QuoteOptions options) =>
        new QuoteOptionsValidator().Validate(Options.DefaultName, options);

    [Fact]
    public void AFullyConfiguredOptionsObjectPasses()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void QuotesWithNoPairsAreAMisconfiguration()
    {
        QuoteOptions options = Valid();
        options.Pairs = Array.Empty<QuotePair>();

        Assert.Contains("Pairs", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePairWrittenTwoWaysIsADuplicate()
    {
        // Two rows for one asset would be refreshed by two independent cycles.
        QuoteOptions options = Valid();
        options.Pairs = new[]
        {
            new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer),
            new QuotePair("00000000000000000000000058504D0000000000", XpmIssuer, "USD", RlusdIssuer),
        };

        Assert.Contains("duplicate", Validate(options).FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonPositiveIntervalFails()
    {
        QuoteOptions options = Valid();
        options.RefreshInterval = TimeSpan.Zero;

        Assert.Contains("RefreshInterval", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaxAgeBelowTheIntervalFails()
    {
        // Anything shorter and every quote is stale the moment after it is written.
        QuoteOptions options = Valid();
        options.RefreshInterval = TimeSpan.FromMinutes(1);
        options.MaxQuoteAge = TimeSpan.FromSeconds(30);

        Assert.Contains("MaxQuoteAge", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsetMaxAgeDefaultsToThreeIntervals()
    {
        QuoteOptions options = Valid();
        options.RefreshInterval = TimeSpan.FromMinutes(1);
        options.MaxQuoteAge = null;

        Assert.True(Validate(options).Succeeded);
        Assert.Equal(TimeSpan.FromMinutes(3), options.EffectiveMaxQuoteAge);
    }

    [Fact]
    public void ANonPositiveCaptureTimeoutFails()
    {
        QuoteOptions options = Valid();
        options.CaptureTimeout = TimeSpan.Zero;

        Assert.Contains("CaptureTimeout", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveBatchSizeFails()
    {
        QuoteOptions options = Valid();
        options.ValuationBatchSize = 0;

        Assert.Contains("ValuationBatchSize", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveValuationPollIntervalFails()
    {
        QuoteOptions options = Valid();
        options.ValuationPollInterval = TimeSpan.Zero;

        Assert.Contains("ValuationPollInterval", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveEnqueueTimeoutFails()
    {
        QuoteOptions options = Valid();
        options.EnqueueTimeout = TimeSpan.Zero;

        Assert.Contains("EnqueueTimeout", Validate(options).FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoQuoteCurrenciesForOneReceivedAssetIsAMisconfiguration()
    {
        // QuotePair.Matches identifies the received asset by currency and issuer alone, and
        // QuoteRegistry.FindPair returns the first match, so a second pair for the same received asset
        // against a different quote currency would validate, refresh and report healthy while being
        // silently unreachable.
        QuoteOptions options = Valid();
        options.Pairs = new[]
        {
            new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer),
            new QuotePair("XPM", XpmIssuer, "EUR", RlusdIssuer),
        };

        Assert.Contains("quote currency", Validate(options).FailureMessage, StringComparison.OrdinalIgnoreCase);
    }
}
