namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Called once a received payment has been priced.
/// </summary>
/// <remarks>
/// <para>
/// Optional in the sense that nothing about the gateway's core payment path depends on it — but once
/// <c>AddXrplPaymentQuotes</c> is called, <c>ValuationWorker</c> requires an implementation to be
/// registered, the same way <see cref="IQuoteSource"/> is required even when
/// <c>ValuateWithFreshSnapshot</c> is off. A host that wants checkout pricing without ever registering
/// this fails to start, not silently drops the callback.
/// </para>
/// <para>
/// This is a second, later signal than <see cref="IPaymentReceivedHandler"/>, never a replacement for it.
/// Waiting for a price before announcing the payment would put liquidity availability on the path of
/// money arriving, which is the one dependency the gateway refuses to create.
/// Delivery is at least once, so implementations must be idempotent.
/// </para>
/// </remarks>
public interface IPaymentValuedHandler
{
    /// <summary>
    /// Hands the host a completed valuation and the buyer it belongs to.
    /// </summary>
    /// <remarks>
    /// The payment record itself is not passed: <see cref="IPaymentStore"/> offers no lookup by hash and
    /// cannot gain one without breaking every implementation written against 1.0.0. The valuation carries
    /// the hash, the amount and the tag, which is what a host needs to find its own row.
    /// </remarks>
    Task OnPaymentValuedAsync(
        PaymentValuation valuation,
        string? buyerId,
        CancellationToken cancellationToken);
}
