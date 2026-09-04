using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteReaderTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (QuoteReader Reader, QuoteRegistry Registry) Build(
        Action<QuoteOptions>? configure = null,
        DateTimeOffset? now = null)
    {
        QuoteOptions options = new QuoteOptions
        {
            Pairs = new[] { Xpm },
            RefreshInterval = TimeSpan.FromMinutes(1),
        };
        configure?.Invoke(options);

        QuoteRegistry registry = new QuoteRegistry(options.Pairs);
        QuoteReader reader = new QuoteReader(
            Options.Create(options), registry, new FixedTimeProvider(now ?? Now));

        return (reader, registry);
    }

    [Fact]
    public async Task AnAssetWithNoConfiguredPairHasNoQuote()
    {
        (QuoteReader reader, _) = Build();

        Assert.Null(await reader.GetPriceAsync("SOL", XpmIssuer, Ct));
    }

    [Fact]
    public async Task APairWithNothingCapturedYetHasNoQuote()
    {
        (QuoteReader reader, _) = Build();

        Assert.Null(await reader.GetPriceAsync("XPM", XpmIssuer, Ct));
    }

    [Fact]
    public async Task AFreshReadingComesBackWithItsAge()
    {
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(
            price: 0.01m, ledgerIndex: 900, capturedAt: Now.AddSeconds(-30)));

        QuoteView? view = await reader.GetPriceAsync("XPM", XpmIssuer, Ct);

        Assert.NotNull(view);
        Assert.Equal(0.01m, view.MarginalPrice);
        Assert.Equal(900u, view.LedgerIndex);
        Assert.Equal(TimeSpan.FromSeconds(30), view.Age);
        Assert.False(view.IsStale);
        Assert.Null(view.Result);
    }

    [Fact]
    public async Task AReadingPastItsAgeLimitIsWithheldByDefault()
    {
        // Default MaxQuoteAge is three intervals: three minutes here.
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddMinutes(-4)));

        Assert.Null(await reader.GetPriceAsync("XPM", XpmIssuer, Ct));
    }

    [Fact]
    public async Task WithRefusalOffAStaleReadingComesBackFlagged()
    {
        (QuoteReader reader, QuoteRegistry registry) = Build(options => options.RefuseStaleQuotes = false);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddMinutes(-4)));

        QuoteView? view = await reader.GetPriceAsync("XPM", XpmIssuer, Ct);

        Assert.NotNull(view);
        Assert.True(view.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(4), view.Age);
    }

    [Fact]
    public async Task PricingAnAmountCarriesTheResultAndTheAgeTogether()
    {
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(price: 0.01m, capturedAt: Now.AddSeconds(-5)));

        QuoteView? view = await reader.QuoteAsync("XPM", XpmIssuer, 1000m, QuoteDirection.ExactInput, Ct);

        Assert.NotNull(view);
        Assert.NotNull(view.Result);
        Assert.Equal(10m, view.Result.OutputAmount);
        Assert.True(view.Result.IsFullyFilled);
        Assert.Equal(TimeSpan.FromSeconds(5), view.Age);
    }

    [Fact]
    public async Task ANoLiquiditySnapshotAnswersWithANullResultRatherThanAMiss()
    {
        // Distinct from APairWithNothingCapturedYetHasNoQuote: here the pair is configured and a
        // snapshot was captured (it has a ledger and CapturedAt), but the snapshot itself reports no
        // liquidity for the requested amount. The view must come back non-null with only Result null —
        // conflating this with a genuine miss would tell the caller there is no pair at all.
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new NoLiquiditySnapshot(capturedAt: Now));

        QuoteView? view = await reader.QuoteAsync("XPM", XpmIssuer, 1000m, QuoteDirection.ExactInput, Ct);

        Assert.NotNull(view);
        Assert.Null(view.MarginalPrice);
        Assert.Null(view.Result);
    }

    [Fact]
    public async Task TheHexAndReadableSpellingsReachTheSamePair()
    {
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now));

        Assert.NotNull(await reader.GetPriceAsync("00000000000000000000000058504D0000000000", XpmIssuer, Ct));
    }

    [Fact]
    public async Task ANonPositiveAmountIsRejected()
    {
        (QuoteReader reader, QuoteRegistry registry) = Build();
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.QuoteAsync("XPM", XpmIssuer, 0m, QuoteDirection.ExactInput, Ct));
    }

    [Fact]
    public async Task AnUnparseableCurrencyIsNotAQuoteMiss()
    {
        // A caller passing rubbish should be told, not quietly handed null as if the pair were absent.
        (QuoteReader reader, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.GetPriceAsync("NOT-A-CURRENCY", XpmIssuer, Ct));
    }
}
