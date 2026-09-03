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
/// <para>
/// A <c>valuation</c> here is not always priced. Its <see cref="PaymentValuation.State"/> can
/// be <see cref="ValuationState.Failed"/> — the automatic pipeline could not price it and it now waits on
/// an operator, see <see cref="IUnresolvedValuationAdmin"/> — or <see cref="ValuationState.WrittenOff"/> — an
/// operator looked at it and decided it will never be credited. Both carry
/// <see cref="PaymentValuation.FailureReason"/> and no <see cref="PaymentValuation.QuoteAmount"/>. This is
/// how a host learns to tell the buyer that funds arrived but could not be valued (<c>Failed</c>) or that
/// the case has been closed (<c>WrittenOff</c>), instead of leaving the buyer with no news at all. Only
/// <see cref="ValuationState.Valued"/> and <see cref="ValuationState.ValuedManually"/> carry a real number.
/// </para>
/// </remarks>
public interface IPaymentValuedHandler
{
    /// <summary>
    /// Hands the host a completed, failed, or written-off valuation and the buyer it belongs to.
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
