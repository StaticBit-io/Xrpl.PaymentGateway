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
    private const string BarIssuer = "rBARxBeefHGxx2K7g5qmmWq3gFsgawkoa";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);
    private static readonly QuotePair Bar = new QuotePair("BAR", BarIssuer, "USD", RlusdIssuer);
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentRecord Payment(
        string hash = "HASH1",
        uint? tag = 42,
        decimal value = 1000m,
        string currency = "XPM",
        string? issuer = XpmIssuer) =>
        new PaymentRecord
        {
            TransactionHash = hash,
            TransactionType = "Payment",
            Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
            DestinationTag = tag,
            Currency = currency,
            Issuer = issuer,
            Value = value,
            LedgerIndex = 901,
            ProcessedAt = Now,
        };

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            Action<QuoteOptions>? configure = null,
            IQuoteSource? source = null,
            Func<InMemoryQuoteStore, IQuoteStore>? wrapQuoteStore = null)
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
                Microsoft.Extensions.Options.Options.Create(Options),
                QuoteStore,
                Registry,
                new FixedTimeProvider(Now),
                NullLogger.Instance);

            // The worker can be pointed at a decorator (e.g. one that fails a chosen write) while the
            // enqueuer and every assertion still go through the plain in-memory store underneath it.
            IQuoteStore workerStore = wrapQuoteStore?.Invoke(QuoteStore) ?? QuoteStore;
            Worker = new ValuationWorker(
                Microsoft.Extensions.Options.Options.Create(Options),
                workerStore,
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

        IReadOnlyList<PaymentValuation> pending = await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct);
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

        Assert.Empty(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
    }

    [Fact]
    public async Task QueueingTheSamePaymentTwiceLeavesOneEntry()
    {
        // Live processing, catch-up and reconciliation all offer it.
        await using Harness harness = new Harness();

        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
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
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
    }

    [Fact]
    public async Task ANoLiquidityAnswerLeavesThePaymentQueuedRatherThanFailingItForGood()
    {
        // The snapshot is fresh and present — this is not the "nothing captured yet" or "stale" case
        // covered elsewhere — but EvaluateAsync itself reports no liquidity for this amount right now. That
        // is transient, not terminal: the next capture may price it fine, exactly as the reason string used
        // to say ("the pair currently has no liquidity"). The entry must stay Pending rather than being
        // parked in the operator queue over a condition that can clear on its own.
        await using Harness harness = new Harness();
        harness.Registry.SetSnapshot(Xpm.Key, new NoLiquiditySnapshot(capturedAt: Now));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await Task.Delay(200, Ct);

        Assert.Empty(harness.Handler.Deliveries);
        IReadOnlyList<PaymentValuation> pending = await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct);
        Assert.Single(pending);
        Assert.Equal(ValuationState.Pending, pending[0].State);
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
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
    }

    [Fact]
    public async Task ValuateWithFreshSnapshotPricesAgainstANewCaptureRatherThanTheRegistry()
    {
        // Nothing sets a registry snapshot for Xpm here. If the worker fell back to the registry instead
        // of capturing fresh — the ValuateWithFreshSnapshot-off behaviour SnapshotForAsync takes — the
        // payment would have nothing to price against and would stay queued forever, so a delivery here
        // is proof the fresh-capture branch, not the registry one, priced it.
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () => new StubQuoteSnapshot(price: 0.02m, ledgerIndex: 950, capturedAt: Now);
        await using Harness harness = new Harness(
            configure: options => options.ValuateWithFreshSnapshot = true,
            source: source);
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Count == 1, "the freshly captured valuation to be delivered");

        Assert.Contains(Xpm.Key, source.Captured);
        PaymentValuation valuation = harness.Handler.Deliveries[0].Valuation;
        Assert.Equal(20m, valuation.QuoteAmount);
        Assert.Equal(950u, valuation.SnapshotLedgerIndex);
    }

    [Fact]
    public async Task AFreshCaptureFailureLeavesThePaymentQueuedRatherThanValuingItAtNothing()
    {
        // Mirrors WithNoSnapshotYetThePaymentStaysQueuedRatherThanBeingValuedAtNothing, but on the
        // ValuateWithFreshSnapshot path: SnapshotForAsync's catch logs and returns null on a capture
        // failure, and ValuePendingAsync must treat a null snapshot the same way it does when nothing has
        // been captured yet — the payment stays queued rather than disappearing or being valued at zero.
        ScriptedQuoteSource source = new ScriptedQuoteSource();
        source.Behaviour[Xpm.Key] = () => throw new InvalidOperationException("node unreachable");
        await using Harness harness = new Harness(
            configure: options => options.ValuateWithFreshSnapshot = true,
            source: source);
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        await harness.StartAsync();
        await Task.Delay(200, Ct);

        Assert.Empty(harness.Handler.Deliveries);
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
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
    public async Task AnEvaluationThatThrowsFailsTheEntryRatherThanStarvingAnotherPairsEntry()
    {
        // Metadata is written by whoever built the payment; an amount decimal cannot hold must not wedge
        // the queue the way it once wedged the monitor. Pricing that throws is deterministic and per-entry
        // — the same amount against the same snapshot throws the same way every time — so the entry is
        // failed rather than left pending. Two pairs, each priced from its own snapshot and its own pending
        // queue, prove a poisoned pair costs nothing to a healthy one queued behind it.
        await using Harness harness = new Harness(configure: options => options.Pairs = new[] { Xpm, Bar });
        harness.Registry.SetSnapshot(Xpm.Key, new ThrowingQuoteSnapshot(Now));
        harness.Registry.SetSnapshot(Bar.Key, new StubQuoteSnapshot(price: 0.02m, ledgerIndex: 900, capturedAt: Now));

        await harness.Enqueuer.EnqueueAsync(Payment(hash: "POISON", currency: "XPM", issuer: XpmIssuer), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(hash: "HEALTHY", currency: "BAR", issuer: BarIssuer), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Any(d => d.Valuation.TransactionHash == "HEALTHY"),
            "the healthy pair's entry to be valued and delivered despite the poisoned pair");

        Assert.Empty(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
        Assert.Empty(await harness.QuoteStore.GetPendingValuationsAsync(Bar.Key, 10, Ct));

        PaymentValuation? poisoned = await harness.QuoteStore.GetValuationAsync("POISON", Ct);
        Assert.NotNull(poisoned);
        Assert.Equal(ValuationState.Failed, poisoned!.State);
        Assert.Contains("threw", poisoned.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABrokenPairDoesNotDelayAHealthyPairsPayments()
    {
        // The defect a shared, fairness-ordered queue used to reintroduce in a different shape: a pair
        // with no usable snapshot must cost only its own payments a delay, not the payments of a pair right
        // behind it. Xpm never gets a snapshot at all; Bar's prices normally throughout.
        await using Harness harness = new Harness(configure: options => options.Pairs = new[] { Xpm, Bar });
        harness.Registry.SetSnapshot(Bar.Key, new StubQuoteSnapshot(price: 0.02m, ledgerIndex: 900, capturedAt: Now));

        await harness.Enqueuer.EnqueueAsync(Payment(hash: "STUCK", currency: "XPM", issuer: XpmIssuer), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(hash: "HEALTHY", currency: "BAR", issuer: BarIssuer), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Any(d => d.Valuation.TransactionHash == "HEALTHY"),
            "Bar's entry to be valued despite Xpm never having a snapshot");

        Assert.DoesNotContain(harness.Handler.Deliveries, d => d.Valuation.TransactionHash == "STUCK");
        Assert.Single(await harness.QuoteStore.GetPendingValuationsAsync(Xpm.Key, 10, Ct));
    }

    [Fact]
    public async Task ASaveFailureLeavesTheEntryPendingRatherThanFailingItForGood()
    {
        // A store write failure is the archetypal transient error — a timeout, a dropped connection, a
        // deadlock. Parking a correctly priced payment in the operator queue over a momentary blip is not
        // acceptable, so the entry stays Pending and prices itself again once the store stops rejecting the
        // write. Two pairs prove the rejection for one entry does not block the payment behind it either.
        await using Harness harness = new Harness(
            configure: options => options.Pairs = new[] { Xpm, Bar },
            wrapQuoteStore: inner => new FlakyQuoteStore(inner, failingHash: "BADSAVE", failures: 1));
        harness.Registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(price: 0.01m, ledgerIndex: 900, capturedAt: Now));
        harness.Registry.SetSnapshot(Bar.Key, new StubQuoteSnapshot(price: 0.02m, ledgerIndex: 900, capturedAt: Now));

        await harness.Enqueuer.EnqueueAsync(Payment(hash: "BADSAVE", currency: "XPM", issuer: XpmIssuer), Ct);
        await harness.Enqueuer.EnqueueAsync(Payment(hash: "GOODSAVE", currency: "BAR", issuer: BarIssuer), Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Any(d => d.Valuation.TransactionHash == "GOODSAVE"),
            "the entry behind the failing save to be valued and delivered");

        PaymentValuation? badSave = await harness.QuoteStore.GetValuationAsync("BADSAVE", Ct);
        Assert.NotNull(badSave);
        Assert.Equal(ValuationState.Pending, badSave!.State);

        // And it prices itself on a later pass, once the store stops rejecting the write (FlakyQuoteStore
        // was given exactly one failure to spend).
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Any(d => d.Valuation.TransactionHash == "BADSAVE"),
            "the previously failing entry to be valued once the store accepts the write");
    }

    [Fact]
    public async Task APairRemovedFromConfigurationFailsTheEntryRatherThanLeavingItQueuedForever()
    {
        // The pair was removed from configuration after the payment was queued. Nothing will re-add it
        // behind this worker's back, so the entry is failed rather than retried on a timer forever.
        await using Harness harness = new Harness();
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);

        // Reconfigure the registry to no longer carry Xpm, as if the host redeployed without it. The
        // harness enqueues against the original Xpm-carrying options, so the entry is already queued.
        QuoteRegistry emptyRegistry = new QuoteRegistry(Array.Empty<QuotePair>());
        ValuationWorker worker = new ValuationWorker(
            Microsoft.Extensions.Options.Options.Create(harness.Options),
            harness.QuoteStore,
            harness.PaymentStore,
            emptyRegistry,
            new ScriptedQuoteSource(),
            harness.Handler,
            new FixedTimeProvider(Now),
            NullLogger<ValuationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the failed valuation to be delivered");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        PaymentValuation valuation = harness.Handler.Deliveries[0].Valuation;
        Assert.Equal(ValuationState.Failed, valuation.State);
        Assert.Contains("no longer configured", valuation.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AResolutionRacingAnInFlightDeliveryOfTheStaleFailedContentIsNotLostBehindADeliveredFlag()
    {
        // The race DeliverValuedAsync must survive: it reads a Failed entry, hands its content to the host
        // handler, and while that call is in flight an operator resolves the entry. Marking delivered must
        // be conditional on the row still being Failed — the state actually handed to the handler — or the
        // manual price would be marked delivered on the handler-call's behalf without the host ever having
        // seen it.
        await using Harness harness = new Harness();
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);
        await harness.QuoteStore.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        TaskCompletionSource resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.BeforeReturning = async () =>
        {
            // Only the first delivery call — the one carrying the stale Failed content — races the
            // resolution. Clearing the hook here stops it firing again on the later pass that redelivers
            // the resolved content, where the entry is no longer Failed and a second ValueManuallyAsync
            // call would simply throw.
            harness.Handler.BeforeReturning = null;
            FailedValuationAdmin admin = new FailedValuationAdmin(harness.QuoteStore, new FixedTimeProvider(Now));
            await admin.ValueManuallyAsync("HASH1", rate: 0.02m, Ct).ConfigureAwait(false);
            resolved.TrySetResult();
        };

        await harness.StartAsync();
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The delivery call for the stale Failed content must not have marked the now-resolved row
        // delivered — it must come back around and hand the host the ValuedManually content instead.
        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Any(d => d.Valuation.State == ValuationState.ValuedManually),
            "the manually priced content to reach the handler on a later pass");

        PaymentValuation? stored = await harness.QuoteStore.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(stored);
        Assert.Equal(ValuationState.ValuedManually, stored!.State);
        Assert.True(stored.Delivered);
    }

    [Fact]
    public async Task AManuallyPricedEntryIsDeliveredThroughTheSameNormalDeliveryPassAsAnAutomaticOne()
    {
        // FailedValuationAdmin never calls IPaymentValuedHandler itself: it leaves the resolved row
        // undelivered, exactly as ValuationWorker leaves a freshly computed automatic one, so this worker's
        // own delivery pass is what must pick it up — proving the operator path rides the one delivery
        // mechanism rather than a second one built for it.
        await using Harness harness = new Harness();
        Assert.Equal(42u, await harness.PaymentStore.GetOrAssignTagAsync("buyer-42", Ct));
        await harness.Enqueuer.EnqueueAsync(Payment(), Ct);
        await harness.QuoteStore.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        FailedValuationAdmin admin = new FailedValuationAdmin(harness.QuoteStore, new FixedTimeProvider(Now));
        await admin.ValueManuallyAsync("HASH1", rate: 0.02m, Ct);

        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the manually priced valuation to be delivered");

        (PaymentValuation valuation, string? buyerId) = harness.Handler.Deliveries[0];
        Assert.Equal("buyer-42", buyerId);
        Assert.Equal(ValuationState.ValuedManually, valuation.State);
        Assert.Equal(20m, valuation.QuoteAmount);
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
