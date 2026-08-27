using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xrpl.AddressCodec;
using Xrpl.Client;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.Wallet;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// A small economy on the standalone stand, built once and shared by the tests that spend it: a receiving
/// account with a running monitor, two token issuers, and two buyers holding XRP and one token each.
/// </summary>
/// <remarks>
/// <para>
/// There are two issuers on purpose. A trust line's two sides are named low and high by comparing the
/// accounts' 160-bit ids, and the balance on it is written from the low side's perspective — so whether
/// the receiving account is the low or the high side flips the sign of the delta and swaps which end of
/// the line names the issuer. Addresses are random, so one issuer would test whichever case the dice
/// gave. These two are generated until one sits either side of the receiver, and both get exercised.
/// </para>
/// <para>
/// Setup costs a minute and a half of closed ledgers, which is why it is a fixture rather than per-test
/// work. When no stand is reachable, <see cref="Available"/> stays false and every test skips itself
/// instead of failing — the suite has to be runnable without Docker.
/// </para>
/// </remarks>
public sealed class LedgerWorld : IAsyncLifetime
{
    public const string Currency = "USD";

    private XrplClient? _node;
    private IHost? _host;

    /// <summary>False when no standalone node answered, which turns every test in the class into a skip.</summary>
    public bool Available { get; private set; }

    public XrplClient Node => _node ?? throw new InvalidOperationException("no ledger");

    public XrplWallet Receiver { get; private set; } = null!;

    /// <summary>Its id sorts above the receiver's, so on their shared line the receiver is the low side.</summary>
    public XrplWallet IssuerWhereReceiverIsLow { get; private set; } = null!;

    /// <summary>Its id sorts below the receiver's, so on their shared line the receiver is the high side.</summary>
    public XrplWallet IssuerWhereReceiverIsHigh { get; private set; } = null!;

    /// <summary>Holds the token whose line makes the receiver the low side.</summary>
    public XrplWallet BuyerOfLowSideToken { get; private set; } = null!;

    /// <summary>Holds the token whose line makes the receiver the high side.</summary>
    public XrplWallet BuyerOfHighSideToken { get; private set; } = null!;

    public InMemoryPaymentStore Store { get; } = new InMemoryPaymentStore();

    public RecordingHandler Handler { get; } = new RecordingHandler();

    public IPaymentGateway Gateway => _host!.Services.GetRequiredService<IPaymentGateway>();

    public IPaymentMonitorHealth Health => _host!.Services.GetRequiredService<IPaymentMonitorHealth>();

    public async ValueTask InitializeAsync()
    {
        _node = await StandaloneFixture.TryConnectAsync();
        if (_node is null)
        {
            return;
        }

        Receiver = await StandaloneFixture.CreateFundedWalletAsync(_node);

        IssuerWhereReceiverIsLow = await FundIssuerAsync(sortsAboveReceiver: true);
        IssuerWhereReceiverIsHigh = await FundIssuerAsync(sortsAboveReceiver: false);

        BuyerOfLowSideToken = await StandaloneFixture.CreateFundedWalletAsync(_node);
        BuyerOfHighSideToken = await StandaloneFixture.CreateFundedWalletAsync(_node);

        await SetUpTokenAsync(IssuerWhereReceiverIsLow, BuyerOfLowSideToken);
        await SetUpTokenAsync(IssuerWhereReceiverIsHigh, BuyerOfHighSideToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(Store);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(Handler);
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = Receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });

        _host = builder.Build();
        await _host.StartAsync();

        Available = true;
    }

    /// <summary>
    /// Generates until the account falls on the wanted side of the receiver, then funds it. Generating is
    /// free; only the funding touches the ledger.
    /// </summary>
    private async Task<XrplWallet> FundIssuerAsync(bool sortsAboveReceiver)
    {
        byte[] receiverId = XrplCodec.DecodeAccountID(Receiver.ClassicAddress);

        XrplWallet candidate;
        do
        {
            candidate = XrplWallet.Generate();
        }
        while (CompareAccountIds(XrplCodec.DecodeAccountID(candidate.ClassicAddress), receiverId) > 0 != sortsAboveReceiver);

        return await StandaloneFixture.FundWalletAsync(_node!, candidate);
    }

    /// <summary>Unsigned, byte by byte, which is how the protocol orders the two sides of a trust line.</summary>
    private static int CompareAccountIds(byte[] left, byte[] right)
    {
        for (int i = 0; i < left.Length && i < right.Length; i++)
        {
            if (left[i] != right[i])
            {
                return left[i] < right[i] ? -1 : 1;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Makes the issuer's token transferable, opens both lines to it, and puts some in the buyer's hands.</summary>
    private async Task SetUpTokenAsync(XrplWallet issuer, XrplWallet buyer)
    {
        // Without DefaultRipple the token cannot move between two holders, only back to the issuer — and
        // a buyer paying a merchant is a move between two holders.
        await StandaloneFixture.SetDefaultRippleAsync(_node!, issuer);
        await StandaloneFixture.CreateTrustLineAsync(_node!, Receiver, issuer.ClassicAddress, Currency, "1000000");
        await StandaloneFixture.CreateTrustLineAsync(_node!, buyer, issuer.ClassicAddress, Currency, "1000000");
        await StandaloneFixture.SendIouPaymentAsync(
            _node!, issuer, buyer.ClassicAddress, destinationTag: null, issuer.ClassicAddress, Currency, "500");
    }

    /// <summary>
    /// Which side of the shared trust line the receiving account sits on. Exposed so a test can assert
    /// the orientation it claims to cover: a mistake in the id comparison above would otherwise leave
    /// both token tests quietly exercising the same case.
    /// </summary>
    public bool ReceiverIsLowSideOf(XrplWallet issuer) =>
        CompareAccountIds(
            XrplCodec.DecodeAccountID(Receiver.ClassicAddress),
            XrplCodec.DecodeAccountID(issuer.ClassicAddress)) < 0;

    /// <summary>Skips the calling test when no stand is running, rather than failing it.</summary>
    public void SkipUnlessAvailable()
    {
        if (!Available)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
        }
    }

    /// <summary>Waits for a payment to reach the handler for one buyer, and returns it.</summary>
    public async Task<PaymentRecord> WaitForPaymentAsync(string buyerId, int timeoutMs = 60000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach ((PaymentRecord payment, string? delivered) in Handler.Deliveries)
            {
                if (delivered == buyerId)
                {
                    return payment;
                }
            }

            await Task.Delay(500);
        }

        Assert.Fail($"no payment reached the handler for {buyerId} within {timeoutMs} ms");
        throw new InvalidOperationException("unreachable");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _node?.Dispose();
    }
}
