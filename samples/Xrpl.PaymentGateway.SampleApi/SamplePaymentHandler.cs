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

    public SamplePaymentHandler(ILogger<SamplePaymentHandler> logger) => _logger = logger;

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

        _delivered.Enqueue(new DeliveredPayment(
            payment.TransactionHash,
            buyerId,
            payment.Sender,
            payment.DestinationTag,
            payment.Currency,
            payment.Issuer,
            payment.Value,
            payment.LedgerIndex,
            payment.ProcessedAt));

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
    DateTimeOffset ProcessedAt);
