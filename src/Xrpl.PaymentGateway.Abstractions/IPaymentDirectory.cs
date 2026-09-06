namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Read-only listings over what a store has recorded, for an operator screen.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IPaymentStore"/> on purpose, and the reason is the same one that kept
/// <see cref="IQuoteStore"/> separate in 1.1.0: a store written against an earlier version must keep
/// compiling. Every method here is a read, and a host that never draws an admin screen needs none of
/// them — so requiring them of every implementation would charge everyone for a feature few use.
/// </para>
/// <para>
/// It is a capability, not a guarantee: a store may implement it or not, and a host discovers which by
/// testing its own store for the interface. The three stores that ship implement it.
/// </para>
/// <para>
/// Nothing in the gateway calls this. The library has no UI and never will — the payment path, the
/// monitor and the valuation worker all get by on point lookups, and these listings exist purely so a
/// host can show a person what is there. That is also why they answer the questions an operator actually
/// asks — whose money is this, did the handler take it, which payments belong to nobody — rather than
/// exposing the storage shape and leaving the host to reassemble them.
/// </para>
/// </remarks>
public interface IPaymentDirectory
{
    /// <summary>
    /// Buyers that have been issued a destination tag, in tag order.
    /// </summary>
    /// <remarks>
    /// Tag order rather than any notion of recency, because tags come from an increasing sequence: tag
    /// order IS issue order, and it is the only ordering here that paging can trust. A new buyer appends
    /// at the end and shifts nobody, so an operator walking page after page sees every row exactly once —
    /// which an ordering that puts new rows at the head cannot promise.
    /// </remarks>
    /// <param name="limit">Page size. Must be positive.</param>
    /// <param name="offset">How many buyers, lowest tag first, to skip. Must not be negative.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BuyerTagPage> ListBuyersAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// Recorded payments, newest first.
    /// </summary>
    /// <remarks>
    /// Newest first because the question an operator opens this with is "what just came in", and unlike
    /// the buyer listing there is no stable ordering to prefer instead: whichever end new rows arrive at,
    /// they arrive. Offsets therefore shift under a busy account, and a payment can repeat or be skipped
    /// across pages — acceptable for a screen someone reads, not a basis for anything that must see every
    /// row. Reconciliation has <see cref="IPaymentStore.GetUnhandledPaymentsAsync"/> for that.
    /// </remarks>
    /// <param name="attribution">Which payments to list. See <see cref="PaymentAttribution"/>.</param>
    /// <param name="limit">Page size. Must be positive.</param>
    /// <param name="offset">How many payments, newest first, to skip. Must not be negative.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RecordedPaymentPage> ListPaymentsAsync(
        PaymentAttribution attribution, int limit, int offset, CancellationToken cancellationToken);
}

/// <summary>Which payments a listing covers, by whether they can be traced to a buyer.</summary>
public enum PaymentAttribution
{
    /// <summary>Every recorded payment.</summary>
    Any = 0,

    /// <summary>Only payments whose destination tag was issued to a buyer.</summary>
    Attributed = 1,

    /// <summary>
    /// Only payments that belong to nobody: no destination tag at all, or a tag this store never issued.
    /// </summary>
    /// <remarks>
    /// The one filter worth having a name. Such a payment is money that arrived and cannot be credited —
    /// the sender is known but is not necessarily the buyer, and there is nothing to attribute it to —
    /// and it is invisible everywhere else: no valuation queue holds it, no balance shows it. Without a
    /// way to list it, an operator learns about it from the person who sent it.
    /// </remarks>
    Unattributed = 2,
}

/// <summary>A buyer and the destination tag issued to them.</summary>
/// <param name="BuyerId">Buyer identifier as the host passed it to <see cref="IPaymentStore.GetOrAssignTagAsync"/>.</param>
/// <param name="DestinationTag">The tag. Never changes once issued.</param>
public readonly record struct BuyerTag(string BuyerId, uint DestinationTag);

/// <summary>One page of buyers.</summary>
public sealed class BuyerTagPage
{
    /// <summary>The buyers in this page, lowest tag first.</summary>
    public required IReadOnlyList<BuyerTag> Items { get; init; }

    /// <summary>How many buyers the store holds in total, not just in this page — what a screen paginates against.</summary>
    public required int TotalCount { get; init; }
}

/// <summary>A recorded payment with what the store knows about it beyond the transaction itself.</summary>
public sealed class RecordedPayment
{
    /// <summary>The payment as it was recorded.</summary>
    public required PaymentRecord Payment { get; init; }

    /// <summary>
    /// The buyer the destination tag was issued to, or null when the payment belongs to nobody.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than left to the caller because doing it per row is a query per row, and the
    /// store can join. Null covers both shapes of "nobody": no tag on the transaction, and a tag that was
    /// never issued.
    /// </remarks>
    public string? BuyerId { get; init; }

    /// <summary>
    /// Whether <see cref="IPaymentStore.MarkHandledAsync"/> has been called for this payment.
    /// </summary>
    /// <remarks>
    /// Means the host's <see cref="IPaymentReceivedHandler"/> accepted delivery, and nothing more: what
    /// the host did with it — credited a balance, priced it, refused it — is the host's own record to
    /// keep. Shown because an unhandled payment that is not recent is the shape a stuck delivery has.
    /// </remarks>
    public required bool Handled { get; init; }
}

/// <summary>One page of recorded payments.</summary>
public sealed class RecordedPaymentPage
{
    /// <summary>The payments in this page, newest first.</summary>
    public required IReadOnlyList<RecordedPayment> Items { get; init; }

    /// <summary>How many payments match the requested attribution in total, not just in this page.</summary>
    public required int TotalCount { get; init; }
}
