namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// What one received payment was worth, and how that number was arrived at.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="PaymentRecord"/> deliberately. The record is a fact off the ledger,
/// reproducible from account_tx and never rewritten; a valuation is our own derived number, computed
/// later and dependent on which snapshot happened to be current. Folding the two together would make an
/// immutable fact mutable.
/// </remarks>
public sealed class PaymentValuation
{
    /// <summary>Hash of the payment this values. Unique key.</summary>
    public required string TransactionHash { get; init; }

    /// <summary>Pair used to value it, from <see cref="QuotePair.Key"/>.</summary>
    public required string PairKey { get; init; }

    /// <summary>Amount received, in the received asset's own units.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Ledger the payment was validated in.</summary>
    public required uint PaymentLedgerIndex { get; init; }

    /// <summary>
    /// Destination tag the payment carried, copied here so the buyer can be resolved without reaching
    /// back into the payment store — which offers no lookup by hash, and cannot gain one without
    /// breaking every 1.0.0 implementation.
    /// </summary>
    public uint? DestinationTag { get; init; }

    /// <summary>When the payment entered the valuation queue.</summary>
    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>
    /// What stage this entry is at. Defaults to <see cref="ValuationState.Pending"/>, which is what a
    /// freshly queued entry is.
    /// </summary>
    public ValuationState State { get; init; } = ValuationState.Pending;

    /// <summary>
    /// When the valuation was computed, automatically or by an operator. Null while queued or failed.
    /// </summary>
    public DateTimeOffset? ValuedAt { get; init; }

    /// <summary>
    /// Value in the quote asset. Null until <see cref="State"/> is <see cref="ValuationState.Valued"/> or
    /// <see cref="ValuationState.ValuedManually"/>.
    /// </summary>
    public decimal? QuoteAmount { get; init; }

    /// <summary>
    /// Price actually achieved for this size. For <see cref="ValuationState.ValuedManually"/> this is the
    /// rate the operator supplied.
    /// </summary>
    public decimal? EffectivePrice { get; init; }

    /// <summary>
    /// Marginal price of the snapshot used. Null for <see cref="ValuationState.ValuedManually"/> — no
    /// snapshot is involved in an operator's price.
    /// </summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>How much worse the achieved price was than the marginal one, in percent.</summary>
    public decimal? SlippagePercent { get; init; }

    /// <summary>
    /// Whether the whole received amount could be traded against the snapshot. Always true for
    /// <see cref="ValuationState.ValuedManually"/> — an operator's rate prices the full recorded amount.
    /// </summary>
    public bool FullyFilled { get; init; }

    /// <summary>Whether the snapshot's book may have run deeper than what was priced.</summary>
    public bool BookTruncated { get; init; }

    /// <summary>Assets the route went through. Null for <see cref="ValuationState.ValuedManually"/>.</summary>
    public string? Route { get; init; }

    /// <summary>
    /// Ledger the snapshot was captured at. This is what makes an old automatic valuation checkable. Null
    /// for <see cref="ValuationState.ValuedManually"/>.
    /// </summary>
    public uint? SnapshotLedgerIndex { get; init; }

    /// <summary>When the snapshot was captured. Null for <see cref="ValuationState.ValuedManually"/>.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; init; }

    /// <summary>
    /// Whether the valuation has reached the host handler. Applies to every non-<see cref="ValuationState.Pending"/>
    /// state: <see cref="IPaymentValuedHandler"/> is handed a <see cref="ValuationState.Failed"/> and a
    /// <see cref="ValuationState.WrittenOff"/> entry too, not only a priced one, so the host can tell the
    /// buyer what happened instead of nothing at all.
    /// </summary>
    public bool Delivered { get; init; }

    /// <summary>
    /// When this entry reached <see cref="ValuationState.Failed"/>. Null otherwise.
    /// </summary>
    /// <remarks>
    /// Reached only for a per-entry, non-transient cause: the pair it names is no longer configured, or
    /// pricing it threw. Everything else that can keep an entry from being valued — no snapshot yet, a
    /// stale one, the snapshot answering "no liquidity right now", or the store rejecting the write — is
    /// transient and shared by every entry against that pair, so none of it fails an entry; the entry simply
    /// stays <see cref="ValuationState.Pending"/> until conditions allow. There is deliberately no retry
    /// counter or backoff behind this: a cause that terminates is, by definition, one another attempt cannot
    /// fix on its own — an operator, through <see cref="IFailedValuationAdmin"/>, is what moves it on from
    /// here.
    /// </remarks>
    public DateTimeOffset? FailedAt { get; init; }

    /// <summary>
    /// Why this entry failed, specific enough for an operator to act on: which cause it was, and the
    /// exception message where the cause was an exception. Null unless <see cref="State"/> is
    /// <see cref="ValuationState.Failed"/> or <see cref="ValuationState.WrittenOff"/> — a write-off keeps
    /// the reason it originally failed for, alongside <see cref="WriteOffReason"/>, the operator's own.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>When an operator moved this entry to <see cref="ValuationState.WrittenOff"/>. Null otherwise.</summary>
    public DateTimeOffset? WrittenOffAt { get; init; }

    /// <summary>
    /// The reason an operator supplied for writing this entry off — dust, a spam token, a mistaken
    /// transfer. Null unless <see cref="State"/> is <see cref="ValuationState.WrittenOff"/>.
    /// </summary>
    public string? WriteOffReason { get; init; }

    /// <summary>Whether this entry is still queued and unpriced.</summary>
    public bool IsPending => State == ValuationState.Pending;

    /// <summary>Whether a value has been computed, automatically or by an operator.</summary>
    public bool IsValued => State is ValuationState.Valued or ValuationState.ValuedManually;

    /// <summary>Whether this entry could not be priced and is waiting on an operator.</summary>
    public bool IsFailed => State == ValuationState.Failed;

    /// <summary>Whether an operator decided this entry will never be priced or credited.</summary>
    public bool IsWrittenOff => State == ValuationState.WrittenOff;
}
