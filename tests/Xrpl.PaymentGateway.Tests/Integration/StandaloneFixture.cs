using System.Globalization;
using Xrpl.Client;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// Talks to the standalone rippled from .ci-config. The stand closes a ledger every four seconds through
/// its ledger-acceptor container, so the waits here are for validation, not for the node to be prodded.
/// </summary>
public static class StandaloneFixture
{
    /// <summary>
    /// The stand's admin WebSocket. Override with <c>XRPLPG_NODE_URL</c> to run this repository's stand
    /// beside another project's on the same machine; the default is what CI and the Compose file use.
    /// </summary>
    public static readonly string NodeUrl =
        Environment.GetEnvironmentVariable("XRPLPG_NODE_URL") ?? "ws://localhost:6006";
    public const string MasterAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    public const string MasterSecret = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

    /// <summary>Serialises master-account funding: concurrent submissions would collide on its sequence.</summary>
    private static readonly SemaphoreSlim FundingGate = new SemaphoreSlim(1, 1);

    /// <summary>Connects, or returns null when no stand is running so the test can skip.</summary>
    public static async Task<XrplClient?> TryConnectAsync()
    {
        XrplClient client = new XrplClient(
            NodeUrl,
            new XrplClient.ClientOptions
            {
                MaxReconnectAttempts = 1,
                StopAfterMaxAttempts = true,
                ConnectionAttemptTimeout = TimeSpan.FromSeconds(5),
                RequestPolicy = RequestFailurePolicy.ImmediateFail,
            });

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.Connect(cts.Token);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>Creates a funded account and waits until the ledger has validated it.</summary>
    public static Task<XrplWallet> CreateFundedWalletAsync(XrplClient client, decimal xrp = 400m) =>
        FundWalletAsync(client, XrplWallet.Generate(), xrp);

    /// <summary>
    /// Funds an account the caller already chose. Separate from generating one because a test may need an
    /// address with a particular property — which side of a trust line it will end up on, say.
    /// </summary>
    public static async Task<XrplWallet> FundWalletAsync(XrplClient client, XrplWallet wallet, decimal xrp = 400m)
    {
        XrplWallet master = XrplWallet.FromSeed(MasterSecret);

        Payment funding = new Payment
        {
            Account = MasterAccount,
            Destination = wallet.ClassicAddress,
            Amount = new Currency { CurrencyCode = "XRP", ValueAsXrp = xrp },
        };

        await FundingGate.WaitAsync();
        try
        {
            Submit response = await client.Submit(funding, master, true);
            if (response.EngineResult != "tesSUCCESS")
            {
                throw new InvalidOperationException($"funding {wallet.ClassicAddress} failed with {response.EngineResult}");
            }
        }
        finally
        {
            FundingGate.Release();
        }

        await WaitUntilFundedAsync(client, wallet.ClassicAddress);
        return wallet;
    }

    /// <summary>Polls until the account exists in a validated ledger.</summary>
    public static async Task WaitUntilFundedAsync(XrplClient client, string address, int timeoutSeconds = 40)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                decimal balance = await client.GetXrpFreeBalance(address);
                if (balance > 0m)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // The account is not in a validated ledger yet.
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"account {address} was not validated within {timeoutSeconds} seconds");
    }

    /// <summary>Sends XRP with a destination tag and waits only for provisional acceptance.</summary>
    public static async Task<string> SendTaggedPaymentAsync(
        XrplClient client,
        XrplWallet from,
        string destination,
        uint destinationTag,
        decimal xrp)
    {
        Payment payment = new Payment
        {
            Account = from.ClassicAddress,
            Destination = destination,
            DestinationTag = destinationTag,
            Amount = new Currency { CurrencyCode = "XRP", ValueAsXrp = xrp },
        };

        Submit response = await client.Submit(payment, from, true);
        if (response.EngineResult != "tesSUCCESS")
        {
            throw new InvalidOperationException($"payment failed with {response.EngineResult}");
        }

        // Submit.TxJson is declared as object, so it has no Hash. Submit.Transaction is the computed
        // ITransactionResponse that does.
        return response.Transaction?.Hash ?? string.Empty;
    }

    /// <summary>
    /// Lets the issuer's token move between two holders. Without it a token can only be sent back to the
    /// issuer, and a buyer paying a merchant is a move between two holders.
    /// </summary>
    public static async Task SetDefaultRippleAsync(XrplClient client, XrplWallet issuer)
    {
        AccountSet enableRippling = new AccountSet
        {
            Account = issuer.ClassicAddress,
            SetFlag = AccountSetAsfFlags.asfDefaultRipple,
        };

        await SubmitAndWaitAsync(client, enableRippling, issuer, "enabling DefaultRipple");
    }

    /// <summary>Opens a trust line from <paramref name="holder"/> to an issuer for one currency.</summary>
    public static async Task CreateTrustLineAsync(
        XrplClient client,
        XrplWallet holder,
        string issuer,
        string currency,
        string limit)
    {
        TrustSet trust = new TrustSet
        {
            Account = holder.ClassicAddress,
            LimitAmount = new Currency { CurrencyCode = currency, Issuer = issuer, Value = limit },
        };

        await SubmitAndWaitAsync(client, trust, holder, $"opening a {currency} trust line for {holder.ClassicAddress}");
    }

    /// <summary>Sends an issued currency, optionally tagged.</summary>
    public static async Task SendIouPaymentAsync(
        XrplClient client,
        XrplWallet from,
        string destination,
        uint? destinationTag,
        string issuer,
        string currency,
        string value)
    {
        Payment payment = new Payment
        {
            Account = from.ClassicAddress,
            Destination = destination,
            DestinationTag = destinationTag,
            Amount = new Currency { CurrencyCode = currency, Issuer = issuer, Value = value },
        };

        await SubmitAndWaitAsync(client, payment, from, $"sending {value} {currency} to {destination}");
    }

    /// <summary>
    /// Submits and waits for the ledger to close over it. Every step of building a token economy depends
    /// on the previous one being validated, and the stand closes a ledger every four seconds.
    /// </summary>
    private static async Task SubmitAndWaitAsync(
        XrplClient client,
        ITransactionRequest transaction,
        XrplWallet wallet,
        string what)
    {
        Submit response = await client.Submit(transaction, wallet, true);
        if (response.EngineResult != "tesSUCCESS")
        {
            throw new InvalidOperationException($"{what} failed with {response.EngineResult}");
        }

        await Task.Delay(TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// Creates an AMM pool holding an issued currency and XRP, funded by <paramref name="creator"/>.
    /// </summary>
    /// <remarks>The creator must already hold the token; AMMCreate deposits both sides.</remarks>
    public static async Task CreateAmmAsync(
        XrplClient client,
        XrplWallet creator,
        string currency,
        string issuer,
        decimal tokenAmount,
        decimal xrpAmount,
        uint tradingFee = 500)
    {
        AMMCreate create = new AMMCreate
        {
            Account = creator.ClassicAddress,
            Amount = new Currency
            {
                CurrencyCode = currency,
                Issuer = issuer,
                Value = tokenAmount.ToString(CultureInfo.InvariantCulture),
            },
            Amount2 = new Currency { ValueAsXrp = xrpAmount },
            TradingFee = tradingFee,
        };

        await SubmitAndWaitAsync(client, create, creator, $"creating an AMM pool for {currency}/{issuer}");
    }

    /// <summary>The node's current validated ledger index.</summary>
    public static async Task<uint> CurrentValidatedLedgerAsync(XrplClient client)
    {
        XrplResponse<ServerInfo> response = await client.ServerInfo(new ServerInfoRequest());
        return (uint)(response.Result.Info.ValidatedLedger?.Sequence ?? 0);
    }
}
