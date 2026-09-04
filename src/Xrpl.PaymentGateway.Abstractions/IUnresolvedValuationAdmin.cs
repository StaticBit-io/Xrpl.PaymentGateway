namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// The operator path for a valuation the automatic pipeline has not resolved.
/// </summary>
/// <remarks>
/// <para>
/// This library has no UI and never will; a host draws whatever admin screen it wants and calls through
/// here. Both write operations act on an entry that is still <see cref="ValuationState.Pending"/> or
/// <see cref="ValuationState.Failed"/> — there is no reliable way to tell, from a single exception or a
/// single null result, whether a given cause is one an automatic retry could ever clear, so the pipeline
/// does not try to classify that: an entry either resolves itself or it sits, and an operator is the one
/// who decides when "sits" has gone on long enough to act on. Both operations leave the resolved entry
/// undelivered, exactly as an automatic valuation does the moment it is computed: the same
/// <c>ValuationWorker</c> delivery pass that retries a stuck automatic delivery picks these up on its next
/// poll and hands them to <see cref="IPaymentValuedHandler"/>, so crediting a manually priced or written-off
/// payment runs through the one code path the host already trusts, rather than a second one built for this.
/// </para>
/// </remarks>
public interface IUnresolvedValuationAdmin
{
    /// <summary>
    /// Unresolved valuations — <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/>,
    /// not <see cref="ValuationState.Valued"/>, <see cref="ValuationState.ValuedManually"/> or
    /// <see cref="ValuationState.WrittenOff"/> — for an operator to page through and act on.
    /// </summary>
    /// <param name="limit">Page size.</param>
    /// <param name="offset">How many unresolved entries, oldest-enqueued first, to skip.</param>
    /// <param name="minAge">
    /// How long an entry must have sat unresolved to be listed — <see cref="PaymentValuation.EnqueuedAt"/>
    /// at or before now minus this. Null defaults to 15 minutes: long enough that a payment still working
    /// through an ordinary transient wait — no snapshot captured yet, a momentary store hiccup — does not
    /// show up as something to act on, short enough that a genuinely stuck entry does not sit unnoticed for
    /// long. Pass <see cref="TimeSpan.Zero"/> to see every unresolved entry regardless of age.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UnresolvedValuationPage> ListUnresolvedAsync(
        int limit, int offset, TimeSpan? minAge, CancellationToken cancellationToken);

    /// <summary>
    /// Prices an unresolved entry manually: the recorded amount at <paramref name="rate"/>, moved to
    /// <see cref="ValuationState.ValuedManually"/> and left for the normal delivery pass to hand to
    /// <see cref="IPaymentValuedHandler"/>.
    /// </summary>
    /// <param name="transactionHash">The unresolved entry to price.</param>
    /// <param name="rate">Quote-asset units per unit of the received asset. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No entry exists for <paramref name="transactionHash"/>, it is not currently
    /// <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/>, or it moved on to some
    /// other state between being read and being written — another operation resolved it first.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is zero or negative.</exception>
    Task ValueManuallyAsync(string transactionHash, decimal rate, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an unresolved entry off: no quote amount, moved to <see cref="ValuationState.WrittenOff"/> and
    /// left for the normal delivery pass to tell <see cref="IPaymentValuedHandler"/> the case is closed.
    /// </summary>
    /// <param name="transactionHash">The unresolved entry to write off.</param>
    /// <param name="reason">Why — dust, a spam token, a mistaken transfer. Kept for the record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No entry exists for <paramref name="transactionHash"/>, it is not currently
    /// <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/>, or it moved on to some
    /// other state between being read and being written — another operation resolved it first.
    /// </exception>
    Task WriteOffAsync(string transactionHash, string reason, CancellationToken cancellationToken);
}

/// <summary>One page of unresolved valuations.</summary>
public sealed class UnresolvedValuationPage
{
    /// <summary>The entries in this page.</summary>
    public required IReadOnlyList<PaymentValuation> Items { get; init; }

    /// <summary>Total unresolved entries at or past the requested age, not just those in this page — what an admin screen paginates against.</summary>
    public required int TotalCount { get; init; }
}
