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
            : this(source, store: null, configure, pairs)
        {
        }

        public Harness(
            IQuoteSource source, IQuoteStore? store, Action<QuoteOptions>? configure, params QuotePair[] pairs)
        {
            Options = new QuoteOptions
            {
                Pairs = pairs.Length == 0 ? new[] { Xpm } : pairs,
                RefreshInterval = TimeSpan.FromMilliseconds(200),
                MinimumPairStagger = TimeSpan.FromMilliseconds(10),
                CaptureTimeout = TimeSpan.FromMilliseconds(200),
            };
            configure?.Invoke(Options);

            Store = store ?? new InMemoryQuoteStore();
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

        public IQuoteStore Store { get; }

        public QuoteRegistry Registry { get; }

        public QuoteCollector Collector { get; }

        public Task StartAsync() => Collector.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Collector.StopAsync(CancellationToken.None);
            Collector.Dispose();
        }
    }

    /// <summary>An <see cref="IQuoteSource"/> that hangs like <see cref="HangingQuoteSource"/> but
    /// signals once a capture is actually in flight, so a test can cancel deterministically mid-capture
    /// instead of racing the background loop's startup.</summary>
    private sealed class SignallingHangingQuoteSource : IQuoteSource
    {
        private readonly TaskCompletionSource _started =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>An <see cref="IQuoteStore"/> whose reads always fail while writes and everything else
    /// delegate to a real <see cref="InMemoryQuoteStore"/>, so a test can inspect what, if anything,
    /// actually got persisted.</summary>
    private sealed class ReadThrowsQuoteStore : IQuoteStore
    {
        private readonly InMemoryQuoteStore _inner;

        public ReadThrowsQuoteStore(InMemoryQuoteStore inner) => _inner = inner;

        public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store read unavailable");

        public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) =>
            _inner.SaveQuoteAsync(quote, cancellationToken);

        public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
            _inner.GetQuotesAsync(cancellationToken);

        public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken) =>
            _inner.TryEnqueueValuationAsync(pending, cancellationToken);

        public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
            int limit, CancellationToken cancellationToken) =>
            _inner.GetPendingValuationsAsync(limit, cancellationToken);

        public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) =>
            _inner.SaveValuationAsync(valuation, cancellationToken);

        public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(
            int limit, CancellationToken cancellationToken) =>
            _inner.GetUndeliveredValuationsAsync(limit, cancellationToken);

        public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) =>
            _inner.MarkValuationDeliveredAsync(transactionHash, cancellationToken);

        public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
            _inner.GetValuationAsync(transactionHash, cancellationToken);
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

    [Fact]
    public async Task AShutdownDuringAHungCaptureLeavesTheExecuteTaskCompletedNotFaulted()
    {
        // RefreshAsync rethrows OperationCanceledException when stoppingToken (not the capture
        // timeout) caused the cancellation. A real IQuoteSource that honours the token during a live
        // network call, cancelled by host shutdown, takes exactly this path — unlike the capture-
        // timeout path the other tests exercise, where stoppingToken itself is never cancelled.
        SignallingHangingQuoteSource source = new SignallingHangingQuoteSource();
        await using Harness harness = new Harness(
            source, options => options.CaptureTimeout = TimeSpan.FromSeconds(10));

        await harness.StartAsync();

        // Wait for the actual hang rather than a fixed delay: this is a deterministic signal, not a
        // race against however fast the background loop happens to start.
        await source.Started.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await harness.Collector.StopAsync(CancellationToken.None);

        Assert.NotNull(harness.Collector.ExecuteTask);
        Assert.True(
            harness.Collector.ExecuteTask!.IsCompletedSuccessfully,
            $"expected the execute task to complete normally, but its status was {harness.Collector.ExecuteTask.Status}");
    }

    [Fact]
    public async Task AFailedReadOfThePreviousRowDoesNotBlankAGoodStoredQuote()
    {
        // A read failure is not proof the pair has no history — it is proof we could not see it this
        // time. Building a failure record on top of "no previous row" would silently erase a perfectly
        // good reading purely because the store hiccupped on the read.
        InMemoryQuoteStore inner = new InMemoryQuoteStore();
        await inner.SaveQuoteAsync(
            new StoredQuote
            {
                PairKey = Xpm.Key,
                Currency = Xpm.Currency,
                Issuer = Xpm.Issuer,
                QuoteCurrency = Xpm.QuoteCurrency,
                QuoteIssuer = Xpm.QuoteIssuer,
                MarginalPrice = 0.09m,
                LedgerIndex = 777,
                CapturedAt = DateTimeOffset.UtcNow,
                LastAttemptAt = DateTimeOffset.UtcNow,
                ConsecutiveFailures = 0,
                LastError = null,
            },
            Ct);

        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () => throw new InvalidOperationException("node unreachable");

        ReadThrowsQuoteStore store = new ReadThrowsQuoteStore(inner);
        await using Harness harness = new Harness(source, store, configure: null);

        await harness.StartAsync();

        // Several cycles, not just one, so a hollow write happening on a later attempt is caught too.
        await TestWait.UntilAsync(() => source.CountFor(Xpm.Key) >= 3, "several failed captures");

        StoredQuote? quote = await inner.GetQuoteAsync(Xpm.Key, Ct);
        Assert.NotNull(quote);
        Assert.Equal(0.09m, quote!.MarginalPrice);
        Assert.Equal(777u, quote.LedgerIndex);
        Assert.Equal(0, quote.ConsecutiveFailures);
        Assert.Null(quote.LastError);
    }
}
