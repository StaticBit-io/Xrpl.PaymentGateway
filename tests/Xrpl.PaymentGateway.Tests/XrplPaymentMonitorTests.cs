using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class XrplPaymentMonitorTests
{
    private static readonly Uri NodeA = new Uri("ws://a:6006");
    private static readonly Uri NodeB = new Uri("ws://b:6006");

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Action<PaymentGatewayOptions>? configure = null, uint firstDestinationTag = 1)
        {
            Store = new InMemoryPaymentStore(firstDestinationTag);
            Options = new PaymentGatewayOptions
            {
                Address = TransactionFixtures.Receiver,
                Nodes = new[] { NodeA, NodeB },
                LedgerStallTimeout = TimeSpan.FromMinutes(5),
                ReconnectBaseDelay = TimeSpan.FromMilliseconds(5),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
                StoreRetryBaseDelay = TimeSpan.FromMilliseconds(5),
                StoreRetryMaxDelay = TimeSpan.FromMilliseconds(20),
            };
            configure?.Invoke(Options);

            Snapshot = new MonitorSnapshot();
            Monitor = new XrplPaymentMonitor(
                Microsoft.Extensions.Options.Options.Create(Options),
                Factory,
                Store,
                Handler,
                Snapshot,
                TimeProvider.System,
                NullLogger<XrplPaymentMonitor>.Instance);
        }

        public PaymentGatewayOptions Options { get; }

        public FakeXrplNodeConnectionFactory Factory { get; } = new FakeXrplNodeConnectionFactory();

        public InMemoryPaymentStore Store { get; }

        public RecordingHandler Handler { get; } = new RecordingHandler();

        public MonitorSnapshot Snapshot { get; }

        public XrplPaymentMonitor Monitor { get; }

        public Task StartAsync() => Monitor.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Monitor.StopAsync(CancellationToken.None);
            Monitor.Dispose();
        }
    }

    [Fact]
    public async Task AFreshStoreStartsAtTheCurrentValidatedLedgerAndDoesNotReplayHistory()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 900, CompleteLedgers = "1-900" };

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        Assert.Equal(TransactionFixtures.Receiver, node.SubscribedAccount);
        Assert.Empty(node.Queries);
        Assert.Equal(900u, harness.Snapshot.Read().Cursor);

        // The starting point must reach the store, not just the in-memory snapshot: a restart before the
        // first ledger close would otherwise pick "current validated" again and skip the interval.
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredCursorBehindTheNetworkTriggersACatchUpAndThenAdvances()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(800, TestContext.Current.CancellationToken);
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 900, CompleteLedgers = "1-900" };
        node.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 801,
            LedgerIndexMax = 900,
        });

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to finish catching up");

        AccountTransactionQuery query = Assert.Single(node.Queries);
        Assert.Equal(801u, query.LedgerIndexMin);
        Assert.Equal(900u, query.LedgerIndexMax);
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
        Assert.Single(harness.Store.Snapshot());
    }

    [Fact]
    public async Task ACatchUpTheNodesCannotProveLeavesTheCursorAloneAndReportsAHistoryGap()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 900,
                CompleteLedgers = "800-900",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.HistoryGap, "the monitor to report a history gap");

        Assert.Equal(100u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFrozenCursorStaysFrozenEvenAsLedgersKeepClosing()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 900,
                CompleteLedgers = "800-900",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.HistoryGap, "the monitor to report a history gap");

        // The live stream keeps running. If the cursor followed it, ledgers 101-900 would be written off as
        // searched and the gap would become permanent and invisible.
        await harness.Factory.For(NodeA).PushLedgerAsync(901);
        await harness.Factory.For(NodeA).PushLedgerAsync(902);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(100u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PaymentMonitorState.HistoryGap, harness.Snapshot.Read().State);
    }

    [Fact]
    public async Task ALiveTransactionIsRecordedAndDeliveredToTheBuyerBehindTheTag()
    {
        // The fixture carries DestinationTag 42, so the store must hand tag 42 to the buyer for the two to meet.
        await using Harness harness = new Harness(firstDestinationTag: 42);
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");
        uint tag = await harness.Store.GetOrAssignTagAsync("buyer-42", TestContext.Current.CancellationToken);
        Assert.Equal(42u, tag);

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the payment to reach the handler");
        Assert.Equal(1m, harness.Handler.Deliveries[0].Payment.Value);
        Assert.Equal("buyer-42", harness.Handler.Deliveries[0].BuyerId);
        Assert.Empty(await harness.Store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSameTransactionArrivingTwiceIsDeliveredOnce()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));
        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count >= 1, "the payment to reach the handler");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(harness.Handler.Deliveries);
    }

    [Fact]
    public async Task AClosedLedgerAdvancesTheCursorToTheLedgerBeforeIt()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // One ledger past the starting point. The margin of one covers the fact that rippled does not
        // guarantee every transaction of ledger N arrives before N's close notification.
        await node.PushLedgerAsync(91);

        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().Cursor == 90u,
            "the cursor to reach 90");
        Assert.Equal(90u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADroppedSessionReconnectsToTheNextNodeInThePool()
    {
        await using Harness harness = new Harness();
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 90,
                CompleteLedgers = "1-90",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the first session to start");

        await harness.Factory.For(NodeA).EndSessionAsync("socket closed");

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to move to the second node");
    }

    [Fact]
    public async Task APaymentThatArrivedWhileTheSocketWasDownIsFoundOnReconnect()
    {
        // The half nobody was proving end to end: a live monitor loses its connection, the network keeps
        // closing ledgers, and a payment lands in one of them. That the reconnect issues a catch-up over
        // the right range, and that a catch-up records what it finds, were each covered on their own.
        await using Harness harness = new Harness(firstDestinationTag: 42);
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 99,
                CompleteLedgers = "1-99",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");
        Assert.Equal(42u, await harness.Store.GetOrAssignTagAsync("buyer-42", TestContext.Current.CancellationToken));
        Assert.Empty(harness.Handler.Deliveries);

        // The network moved on while nothing was listening, and the fixture's payment sits in ledger 100.
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 130,
                CompleteLedgers = "1-130",
            };
        }

        harness.Factory.For(NodeB).EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 100,
            LedgerIndexMax = 130,
        });

        await harness.Factory.For(NodeA).EndSessionAsync("socket closed");

        await TestWait.UntilAsync(
            () => harness.Handler.Deliveries.Count == 1,
            "the payment sent during the outage to reach the handler after the reconnect");

        // The monitor never saw this transaction live: only the catch-up on the new session could find it.
        AccountTransactionQuery query = Assert.Single(harness.Factory.For(NodeB).Queries);
        Assert.Equal(100u, query.LedgerIndexMin);
        Assert.Equal(130u, query.LedgerIndexMax);

        Assert.Equal(1m, harness.Handler.Deliveries[0].Payment.Value);
        Assert.Equal("buyer-42", harness.Handler.Deliveries[0].BuyerId);
        Assert.Equal(130u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStalledOutOfSyncNodeIsAbandonedForTheNextOne()
    {
        await using Harness harness = new Harness(options => options.LedgerStallTimeout = TimeSpan.FromMilliseconds(100));
        harness.Factory.For(NodeA).Status = new NodeStatus
        {
            ServerState = "syncing",
            ValidatedLedgerIndex = 90,
            CompleteLedgers = "1-90",
        };
        harness.Factory.For(NodeB).Status = new NodeStatus
        {
            ServerState = "full",
            ValidatedLedgerIndex = 90,
            CompleteLedgers = "1-90",
        };

        await harness.StartAsync();

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to rotate off the stalled node");
    }

    [Fact]
    public async Task WhenEverySyncedNodeStopsAdvancingTheStallIsBlamedOnTheNetwork()
    {
        await using Harness harness = new Harness(options => options.LedgerStallTimeout = TimeSpan.FromMilliseconds(100));
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 90,
                CompleteLedgers = "1-90",
            };
        }

        await harness.StartAsync();

        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.NetworkStalled, "the monitor to blame the network");
        Assert.NotNull(harness.Factory.For(NodeA).SubscribedAccount);
    }

    [Fact]
    public async Task AnAnomalousPaymentIsCountedButStillDelivered()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.PaymentToUsWithDebit));

        await TestWait.UntilAsync(() => harness.Snapshot.Read().AnomalyCount == 1, "the anomaly to be counted");
        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the payment to still be delivered");
        Assert.Equal(80m, harness.Handler.Deliveries[0].Payment.Value);
    }

    [Fact]
    public async Task ATransactionThatMovesOurBalancesWithoutBeingAPaymentToUsIsIgnored()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.ExchangeWithDebit));
        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.PaymentRipplingThroughUs));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Empty(harness.Handler.Deliveries);
        Assert.Empty(harness.Store.Snapshot());
        Assert.Equal(0, harness.Snapshot.Read().AnomalyCount);
    }
}
