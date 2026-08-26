namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Host code that reacts to a recorded payment. Called after the record is persisted, at least once —
/// implementations must be idempotent. An exception here never blocks recording; the record stays
/// unhandled and reconciliation redelivers it.
/// </summary>
public interface IPaymentReceivedHandler
{
    /// <param name="payment">The recorded payment.</param>
    /// <param name="buyerId">The buyer the destination tag resolved to, or null when it resolved to nobody.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken);
}
