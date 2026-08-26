using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xrpl.Client;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.Wallet;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

[Trait("Category", "Integration")]
public class EndToEndPaymentTests
{
    [Fact]
    public async Task ATaggedPaymentOnAStandaloneLedgerReachesTheHostHandler()
    {
        using XrplClient? node = await StandaloneFixture.TryConnectAsync();
        if (node is null)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
            return;
        }

        XrplWallet receiver = await StandaloneFixture.CreateFundedWalletAsync(node);
        XrplWallet buyer = await StandaloneFixture.CreateFundedWalletAsync(node);

        RecordingHandler handler = new RecordingHandler();
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(store);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(handler);
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });

        using IHost host = builder.Build();
        IPaymentGateway gateway = host.Services.GetRequiredService<IPaymentGateway>();
        PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            MonitorSnapshot snapshot = host.Services.GetRequiredService<MonitorSnapshot>();
            await TestWait.UntilAsync(
                () => snapshot.Read().State == PaymentMonitorState.Streaming,
                "the monitor to reach the streaming state",
                timeoutMs: 30000);

            await StandaloneFixture.SendTaggedPaymentAsync(
                node, buyer, receiver.ClassicAddress, instructions.DestinationTag, 25m);

            await TestWait.UntilAsync(
                () => handler.Deliveries.Count == 1,
                "the payment to reach the host handler",
                timeoutMs: 60000);

            (PaymentRecord payment, string? buyerId) = handler.Deliveries[0];
            Assert.Equal("buyer-1", buyerId);
            Assert.Equal("XRP", payment.Currency);
            Assert.Equal(25m, payment.Value);
            Assert.Equal(buyer.ClassicAddress, payment.Sender);
            Assert.Equal(instructions.DestinationTag, payment.DestinationTag);
            Assert.Empty(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));

            PaymentMonitorHealthReport report = await host.Services
                .GetRequiredService<IPaymentMonitorHealth>()
                .CheckAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PaymentMonitorState.Streaming, report.State);
            Assert.Equal(0, report.AnomalyCount);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task APaymentMissedWhileDisconnectedIsPickedUpByCatchUp()
    {
        using XrplClient? node = await StandaloneFixture.TryConnectAsync();
        if (node is null)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
            return;
        }

        XrplWallet receiver = await StandaloneFixture.CreateFundedWalletAsync(node);
        XrplWallet buyer = await StandaloneFixture.CreateFundedWalletAsync(node);

        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler();

        // Pin the cursor below the payment, then start: the monitor has never seen the transaction live,
        // so only catch-up can find it.
        uint startLedger = await StandaloneFixture.CurrentValidatedLedgerAsync(node);
        await store.SetLastProcessedLedgerAsync(startLedger, TestContext.Current.CancellationToken);
        uint tag = await store.GetOrAssignTagAsync("buyer-2", TestContext.Current.CancellationToken);

        await StandaloneFixture.SendTaggedPaymentAsync(node, buyer, receiver.ClassicAddress, tag, 7m);
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(store);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(handler);
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await TestWait.UntilAsync(
                () => handler.Deliveries.Count == 1,
                "catch-up to find the payment sent while the monitor was down",
                timeoutMs: 60000);

            Assert.Equal(7m, handler.Deliveries[0].Payment.Value);
            Assert.Equal("buyer-2", handler.Deliveries[0].BuyerId);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
