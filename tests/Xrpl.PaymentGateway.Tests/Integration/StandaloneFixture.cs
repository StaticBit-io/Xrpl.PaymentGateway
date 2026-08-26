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
    public const string NodeUrl = "ws://localhost:6006";
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
    public static async Task<XrplWallet> CreateFundedWalletAsync(XrplClient client, decimal xrp = 400m)
    {
        XrplWallet wallet = XrplWallet.Generate();
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

    /// <summary>The node's current validated ledger index.</summary>
    public static async Task<uint> CurrentValidatedLedgerAsync(XrplClient client)
    {
        XrplResponse<ServerInfo> response = await client.ServerInfo(new ServerInfoRequest());
        return (uint)(response.Result.Info.ValidatedLedger?.Sequence ?? 0);
    }
}
