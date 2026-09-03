using System.Collections.Concurrent;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Stands in for whatever a real host does on payment: activate a subscription, release an order,
/// credit a balance. It only logs and remembers, so the sample stays inspectable over HTTP.
/// </summary>
public sealed class SamplePaymentHandler : IPaymentReceivedHandler
{
    private readonly ConcurrentQueue<DeliveredPayment> _delivered = new ConcurrentQueue<DeliveredPayment>();
    private readonly ILogger<SamplePaymentHandler> _logger;
    private readonly QuoteConfiguration _quoteConfiguration;

    public SamplePaymentHandler(ILogger<SamplePaymentHandler> logger, QuoteConfiguration quoteConfiguration)
    {
        _logger = logger;
        _quoteConfiguration = quoteConfiguration;
    }

    public IReadOnlyCollection<DeliveredPayment> Delivered => _delivered.ToArray();

    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "buyer {Buyer} paid {Value} {Currency} (tx {Hash}, tag {Tag}, ledger {Ledger})",
            buyerId ?? "unknown",
            payment.Value,
            payment.Currency,
            payment.TransactionHash,
            payment.DestinationTag,
            payment.LedgerIndex);

        // Already the asset every pair prices into (USD here): no pair exists for it — QuotePair rejects
        // quoting an asset against itself — so no valuation is ever queued for it. That is the library
        // behaving correctly, not a gap; the page uses this flag to show the payment at its own amount
        // right away instead of waiting on a signal that will never arrive.
        bool isQuoteAsset = _quoteConfiguration.IsQuoteAsset(payment.Currency, payment.Issuer);

        _delivered.Enqueue(new DeliveredPayment(
            payment.TransactionHash,
            buyerId,
            payment.Sender,
            payment.DestinationTag,
            payment.Currency,
            payment.Issuer,
            payment.Value,
            payment.LedgerIndex,
            payment.ProcessedAt,
            isQuoteAsset));

        return Task.CompletedTask;
    }
}

/// <summary>What the sample shows for a delivered payment.</summary>
public sealed record DeliveredPayment(
    string TransactionHash,
    string? BuyerId,
    string Sender,
    uint? DestinationTag,
    string Currency,
    string? Issuer,
    decimal Value,
    uint LedgerIndex,
    DateTimeOffset ProcessedAt,
    bool IsQuoteAsset);
