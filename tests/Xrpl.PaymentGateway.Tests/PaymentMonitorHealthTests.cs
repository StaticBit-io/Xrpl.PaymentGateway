using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentMonitorHealthTests
{
    private static readonly Uri Node = new Uri("ws://node:6006");

    private sealed class Harness
    {
        public Harness(Action<PaymentGatewayOptions>? configure = null, ValuationEnqueuer? valuationEnqueuer = null)
        {
            Scripted = new ScriptedPaymentStore(Store);
            Options = new PaymentGatewayOptions
            {
                Address = TransactionFixtures.Receiver,
                Nodes = new[] { Node },
                ReconcileWindow = 100,
            };
            configure?.Invoke(Options);

            Health = new PaymentMonitorHealth(
                Microsoft.Extensions.Options.Options.Create(Options),
                Scripted,
                Handler,
                Snapshot,
                Factory,
                NullLogger<PaymentMonitorHealth>.Instance,
                TimeProvider.System,
                valuationEnqueuer);
        }

        public PaymentGatewayOptions Options { get; }

        public InMemoryPaymentStore Store { get; } = new InMemoryPaymentStore();

        public ScriptedPaymentStore Scripted { get; }

        public RecordingHandler Handler { get; } = new RecordingHandler();

        public MonitorSnapshot Snapshot { get; } = new MonitorSnapshot();

        public FakeXrplNodeConnectionFactory Factory { get; } = new FakeXrplNodeConnectionFactory();

        public PaymentMonitorHealth Health { get; }
    }

    private static PaymentRecord Record(string hash) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = null,
        Currency = "XRP",
        Value = 1m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task AStreamingMonitorWithNoLagReportsHealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(999);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsHealthy);
        Assert.Equal(1u, report.LedgerLag);
        Assert.Equal(PaymentMonitorState.Streaming, report.State);
    }

    [Fact]
    public async Task LagBeyondTheThresholdIsNotHealthy()
    {
        Harness harness = new Harness(options => options.MaxAcceptableLedgerLag = 5);
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(900);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(report.IsHealthy);
        Assert.Equal(100u, report.LedgerLag);
    }

    [Fact]
    public async Task AReconnectingMonitorIsNotHealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Reconnecting);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task UnhandledRecordsAreCountedAndReportedAsUnhealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(1000);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);
        await harness.Store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.UnhandledPaymentCount);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task ReconciliationRedeliversWhatTheHandlerNeverGot()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(0);
        await harness.Store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RedeliveredCount);
        Assert.Single(harness.Handler.Deliveries);
        Assert.Empty(await harness.Store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReconciliationFindsAPaymentTheMonitorNeverStored()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-200" };
        connection.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 100,
            LedgerIndexMax = 200,
        });

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RecoveredCount);
        Assert.Single(harness.Store.Snapshot());
        Assert.Single(harness.Handler.Deliveries);
    }

    [Fact]
    public async Task TheSweepOffersAnAlreadyStoredPaymentForValuationRegardless()
    {
        // This is where the "reconciliation re-offers it" recovery for a lost valuation was always meant
        // to live — not on the record path, which must stay cheap for the ordinary case of an
        // already-stored payment during catch-up. The sweep offers every payment it re-reads to the quote
        // store, new or not; the store rejects the duplicate on its own.
        QuotePair pair = new QuotePair("XRP", null, "USD", "rUsdIssuerxxxxxxxxxxxxxxxxxxxxxxxx");
        CountingQuoteStore quoteStore = new CountingQuoteStore();
        ValuationEnqueuer enqueuer = new ValuationEnqueuer(
            Microsoft.Extensions.Options.Options.Create(new QuoteOptions { Pairs = new[] { pair } }),
            quoteStore,
            new QuoteRegistry(new[] { pair }),
            TimeProvider.System,
            NullLogger.Instance);
        Harness harness = new Harness(valuationEnqueuer: enqueuer);
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-200" };
        connection.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 100,
            LedgerIndexMax = 200,
        });

        ReconciliationResult first = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.RecoveredCount);
        Assert.Equal(1, quoteStore.EnqueueAttempts);

        // Second sweep: the payment is already stored this time, so RecoveredCount is 0 — but the quote
        // store must still be offered the payment, which is the whole point of the sweep for valuation.
        connection.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 100,
            LedgerIndexMax = 200,
        });

        ReconciliationResult second = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second.RecoveredCount);
        Assert.Equal(2, quoteStore.EnqueueAttempts);
    }

    [Fact]
    public async Task ASweepThatFindsNothingMissingRecoversNothing()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-200" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RecoveredCount);
        Assert.Empty(result.Errors);
        Assert.False(result.Skipped);
    }

    [Fact]
    public async Task AStoreThatCannotBeReadIsNeverReportedHealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(1000);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);
        harness.Scripted.UnhandledReadFailure = new TimeoutException("store is down");

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        // The unhandled count defaults to zero when the read fails; that must not read as "nothing pending".
        Assert.False(report.IsHealthy);
        Assert.Contains("store is down", report.LastError);
    }

    [Fact]
    public async Task APaymentTheHandlerKeepsRejectingIsNotCountedAsRedelivered()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(0);
        harness.Handler.Throws = true;
        await harness.Store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RedeliveredCount);
        Assert.NotEmpty(result.Errors);
        Assert.Single(await harness.Store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondReconciliationWhileOneIsRunningReportsItselfSkipped()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(0);
        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Scripted.HoldUnhandledReads = gate;

        Task<ReconciliationResult> first = harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        // Wait for the first run to be genuinely inside the store call, not merely unfinished.
        await harness.Scripted.UnhandledReadStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        ReconciliationResult second = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.True(second.Skipped);
        Assert.Equal(0, second.RedeliveredCount);

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task TheSweepStartsAtTheOldestLedgerTheNodeHasRatherThanRefusing()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "180-200" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        // The window asked for 100-200 and the node only has 180 onwards. A sweep proves nothing, so
        // re-reading what does exist beats refusing to look — ledgers 1-32569 are gone from the public
        // network for good, and a fresh standalone stand starts at 2.
        Assert.Empty(result.Errors);
        AccountTransactionQuery query = Assert.Single(connection.Queries);
        Assert.Equal(180u, query.LedgerIndexMin);
        Assert.Equal(200u, query.LedgerIndexMax);
    }

    [Fact]
    public async Task ANodeWhoseHistoryStopsBelowTheWindowIsStillReportedAsAnError()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-50" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        // Clamping the start does not make a node that stops at 50 able to answer for 100-200.
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ANodeThatHoldsNothingAboveTheCursorIsSkippedWithoutQuerying()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 900, CompleteLedgers = "800-900" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        // Its oldest ledger is past the cursor entirely, so there is no window left to sweep.
        Assert.Empty(connection.Queries);
        Assert.Equal(0, result.RecoveredCount);
    }
}

