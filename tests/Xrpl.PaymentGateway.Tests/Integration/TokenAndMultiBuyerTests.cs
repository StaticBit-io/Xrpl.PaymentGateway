using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// Payments as they actually arrive: several buyers, and an issued currency alongside XRP. The
/// issued-currency path is the one worth putting on a real ledger — everywhere else it is exercised
/// against metadata written by hand, and the shape rippled produces depends on which side of the trust
/// line the receiving account landed on.
/// </summary>
[Trait("Category", "Integration")]
public class TokenAndMultiBuyerTests : IClassFixture<LedgerWorld>
{
    private readonly LedgerWorld _world;

    public TokenAndMultiBuyerTests(LedgerWorld world) => _world = world;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATokenPaymentIsRecordedWhenTheReceiverIsTheLowSideOfTheTrustLine()
    {
        _world.SkipUnlessAvailable();

        Assert.True(
            _world.ReceiverIsLowSideOf(_world.IssuerWhereReceiverIsLow),
            "the fixture was meant to put the receiver on the low side of this line");

        // Balances on a trust line are written from the low side's perspective, so here the number in the
        // metadata rises as the money arrives and the issuer is named by the line's high end.
        PaymentRecord payment = await PayWithTokenAsync(
            "token-buyer-low",
            _world.BuyerOfLowSideToken,
            _world.IssuerWhereReceiverIsLow,
            "37.5");

        Assert.Equal(LedgerWorld.Currency, payment.Currency);
        Assert.Equal(_world.IssuerWhereReceiverIsLow.ClassicAddress, payment.Issuer);
        Assert.Equal(37.5m, payment.Value);
        Assert.Equal(_world.BuyerOfLowSideToken.ClassicAddress, payment.Sender);
        Assert.Equal("Payment", payment.TransactionType);
    }

    [Fact]
    public async Task ATokenPaymentIsRecordedWhenTheReceiverIsTheHighSideOfTheTrustLine()
    {
        _world.SkipUnlessAvailable();

        Assert.False(
            _world.ReceiverIsLowSideOf(_world.IssuerWhereReceiverIsHigh),
            "the fixture was meant to put the receiver on the high side of this line");

        // The mirror image: the recorded balance goes further negative as the money arrives, and the
        // issuer is named by the line's low end. Reading either one backwards would flip the sign and
        // turn an incoming payment into no payment at all.
        PaymentRecord payment = await PayWithTokenAsync(
            "token-buyer-high",
            _world.BuyerOfHighSideToken,
            _world.IssuerWhereReceiverIsHigh,
            "18.25");

        Assert.Equal(LedgerWorld.Currency, payment.Currency);
        Assert.Equal(_world.IssuerWhereReceiverIsHigh.ClassicAddress, payment.Issuer);
        Assert.Equal(18.25m, payment.Value);
        Assert.Equal(_world.BuyerOfHighSideToken.ClassicAddress, payment.Sender);
    }

    [Fact]
    public async Task XrpAndTokenPaymentsFromDifferentBuyersEachReachTheirOwnBuyer()
    {
        _world.SkipUnlessAvailable();

        PaymentInstructions first = await _world.Gateway.GetPaymentInstructionsAsync("buyer-xrp", Ct);
        PaymentInstructions second = await _world.Gateway.GetPaymentInstructionsAsync("buyer-token", Ct);
        Assert.NotEqual(first.DestinationTag, second.DestinationTag);

        // Both go out before either is waited on, so they land close together and the monitor has to keep
        // them apart by tag rather than by arriving one at a time.
        await StandaloneFixture.SendTaggedPaymentAsync(
            _world.Node, _world.BuyerOfLowSideToken, first.Address, first.DestinationTag, 11m);
        await StandaloneFixture.SendIouPaymentAsync(
            _world.Node,
            _world.BuyerOfHighSideToken,
            second.Address,
            second.DestinationTag,
            _world.IssuerWhereReceiverIsHigh.ClassicAddress,
            LedgerWorld.Currency,
            "22");

        PaymentRecord xrp = await _world.WaitForPaymentAsync("buyer-xrp");
        PaymentRecord token = await _world.WaitForPaymentAsync("buyer-token");

        Assert.Equal("XRP", xrp.Currency);
        Assert.Null(xrp.Issuer);
        Assert.Equal(11m, xrp.Value);
        Assert.Equal(_world.BuyerOfLowSideToken.ClassicAddress, xrp.Sender);

        Assert.Equal(LedgerWorld.Currency, token.Currency);
        Assert.Equal(22m, token.Value);
        Assert.Equal(_world.BuyerOfHighSideToken.ClassicAddress, token.Sender);

        Assert.NotEqual(xrp.TransactionHash, token.TransactionHash);
    }

    [Fact]
    public async Task APaymentWithoutATagIsRecordedWithNoBuyerAttached()
    {
        _world.SkipUnlessAvailable();

        int before = _world.Store.Snapshot().Count;

        await StandaloneFixture.SendIouPaymentAsync(
            _world.Node,
            _world.BuyerOfLowSideToken,
            _world.Receiver.ClassicAddress,
            destinationTag: null,
            _world.IssuerWhereReceiverIsLow.ClassicAddress,
            LedgerWorld.Currency,
            "5");

        await TestWait.UntilAsync(
            () => _world.Store.Snapshot().Count > before,
            "the untagged payment to be recorded",
            timeoutMs: 60000);

        PaymentRecord untagged = _world.Store.Snapshot().Last(p => p.DestinationTag is null);

        // The money is recorded because it arrived; it simply belongs to nobody the gateway knows.
        Assert.Equal(5m, untagged.Value);
        Assert.Null(untagged.DestinationTag);
        Assert.Contains(
            _world.Handler.Deliveries,
            d => d.Payment.TransactionHash == untagged.TransactionHash && d.BuyerId is null);
    }

    [Fact]
    public async Task NothingIsRecordedTwiceHoweverManyPaymentsArrive()
    {
        _world.SkipUnlessAvailable();

        IReadOnlyList<PaymentRecord> recorded = _world.Store.Snapshot();

        // The tests above all pay the same account, so by now there are several. Each is keyed by its
        // transaction hash, and a duplicate would mean a buyer credited twice.
        Assert.Equal(recorded.Count, recorded.Select(p => p.TransactionHash).Distinct().Count());
        Assert.All(recorded, p => Assert.NotEqual(0u, p.LedgerIndex));

        PaymentMonitorHealthReport report = await _world.Health.CheckAsync(Ct);
        Assert.Equal(0, report.AnomalyCount);
        Assert.Equal(PaymentMonitorState.Streaming, report.State);
    }

    private async Task<PaymentRecord> PayWithTokenAsync(
        string buyerId,
        Xrpl.Wallet.XrplWallet buyer,
        Xrpl.Wallet.XrplWallet issuer,
        string value)
    {
        PaymentInstructions instructions = await _world.Gateway.GetPaymentInstructionsAsync(buyerId, Ct);

        await StandaloneFixture.SendIouPaymentAsync(
            _world.Node,
            buyer,
            instructions.Address,
            instructions.DestinationTag,
            issuer.ClassicAddress,
            LedgerWorld.Currency,
            value);

        PaymentRecord payment = await _world.WaitForPaymentAsync(buyerId);
        Assert.Equal(instructions.DestinationTag, payment.DestinationTag);
        return payment;
    }
}
