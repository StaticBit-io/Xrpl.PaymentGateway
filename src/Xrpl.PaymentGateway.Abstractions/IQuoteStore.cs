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
    /// Up to <paramref name="limit"/> entries in <see cref="ValuationState.Pending"/> for one pair, oldest
    /// enqueued first.
    /// </summary>
    /// <remarks>
    /// Scoped to <paramref name="pairKey"/> so a pair whose snapshot is missing or stale cannot bury
    /// payments on healthy pairs behind it in one shared queue — <c>ValuationWorker</c> decides per pair
    /// whether it can price at all before ever calling this, so a broken pair costs only its own payments a
    /// delay.
    /// </remarks>
    Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One row per pair currently holding at least one <see cref="ValuationState.Pending"/> entry.
    /// </summary>
    /// <remarks>
    /// Two independent uses share this single call rather than each getting a capped, pair-blind read of
    /// their own: <c>ValuationWorker</c> uses the pair keys alone, to notice a pending entry whose pair was
    /// removed from configuration — since it otherwise only ever asks <see cref="GetPendingValuationsAsync"/>
    /// about pairs still configured, and such an entry would never be looked at again — and
    /// <c>QuoteHealth</c> uses <see cref="PendingValuationsByPair.Count"/> and
    /// <see cref="PendingValuationsByPair.OldestEnqueuedAt"/> summed and minimised across every row, for the
    /// true queue depth and age the health report exposes. Neither a page capped at the batch size nor one
    /// scoped to a single pair could answer either question honestly.
    /// </remarks>
    Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/> entry with its
    /// computed valuation — <see cref="PaymentValuation.State"/> on <paramref name="valuation"/> says which
    /// of <see cref="ValuationState.Valued"/> (the automatic pipeline) or <see cref="ValuationState.ValuedManually"/>
    /// (an operator, via <see cref="IUnresolvedValuationAdmin"/>) it is. Moving on from
    /// <see cref="ValuationState.Failed"/> clears <see cref="PaymentValuation.FailedAt"/> and
    /// <see cref="PaymentValuation.FailureReason"/> — the entry is resolved now, not failed and valued at
    /// once — and clears <see cref="PaymentValuation.Delivered"/>, since the resolved content is a new fact
    /// the host has not heard yet even when the row it replaces was already delivered. The implementation
    /// enforces the cleared <see cref="PaymentValuation.Delivered"/> itself rather than trusting
    /// <paramref name="valuation"/>'s own flag — a caller that builds the replacement by copying an existing
    /// delivered row, the natural way to preserve its other fields, must not have that copy's flag survive.
    /// </summary>
    /// <returns>
    /// Whether the write actually applied. False means the row had already moved on from
    /// <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/> by the time this call
    /// reached the store — another operation resolved it first — and nothing was written. A caller that
    /// read the row, decided it could act, and now finds this false must not report success: the decision
    /// was made against a row that no longer exists in the state it was read in.
    /// </returns>
    /// <remarks>
    /// Only ever replaces an entry that is still <see cref="ValuationState.Pending"/> or
    /// <see cref="ValuationState.Failed"/> — an entry already resolved some other way (an operator's
    /// write-off racing this call, say) is left alone rather than silently overwritten.
    /// </remarks>
    Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken);

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
    /// Moves a <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/> entry to
    /// <see cref="ValuationState.WrittenOff"/> for good, at an operator's decision through
    /// <see cref="IUnresolvedValuationAdmin"/>. Also clears <see cref="PaymentValuation.Delivered"/> — the
    /// write-off is a new fact the host has not heard yet even when the row it replaces was already
    /// delivered.
    /// </summary>
    /// <returns>
    /// Whether the write actually applied. False means the row had already moved on from
    /// <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/> by the time this call
    /// reached the store, and nothing was written — see the identical contract on
    /// <see cref="SaveValuationAsync"/>.
    /// </returns>
    Task<bool> SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries in <see cref="ValuationState.Failed"/>, oldest-failed first,
    /// after skipping <paramref name="offset"/> of them. What a health report's failed-entry count is drawn
    /// from; see <see cref="CountFailedValuationsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// How many entries are in <see cref="ValuationState.Failed"/> right now — the count a health report
    /// wants without paging through the whole list.
    /// </summary>
    Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries in <see cref="ValuationState.Pending"/> or
    /// <see cref="ValuationState.Failed"/> whose <see cref="PaymentValuation.EnqueuedAt"/> is at or before
    /// <paramref name="olderThan"/>, oldest-enqueued first, after skipping <paramref name="offset"/> of
    /// them. What <see cref="IUnresolvedValuationAdmin.ListUnresolvedAsync"/> pages through.
    /// </summary>
    /// <remarks>
    /// <see cref="PaymentValuation.EnqueuedAt"/> is the one timestamp every unresolved entry has — a
    /// <see cref="ValuationState.Pending"/> row has no <see cref="PaymentValuation.FailedAt"/> to sort by —
    /// so it is what "stuck" is measured against for both states alike: how long the entry has been in the
    /// pipeline without reaching a resolved or terminal state, not how long since any one state transition.
    /// </remarks>
    Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
        DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// How many entries are in <see cref="ValuationState.Pending"/> or <see cref="ValuationState.Failed"/>
    /// with <see cref="PaymentValuation.EnqueuedAt"/> at or before <paramref name="olderThan"/> right now —
    /// the count <see cref="IUnresolvedValuationAdmin.ListUnresolvedAsync"/> paginates against.
    /// </summary>
    Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> entries past <see cref="ValuationState.Pending"/> that have not
    /// reached the host handler yet, oldest first — <see cref="ValuationState.Valued"/> and
    /// <see cref="ValuationState.ValuedManually"/> alongside <see cref="ValuationState.Failed"/> and
    /// <see cref="ValuationState.WrittenOff"/>: the host learns about all four, not only a successful price.
    /// </summary>
    Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a valuation delivered, but only when it is still in <paramref name="deliveredState"/> — the
    /// state the caller actually handed to the host handler.
    /// </summary>
    /// <remarks>
    /// A precondition, not an unconditional write: without it, an entry an operator resolves — pricing it
    /// manually or writing it off — while a slow handler call for its stale <see cref="ValuationState.Failed"/>
    /// content is still in flight would have that content marked delivered on the operator's behalf, and the
    /// resolution itself would never reach the host at all. Guarding the write against the row having moved
    /// on in the meantime is what leaves it undelivered for the next pass to pick up correctly instead.
    /// </remarks>
    /// <param name="transactionHash">The entry to mark delivered.</param>
    /// <param name="deliveredState">The state that was actually handed to the host handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Whether the mark actually applied. False means the row had already moved on from
    /// <paramref name="deliveredState"/> — the resolution race this method exists to guard against — and
    /// the caller should not treat the delivery as recorded; the next pass hands the resolved content to
    /// the handler instead.
    /// </returns>
    Task<bool> MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken);

    /// <summary>The valuation for one payment, in any state, or null when there is none.</summary>
    Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken);
}

/// <summary>
/// How many <see cref="ValuationState.Pending"/> entries one pair holds, and the earliest of their
/// <see cref="PaymentValuation.EnqueuedAt"/> values. See <see cref="IQuoteStore.GetPendingValuationBreakdownAsync"/>.
/// </summary>
public sealed class PendingValuationsByPair
{
    /// <summary>The pair, from <see cref="QuotePair.Key"/>.</summary>
    public required string PairKey { get; init; }

    /// <summary>How many <see cref="ValuationState.Pending"/> entries this pair holds right now.</summary>
    public required int Count { get; init; }

    /// <summary>The earliest <see cref="PaymentValuation.EnqueuedAt"/> among them.</summary>
    public required DateTimeOffset OldestEnqueuedAt { get; init; }
}