/// <summary>An <see cref="IQuoteStore"/> that counts every <see cref="TryEnqueueValuationAsync"/> call,
/// whatever it returns, so a test can tell an offer was made even when the store itself rejects it as a
/// duplicate. Everything else delegates to a real <see cref="InMemoryQuoteStore"/>.</summary>
public sealed class CountingQuoteStore : IQuoteStore
{
    private readonly InMemoryQuoteStore _inner = new InMemoryQuoteStore();

    public int EnqueueAttempts { get; private set; }

    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) =>
        _inner.SaveQuoteAsync(quote, cancellationToken);

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) =>
        _inner.GetQuoteAsync(pairKey, cancellationToken);

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
        _inner.GetQuotesAsync(cancellationToken);

    public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        EnqueueAttempts++;
        return _inner.TryEnqueueValuationAsync(pending, cancellationToken);
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetPendingValuationsAsync(limit, cancellationToken);

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) =>
        _inner.SaveValuationAsync(valuation, cancellationToken);

    public Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken) =>
        _inner.SaveValuationFailureAsync(transactionHash, reason, failedAt, cancellationToken);

    public Task SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken) =>
        _inner.SaveWriteOffAsync(transactionHash, reason, writtenOffAt, cancellationToken);

    public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken) =>
        _inner.GetFailedValuationsAsync(limit, offset, cancellationToken);

    public Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken) =>
        _inner.CountFailedValuationsAsync(cancellationToken);

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetUndeliveredValuationsAsync(limit, cancellationToken);

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.MarkValuationDeliveredAsync(transactionHash, cancellationToken);

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.GetValuationAsync(transactionHash, cancellationToken);
}
