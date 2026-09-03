using System.Globalization;
using Xrpl.Client;
using Xrpl.Models.Common;
using Xrpl.Models.Transactions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.Wallet;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Pays the gateway from a wallet this sample holds the seed for, so the demonstration can be driven from
/// the page instead of from a shell.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <c>Xrpl:Demo:PayerSeed</c> is set, and meant for a standalone stand or a test network: a seed
/// in configuration is a private key in a text file, and nothing here belongs anywhere near an account with
/// value on it.
/// </para>
/// <para>
/// It is a test button, not a transfer facility. The destination is always the address and tag the gateway
/// itself just issued for the buyer being checked out — the caller never names one — and the currency has to
/// be one the sample is configured to accept.
/// </para>
/// </remarks>
public sealed class DemoPayer : IDisposable
{
    /// <summary>
    /// Serialises submissions. Autofill reads the sender's sequence from the <em>current</em> ledger, so
    /// two submissions that overlap inside one four-second ledger would be handed the same number and the
    /// second would be rejected as a past sequence.
    /// </summary>
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private readonly XrplWallet _wallet;
    private readonly string _node;
    private readonly ILogger<DemoPayer> _logger;

    private XrplClient? _client;
    private bool _disposed;

    private DemoPayer(XrplWallet wallet, string node, ILogger<DemoPayer> logger)
    {
        _wallet = wallet;
        _node = node;
        _logger = logger;
    }

    /// <summary>The account the demo pays from. Shown on the page so its balances can be checked.</summary>
    public string Address => _wallet.ClassicAddress;

    /// <summary>
    /// Whether a seed is configured at all. When it is not, nothing below is registered and the sample runs
    /// exactly as it did before — the pay buttons and their endpoints are simply not there.
    /// </summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Xrpl:Demo:PayerSeed"]);

    /// <summary>Builds a payer from <c>Xrpl:Demo:PayerSeed</c>.</summary>
    public static DemoPayer Create(IConfiguration configuration, string node, ILogger<DemoPayer> logger)
    {
        string seed = configuration["Xrpl:Demo:PayerSeed"]
            ?? throw new InvalidOperationException("Xrpl:Demo:PayerSeed is not configured");

        XrplWallet wallet = XrplWallet.FromSeed(seed);
        logger.LogWarning(
            "the demo payer is enabled and will sign payments from {Address} — for a test network only",
            wallet.ClassicAddress);

        return new DemoPayer(wallet, node, logger);
    }

    /// <summary>Submits one payment and reports what the node made of it.</summary>
    /// <remarks>
    /// Returns on provisional acceptance rather than waiting for validation. That is the honest thing to
    /// demonstrate: the page then learns about the payment the same way any host does — from the gateway's
    /// own monitor, once the ledger closes over it — instead of from the submission that caused it.
    /// </remarks>
    public async Task<DemoPaymentResult> PayAsync(
        string destination,
        uint destinationTag,
        string currency,
        string? issuer,
        decimal amount,
        CancellationToken cancellationToken)
    {
        bool isXrp = string.Equals(CurrencyKey.Canonical(currency), "XRP", StringComparison.Ordinal);

        Payment payment = new Payment
        {
            Account = _wallet.ClassicAddress,
            Destination = destination,
            DestinationTag = destinationTag,
            Amount = isXrp
                ? new Currency { CurrencyCode = "XRP", ValueAsXrp = amount }
                : new Currency
                {
                    CurrencyCode = currency,
                    Issuer = issuer,
                    Value = amount.ToString(CultureInfo.InvariantCulture),
                },
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            XrplClient client = await ConnectedClientAsync(cancellationToken).ConfigureAwait(false);
            Submit response = await client.Submit(payment, _wallet, true, false, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "demo payment of {Amount} {Currency} to {Destination} tag {Tag}: {Result}",
                amount, currency, destination, destinationTag, response.EngineResult);

            // Submit.TxJson is declared as object and has no Hash; Submit.Transaction is the computed
            // response that does.
            return new DemoPaymentResult(response.EngineResult, response.Transaction?.Hash);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The sample's own connection, separate from the gateway's: the gateway owns its node connection and
    /// its reconnection policy, and borrowing them for a demo button would be reaching into it.
    /// </summary>
    private async Task<XrplClient> ConnectedClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && _client.IsConnected())
        {
            return _client;
        }

        _client?.Dispose();
        _client = null;

        XrplClient client = new XrplClient(_node);
        await client.Connect(cancellationToken).ConfigureAwait(false);
        _client = client;
        return client;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>What the node said about a submitted demo payment.</summary>
/// <param name="EngineResult">
/// The provisional result — <c>tesSUCCESS</c> means accepted for the next ledger, not yet validated.
/// </param>
/// <param name="TransactionHash">
/// The hash the payment will have once it validates, which is what ties it to the row the gateway
/// eventually reports.
/// </param>
public sealed record DemoPaymentResult(string EngineResult, string? TransactionHash);
