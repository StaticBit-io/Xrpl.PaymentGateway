using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xrpl.Client;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.Wallet;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// Proves the wiring on a real ledger: a snapshot captured from a live AMM pool, a token payment reaching
/// the queue, and a valuation computed and delivered to the host handler. It says nothing about pricing
/// correctness — that math is the host's, by design; see <see cref="AmmQuoteSource"/>.
/// </summary>
[Trait("Category", "Integration")]
public class QuotedPaymentTests
{
    private const string TokenCode = "TST";

    [Fact]
    public async Task APaymentInATokenIsValuedAgainstAPoolOnTheLedger()
    {
        using XrplClient? node = await StandaloneFixture.TryConnectAsync();
        if (node is null)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
            return;
        }

        // An issuer, a market maker holding the token, a buyer, and the account we watch.
        XrplWallet issuer = await StandaloneFixture.CreateFundedWalletAsync(node, 2000m);
        XrplWallet marketMaker = await StandaloneFixture.CreateFundedWalletAsync(node, 2000m);
        XrplWallet buyer = await StandaloneFixture.CreateFundedWalletAsync(node);
        XrplWallet receiver = await StandaloneFixture.CreateFundedWalletAsync(node);

        await StandaloneFixture.SetDefaultRippleAsync(node, issuer);
        await StandaloneFixture.CreateTrustLineAsync(node, marketMaker, issuer.ClassicAddress, TokenCode, "1000000");
        await StandaloneFixture.CreateTrustLineAsync(node, buyer, issuer.ClassicAddress, TokenCode, "1000000");
        await StandaloneFixture.CreateTrustLineAsync(node, receiver, issuer.ClassicAddress, TokenCode, "1000000");

        await StandaloneFixture.SendIouPaymentAsync(
            node, issuer, marketMaker.ClassicAddress, destinationTag: null, issuer.ClassicAddress, TokenCode, "10000");
        await StandaloneFixture.SendIouPaymentAsync(
            node, issuer, buyer.ClassicAddress, destinationTag: null, issuer.ClassicAddress, TokenCode, "100");

        // 1000 TST against 500 XRP: one token is worth about half an XRP before slippage.
        await StandaloneFixture.CreateAmmAsync(
            node, marketMaker, TokenCode, issuer.ClassicAddress, tokenAmount: 1000m, xrpAmount: 500m);

        RecordingHandler received = new RecordingHandler();
        RecordingValuedHandler valued = new RecordingValuedHandler();
        InMemoryPaymentStore payments = new InMemoryPaymentStore();
        InMemoryQuoteStore quotes = new InMemoryQuoteStore();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(payments);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(received);
        builder.Services.AddSingleton<IQuoteStore>(quotes);
        builder.Services.AddSingleton<IPaymentValuedHandler>(valued);
        builder.Services.AddSingleton<IQuoteSource>(new AmmQuoteSource(node));
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });
        builder.Services.AddXrplPaymentQuotes(options =>
        {
            options.Pairs = new[] { new QuotePair(TokenCode, issuer.ClassicAddress, "XRP", null) };
            options.RefreshInterval = TimeSpan.FromSeconds(10);
            options.ValuationPollInterval = TimeSpan.FromSeconds(1);
        });

        using IHost host = builder.Build();
        IPaymentGateway gateway = host.Services.GetRequiredService<IPaymentGateway>();
        PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(
            "buyer-quoted", TestContext.Current.CancellationToken);

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // A snapshot has to exist before a payment can be priced against one.
            IQuoteReader reader = host.Services.GetRequiredService<IQuoteReader>();
            await TestWait.UntilAsync(
                () => reader.GetPriceAsync(TokenCode, issuer.ClassicAddress, TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult() is not null,
                "the first quote capture",
                timeoutMs: 60000);

            MonitorSnapshot monitorSnapshot = host.Services.GetRequiredService<MonitorSnapshot>();
            await TestWait.UntilAsync(
                () => monitorSnapshot.Read().State == PaymentMonitorState.Streaming,
                "the monitor to reach the streaming state",
                timeoutMs: 30000);

            await StandaloneFixture.SendIouPaymentAsync(
                node, buyer, receiver.ClassicAddress, instructions.DestinationTag, issuer.ClassicAddress, TokenCode, "10");

            await TestWait.UntilAsync(
                () => received.Deliveries.Count == 1, "the payment itself", timeoutMs: 60000);
            await TestWait.UntilAsync(
                () => valued.Deliveries.Count == 1, "the valuation", timeoutMs: 60000);

            (PaymentValuation valuation, string? buyerId) = valued.Deliveries[0];
            Assert.Equal("buyer-quoted", buyerId);
            Assert.Equal(10m, valuation.Amount);
            Assert.True(valuation.IsValued);
            Assert.True(valuation.FullyFilled);
            Assert.NotNull(valuation.SnapshotLedgerIndex);

            // Ten tokens into a 1000/500 pool: a little under five XRP, and strictly less than the
            // marginal price would suggest, because pushing size through a pool costs something.
            Assert.NotNull(valuation.QuoteAmount);
            Assert.InRange(valuation.QuoteAmount.Value, 4m, 5m);
            Assert.True(valuation.SlippagePercent > 0m, "pushing size through a pool must cost something");

            QuoteHealthReport health = await host.Services
                .GetRequiredService<IQuoteHealth>()
                .CheckAsync(TestContext.Current.CancellationToken);
            Assert.True(health.IsHealthy);
            Assert.Equal(1, health.ConfiguredPairs);
            Assert.Equal(1, health.PairsWithFreshQuote);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
