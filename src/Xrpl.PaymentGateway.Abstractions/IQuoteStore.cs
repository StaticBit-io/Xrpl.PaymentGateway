namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Persistence for quotes and valuations. Separate from <see cref="IPaymentStore"/> on purpose.
/// </summary>
/// <remarks>
/// <para>
/// Adding these methods to <see cref="IPaymentStore"/> would break every implementation written against
/// 1.0.0. Keeping them apart also keeps them optional: a host with no interest in prices registers no
/// quote store and nothing about its gateway changes.
/// </para>
/// <para>
/// One hard requirement: <see cref="TryEnqueueValuationAsync"/> must enforce uniqueness of
/// <see cref="PaymentValuation.TransactionHash"/> and return false on a duplicate rather than throwing.
/// The same payment is offered to the queue by live processing, by catch-up and by reconciliation.
/// </para>
/// </remarks>
public interface IQuoteStore
{
    /// <summary>Writes the latest reading for a pair, replacing any previous one.</summary>
    Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken);

    /// <summary>The stored reading for a pair, or null when the pair has never been refreshed.</summary>
    Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken);

    /// <summary>Every stored reading.</summary>
    Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken);

    /// <summary>Queues a payment for valuation. Returns false when the hash is already queued or resolved.</summary>
    Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries in <see cref="ValuationState.Pending"/>, oldest enqueued
    /// first.
    /// </summary>
    Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/> entry with its
    /// computed valuation — <see cref="PaymentValuation.State"/> on <paramref name="valuation"/> says which
    /// of <see cref="ValuationState.Valued"/> (the automatic pipeline) or <see cref="ValuationState.ValuedManually"/>
    /// (an operator, via <see cref="IFailedValuationAdmin"/>) it is. Moving on from
    /// <see cref="ValuationState.Failed"/> clears <see cref="PaymentValuation.FailedAt"/> and
    /// <see cref="PaymentValuation.FailureReason"/> — the entry is resolved now, not failed and valued at
    /// once.
    /// </summary>
    Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a <see cref="ValuationState.Pending"/> entry to <see cref="ValuationState.Failed"/> for good —
    /// it leaves the pending queue and is never retried automatically. Reserved for a per-entry,
    /// non-transient cause; see <see cref="PaymentValuation.FailedAt"/>.
    /// </summary>
    /// <param name="transactionHash">The pending entry to fail.</param>
    /// <param name="reason">
    /// Specific enough for an operator to act on: which cause it was, and the exception message where the
    /// cause was an exception.
    /// </param>
    /// <param name="failedAt">When it failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a <see cref="ValuationState.Failed"/> entry to <see cref="ValuationState.WrittenOff"/> for
    /// good, at an operator's decision through <see cref="IFailedValuationAdmin"/>.
    /// </summary>
    Task SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries in <see cref="ValuationState.Failed"/>, oldest-failed first,
    /// after skipping <paramref name="offset"/> of them. What <see cref="IFailedValuationAdmin.ListFailedAsync"/>
    /// pages through.
    /// </summary>
    Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// How many entries are in <see cref="ValuationState.Failed"/> right now — the count an admin screen or
    /// a health report wants without paging through the whole list.
    /// </summary>
    Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries past <see cref="ValuationState.Pending"/> that have not
    /// reached the host handler yet, oldest first — <see cref="ValuationState.Valued"/> and
    /// <see cref="ValuationState.ValuedManually"/> alongside <see cref="ValuationState.Failed"/> and
    /// <see cref="ValuationState.WrittenOff"/>: the host learns about all four, not only a successful price.
    /// </summary>
    Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Marks a valuation as delivered to the host handler.</summary>
    Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken);

    /// <summary>The valuation for one payment, in any state, or null when there is none.</summary>
    Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken);
}
