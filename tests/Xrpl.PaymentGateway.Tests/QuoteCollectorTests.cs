using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteCollectorTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);
    private static readonly QuotePair Solo = new QuotePair("SOL", XpmIssuer, "USD", RlusdIssuer);

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(IQuoteSource source, Action<QuoteOptions>? configure = null, params QuotePair[] pairs)
        {
            Options = new QuoteOptions
            {
                Pairs = pairs.Length == 0 ? new[] { Xpm } : pairs,
                RefreshInterval = TimeSpan.FromMilliseconds(200),
                MinimumPairStagger = TimeSpan.FromMilliseconds(10),
                CaptureTimeout = TimeSpan.FromMilliseconds(200),
            };
            configure?.Invoke(Options);

            Registry = new QuoteRegistry(Options.Pairs);
            Collector = new QuoteCollector(
                Microsoft.Extensions.Options.Options.Create(Options),
                source,
                Store,
                Registry,
                TimeProvider.System,
                NullLogger<QuoteCollector>.Instance);
        }

        public QuoteOptions Options { get; }

        public InMemoryQuoteStore Store { get; } = new InMemoryQuoteStore();

        public QuoteRegistry Registry { get; }

        public QuoteCollector Collector { get; }

        public Task StartAsync() => Collector.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Collector.StopAsync(CancellationToken.None);
            Collector.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstCycleRunsAtStartupRatherThanAfterOneInterval()
    {
        // Otherwise the process has no prices at all for its first interval.
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        await using Harness harness = new Harness(source, options => options.RefreshInterval = TimeSpan.FromMinutes(10));

        await harness.StartAsync();

        await TestWait.UntilAsync(() => source.CountFor(Xpm.Key) >= 1, "the first capture");
        Assert.NotNull(harness.Registry.GetSnapshot(Xpm.Key));
    }

    [Fact]
    public async Task ASuccessfulCaptureIsCachedAndRecorded()
    {
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () => new StubQuoteSnapshot(price: 0.0123m, ledgerIndex: 4242);
        await using Harness harness = new Harness(source);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Store.GetQuoteAsync(Xpm.Key, Ct).GetAwaiter().GetResult() is not null,
            "the quote to be written");

        StoredQuote quote = (await harness.Store.GetQuoteAsync(Xpm.Key, Ct))!;
        Assert.Equal(0.0123m, quote.MarginalPrice);
        Assert.Equal(4242u, quote.LedgerIndex);
        Assert.Equal(0, quote.ConsecutiveFailures);
        Assert.Null(quote.LastError);
    }

    [Fact]
    public async Task ACaptureThatThrowsKeepsTheLastGoodSnapshot()
    {
        // A dropped socket must not read as "this pair went empty".
        int calls = 0;
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () =>
        {
            calls++;
            return calls == 1
                ? new StubQuoteSnapshot(price: 0.05m)
                : throw new InvalidOperationException("node unreachable");
        };

        await using Harness harness = new Harness(source);
        await harness.StartAsync();

        await TestWait.UntilAsync(
            () => harness.Store.GetQuoteAsync(Xpm.Key, Ct).GetAwaiter().GetResult()?.ConsecutiveFailures >= 1,
            "a failed refresh to be recorded");

        StoredQuote quote = (await harness.Store.GetQuoteAsync(Xpm.Key, Ct))!;
        Assert.Equal(0.05m, quote.MarginalPrice);
        Assert.NotNull(quote.CapturedAt);
        Assert.Contains("node unreachable", quote.LastError, StringComparison.Ordinal);
        Assert.NotNull(harness.Registry.GetSnapshot(Xpm.Key));
    }

    [Fact]
    public async Task ANullCaptureMeansThePairIsEmptyAndClearsTheSnapshot()
    {
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () => null;
        await using Harness harness = new Harness(source);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Store.GetQuoteAsync(Xpm.Key, Ct).GetAwaiter().GetResult() is not null,
            "the empty reading to be written");

        StoredQuote quote = (await harness.Store.GetQuoteAsync(Xpm.Key, Ct))!;
        Assert.Null(quote.MarginalPrice);
        Assert.Equal(0, quote.ConsecutiveFailures);
        Assert.Null(quote.LastError);
        Assert.Null(harness.Registry.GetSnapshot(Xpm.Key));
    }

    [Fact]
    public async Task AHangingCaptureIsAbandonedAndCountedAsAFailure()
    {
        await using Harness harness = new Harness(
            new HangingQuoteSource(),
            options => options.CaptureTimeout = TimeSpan.FromMilliseconds(100));

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Store.GetQuoteAsync(Xpm.Key, Ct).GetAwaiter().GetResult()?.ConsecutiveFailures >= 1,
            "the hung capture to be abandoned");

        Assert.Null((await harness.Store.GetQuoteAsync(Xpm.Key, Ct))!.MarginalPrice);
    }

    [Fact]
    public async Task PairsAreRefreshedOneAfterAnotherRatherThanAllAtOnce()
    {
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        await using Harness harness = new Harness(
            source,
            options =>
            {
                options.RefreshInterval = TimeSpan.FromSeconds(20);
                options.MinimumPairStagger = TimeSpan.FromSeconds(10);
            },
            Xpm,
            Solo);

        await harness.StartAsync();
        await TestWait.UntilAsync(() => source.Captured.Count >= 1, "the first pair");

        // Ten seconds must pass before the second one, so it cannot already be there.
        await Task.Delay(200, Ct);
        Assert.Single(source.Captured);
        Assert.Equal(Xpm.Key, source.Captured[0]);
    }

    [Fact]
    public async Task TheRegistryFindsAPairByTheAssetOfAPayment()
    {
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        await using Harness harness = new Harness(source);

        // Whichever way the balance reader spells the currency.
        Assert.Equal(Xpm, harness.Registry.FindPair("XPM", XpmIssuer));
        Assert.Equal(Xpm, harness.Registry.FindPair("00000000000000000000000058504D0000000000", XpmIssuer));
        Assert.Null(harness.Registry.FindPair("XPM", RlusdIssuer));
        Assert.Null(harness.Registry.FindPair("XRP", null));
    }
}
