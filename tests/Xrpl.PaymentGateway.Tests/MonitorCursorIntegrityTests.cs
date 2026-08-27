using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// The cursor is the boundary below which record completeness is proven. These are the cases where a
/// naive implementation moves it past ledgers nobody ever searched.
/// </summary>
public class MonitorCursorIntegrityTests
{
    private static readonly Uri NodeA = new Uri("ws://a:6006");
    private static readonly Uri NodeB = new Uri("ws://b:6006");

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Action<PaymentGatewayOptions>? configure = null, IPaymentStore? store = null)
        {
            Store = store ?? new InMemoryPaymentStore();
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

        public IPaymentStore Store { get; }

        public RecordingHandler Handler { get; } = new RecordingHandler();

        public MonitorSnapshot Snapshot { get; } = new MonitorSnapshot();

        public XrplPaymentMonitor Monitor { get; }

        public Task StartAsync() => Monitor.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Monitor.StopAsync(CancellationToken.None);
            Monitor.Dispose();
        }
    }

    private static void ConfigureNodes(Harness harness, uint validated, string completeLedgers = "1-100000")
    {
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = validated,
                CompleteLedgers = completeLedgers,
            };
        }
    }

    [Fact]
    public async Task ALedgerStreamThatSkipsAheadEndsTheSessionInsteadOfAdvancingTheCursor()
    {
        await using Harness harness = new Harness();
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // 101 is contiguous with the starting point and may advance the cursor.
        await harness.Factory.For(NodeA).PushLedgerAsync(101);
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().Cursor == 100u, "the contiguous ledger to advance the cursor");

        // 130 skips 102-129. Those ledgers were never searched, so the cursor must not jump to 129.
        await harness.Factory.For(NodeA).PushLedgerAsync(130);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(100u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AJumpMakesTheMonitorReconnectSoTheGapIsReplayedThroughCatchUp()
    {
        await using Harness harness = new Harness();
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // The jump happened because the network itself moved on, so the nodes now report the newer tip.
        ConfigureNodes(harness, validated: 130);
        await harness.Factory.For(NodeA).PushLedgerAsync(130);

        // The next session lands on the other node and replays the skipped span through a verified catch-up.
        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to open a fresh session");
        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).Queries.Count > 0, "the fresh session to run a catch-up");
        Assert.Equal(101u, harness.Factory.For(NodeB).Queries[0].LedgerIndexMin);
        Assert.Equal(130u, harness.Factory.For(NodeB).Queries[0].LedgerIndexMax);
    }

    [Fact]
    public async Task ALedgerAtOrBelowTheBaselineNeitherAdvancesNorTripsTheGapCheck()
    {
        await using Harness harness = new Harness();
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // A frame queued before the catch-up window closed. It proves nothing new, and it must not be
        // mistaken for the stream running backwards.
        await harness.Factory.For(NodeA).PushLedgerAsync(98);
        await harness.Factory.For(NodeA).PushLedgerAsync(101);

        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().Cursor == 100u, "the later contiguous ledger to advance the cursor");
        Assert.Equal(PaymentMonitorState.Streaming, harness.Snapshot.Read().State);
    }

    [Fact]
    public async Task ANodeBehindThePersistedCursorDoesNotLetTheStreamSkipTheDifference()
    {
        await using Harness harness = new Harness();
        // The previous session ran against a node that was further ahead.
        await harness.Store.SetLastProcessedLedgerAsync(109, TestContext.Current.CancellationToken);
        ConfigureNodes(harness, validated: 105);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // The lagging node then applies a batch of ledgers at once.
        await harness.Factory.For(NodeA).PushLedgerAsync(120);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Ledgers 110-119 were never searched by anyone, so the cursor stays where it was proven.
        Assert.Equal(109u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DroppedStreamFramesEndTheSession()
    {
        await using Harness harness = new Harness();
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // The SDK's inbound queue is bounded and discards the oldest frame without raising anything.
        harness.Factory.For(NodeA).DroppedStreamMessages = 5;
        await harness.Factory.For(NodeA).PushLedgerAsync(101);

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null,
            "the monitor to abandon the session that lost frames");
    }

    [Fact]
    public async Task AStartLedgerIndexBeyondTheNetworkIsClampedToTheValidatedLedger()
    {
        await using Harness harness = new Harness(options => options.StartLedgerIndex = 5_000_000);
        ConfigureNodes(harness, validated: 900);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        // Parking the cursor in the future would silently discard every later write and hide the lag.
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoreOutageStopsTheCursorWithoutLosingThePaymentWhenItRecovers()
    {
        InMemoryPaymentStore inner = new InMemoryPaymentStore();
        FlakyPaymentStore store = new FlakyPaymentStore(inner, failures: 3);
        await using Harness harness = new Harness(store: store);
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await harness.Factory.For(NodeA).PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        // The write is retried until the store comes back rather than being dropped.
        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the payment to survive the outage");
        Assert.True(store.AddAttempts > 1, "the failing writes should have been retried");
        Assert.Equal(PaymentMonitorState.Streaming, harness.Snapshot.Read().State);
    }

    [Fact]
    public async Task WhenTheLiveNodeCannotProveTheRangeADedicatedCatchUpNodeIsTried()
    {
        Uri archive = new Uri("ws://archive:6006");
        await using Harness harness = new Harness(options =>
        {
            options.Nodes = new[] { NodeA };
            options.CatchUpNodes = new[] { archive };
        });
        await harness.Store.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);

        harness.Factory.For(NodeA).Status = new NodeStatus
        {
            ServerState = "full",
            ValidatedLedgerIndex = 900,
            CompleteLedgers = "800-900",
        };
        harness.Factory.For(archive).Status = new NodeStatus
        {
            ServerState = "full",
            ValidatedLedgerIndex = 900,
            CompleteLedgers = "1-900",
        };

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming,
            "the full-history node to prove the range the live node could not");

        Assert.NotEmpty(harness.Factory.For(archive).Queries);
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGapStaysFrozenEvenWhenTheNextNodeSitsBelowTheCursor()
    {
        InMemoryPaymentStore inner = new InMemoryPaymentStore();
        await using Harness harness = new Harness(store: inner);
        await inner.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);

        // Nobody can prove 101-900, so the first session freezes the cursor.
        ConfigureNodes(harness, validated: 900, completeLedgers: "800-900");
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.HistoryGap, "the monitor to report a history gap");

        // The next session lands on a node that sits below the cursor, so no catch-up runs at all. Skipping
        // one proves nothing, so the gap must survive rather than being cleared by the absence of work.
        ConfigureNodes(harness, validated: 50, completeLedgers: "1-50");
        await harness.Factory.For(NodeA).EndSessionAsync("socket closed");

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to open a fresh session");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(PaymentMonitorState.HistoryGap, harness.Snapshot.Read().State);
        Assert.Equal(100u, await inner.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OverflowingTheStreamBufferEndsTheSessionRatherThanDroppingEvents()
    {
        ScriptedPaymentStore store = new ScriptedPaymentStore(new InMemoryPaymentStore());
        TaskCompletionSource cursorGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.HoldCursorReads = cursorGate;

        await using Harness harness = new Harness(options => options.StreamBufferCapacity = 2, store: store);
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeA).SubscribedAccount is not null, "the first session to subscribe");

        // The monitor is parked reading the cursor, so nothing is draining the channel yet. More events
        // than the buffer holds must not be silently discarded.
        for (int i = 0; i < 8; i++)
        {
            await harness.Factory.For(NodeA).PushLedgerAsync((ulong)(101 + i));
        }

        cursorGate.SetResult();

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null,
            "the monitor to abandon the session whose buffer overflowed");
    }

    [Fact]
    public async Task RecoveringFromANetworkStallReturnsToStreamingRatherThanStickingStalled()
    {
        await using Harness harness = new Harness(options => options.LedgerStallTimeout = TimeSpan.FromMilliseconds(100));
        ConfigureNodes(harness, validated: 100);

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.NetworkStalled, "the monitor to blame the network");

        await harness.Factory.For(NodeA).PushLedgerAsync(101);

        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to recover once ledgers resume");
    }
}
