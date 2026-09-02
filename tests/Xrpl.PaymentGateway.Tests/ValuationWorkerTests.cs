using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ValuationWorkerTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentRecord Payment(string hash = "HASH1", uint? tag = 42, decimal value = 1000m) =>
        new PaymentRecord
        {
            TransactionHash = hash,
            TransactionType = "Payment",
            Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
            DestinationTag = tag,
            Currency = "XPM",
            Issuer = XpmIssuer,
            Value = value,
            LedgerIndex = 901,
            ProcessedAt = Now,
        };

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Action<QuoteOptions>? configure = null, IQuoteSource? source = null)
        {
            Options = new QuoteOptions
            {
                Pairs = new[] { Xpm },
                RefreshInterval = TimeSpan.FromMinutes(1),
                ValuationPollInterval = TimeSpan.FromMilliseconds(20),
            };
            configure?.Invoke(Options);

            Registry = new QuoteRegistry(Options.Pairs);
            Enqueuer = new ValuationEnqueuer(
                QuoteStore, Registry, new FixedTimeProvider(Now), NullLogger.Instance);
            Worker = new ValuationWorker(
                Microsoft.Extensions.Options.Options.Create(Options),
                QuoteStore,
                PaymentStore,
                Registry,
                source ?? new ScriptedQuoteSource(),
                Handler,
                new FixedTimeProvider(Now),
                NullLogger<ValuationWorker>.Instance);
        }

        public QuoteOptions Options { get; }

        public InMemoryQuoteStore QuoteStore { get; } = new InMemoryQuoteStore();

        public InMemoryPaymentStore PaymentStore { get; } = new InMemoryPaymentStore(firstDestinationTag: 42);

        public QuoteRegistry Registry { get; }

        public RecordingValuedHandler Handler { get; } = new RecordingValuedHandler();

        public ValuationEnqueuer Enqueuer { get; }

        public ValuationWorker Worker { get; }

        public Task StartAsync() => Worker.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    [Fact]
    public async Task APaymentInAConfiguredAssetIsQueued()
    {
        await using Harness harness = new Harness();

        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        IReadOnlyList<PaymentValuation> pending = await harness.QuoteStore.GetPendingValuationsAsync(10, Ct);
        Assert.Single(pending);
        Assert.Equal(Xpm.Key, pending[0].PairKey);
        Assert.Equal(1000m, pending[0].Amount);
        Assert.Equal(42u, pending[0].DestinationTag);
    }

    [Fact]
    public async Task APaymentInAnAssetWithNoPairIsNotQueuedAndIsNotAnError()
    {
        await using Harness harness = new Harness();
        PaymentRecord xrp = new PaymentRecord
        {
            TransactionHash = "HASH2",
            TransactionType = "Payment",
            Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
            Currency = "XRP",
            Value = 5m,
            LedgerIndex = 902,
            ProcessedAt = Now,
        };

        await harness.Enqueuer.EnqueueAsync(xrp, Ct);

        Assert.Empty(await harness.QuoteStore.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task QueueingTheSamePaymentTwiceLeavesOneEntry()
    {
        // Live processing, catch-up and reconciliation all offer it.
        await using Harness harness = new Harness();

        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task AQueuedPaymentIsValuedAndDeliveredToTheBuyer()
    {
        await using Harness harness = new Harness();
        Assert.Equal(42u, await harness.PaymentStore.GetOrAssignTagAsync("buyer-42", Ct));
        harness.Registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(price: 0.01m, ledgerIndex: 900, capturedAt: Now));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the valuation to be delivered");

        (PaymentValuation valuation, string? buyerId) = harness.Handler.Deliveries[0];
        Assert.Equal("buyer-42", buyerId);
        Assert.Equal(10m, valuation.QuoteAmount);
        Assert.Equal(0.01m, valuation.EffectivePrice);
        Assert.True(valuation.FullyFilled);
        Assert.Equal(900u, valuation.SnapshotLedgerIndex);
        Assert.Equal("STUB", valuation.Route);
        Assert.Empty(await harness.QuoteStore.GetUndeliveredValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task WithNoSnapshotYetThePaymentStaysQueuedRatherThanBeingValuedAtNothing()
    {
        await using Harness harness = new Harness();
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await Task.Delay(200, Ct);

        Assert.Empty(harness.Handler.Deliveries);
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task AStaleSnapshotDoesNotValueAPaymentWhenStaleQuotesAreRefused()
    {
        await using Harness harness = new Harness();
        harness.Registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddMinutes(-10)));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await Task.Delay(200, Ct);

        Assert.Empty(harness.Handler.Deliveries);
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task AHandlerThatThrowsLeavesTheValuationUndeliveredForTheNextPass()
    {
        await using Harness harness = new Harness();
        harness.Handler.Throws = true;
        harness.Registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.QuoteStore.GetUndeliveredValuationsAsync(10, Ct).GetAwaiter().GetResult().Count == 1,
            "the valuation to be computed but not delivered");

        Assert.NotEmpty(harness.Handler.Deliveries);
        PaymentValuation? stored = await harness.QuoteStore.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(stored);
        Assert.True(stored.IsValued);
        Assert.False(stored.Delivered);
    }

    [Fact]
    public async Task AnEvaluationThatThrowsDoesNotStopTheWorker()
    {
        // Metadata is written by whoever built the payment; an amount decimal cannot hold must not wedge
        // the queue the way it once wedged the monitor.
        await using Harness harness = new Harness();
        harness.Registry.SetSnapshot(Xpm.Key, new ThrowingQuoteSnapshot(Now));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(hash: "HASH2"), Ct);

        await harness.StartAsync();
        await Task.Delay(200, Ct);

        // Nothing delivered, nothing lost, and the loop is still running.
        Assert.Empty(harness.Handler.Deliveries);
        Assert.Equal(2, (await harness.QuoteStore.GetPendingValuationsAsync(10, Ct)).Count);
    }
}

/// <summary>A snapshot whose evaluation always throws.</summary>
public sealed class ThrowingQuoteSnapshot : IQuoteSnapshot
{
    public ThrowingQuoteSnapshot(DateTimeOffset capturedAt) => CapturedAt = capturedAt;

    public uint LedgerIndex => 900;

    public DateTimeOffset CapturedAt { get; }

    public decimal? MarginalPrice => 0.01m;

    public ValueTask<QuoteResult?> EvaluateAsync(
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken) =>
        throw new OverflowException("value was either too large or too small for a Decimal");
}
