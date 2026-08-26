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
        public Harness(Action<PaymentGatewayOptions>? configure = null)
        {
            Options = new PaymentGatewayOptions
            {
                Address = TransactionFixtures.Receiver,
                Nodes = new[] { Node },
                ReconcileWindow = 100,
            };
            configure?.Invoke(Options);

            Health = new PaymentMonitorHealth(
                Microsoft.Extensions.Options.Options.Create(Options),
                Store,
                Handler,
                Snapshot,
                Factory,
                NullLogger<PaymentMonitorHealth>.Instance,
                TimeProvider.System);
        }

        public PaymentGatewayOptions Options { get; }

        public InMemoryPaymentStore Store { get; } = new InMemoryPaymentStore();

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
    public async Task ANodeThatCannotProveTheWindowIsReportedAsAnError()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "180-200" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Errors);
    }
}
