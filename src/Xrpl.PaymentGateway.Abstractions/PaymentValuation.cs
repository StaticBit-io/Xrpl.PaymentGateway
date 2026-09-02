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

    /// <summary>When the valuation was computed. Null while it is still queued.</summary>
    public DateTimeOffset? ValuedAt { get; init; }

    /// <summary>Value in the quote asset. Null while queued.</summary>
    public decimal? QuoteAmount { get; init; }

    /// <summary>Price actually achieved for this size.</summary>
    public decimal? EffectivePrice { get; init; }

    /// <summary>Marginal price of the snapshot used.</summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>How much worse the achieved price was than the marginal one, in percent.</summary>
    public decimal? SlippagePercent { get; init; }

    /// <summary>Whether the whole received amount could be traded against the snapshot.</summary>
    public bool FullyFilled { get; init; }

    /// <summary>Whether the snapshot's book may have run deeper than what was priced.</summary>
    public bool BookTruncated { get; init; }

    /// <summary>Assets the route went through.</summary>
    public string? Route { get; init; }

    /// <summary>Ledger the snapshot was captured at. This is what makes an old valuation checkable.</summary>
    public uint? SnapshotLedgerIndex { get; init; }

    /// <summary>When the snapshot was captured.</summary>
    public DateTimeOffset? SnapshotCapturedAt { get; init; }

    /// <summary>Whether the valuation has reached the host handler.</summary>
    public bool Delivered { get; init; }

    /// <summary>Whether a value has been computed yet.</summary>
    public bool IsValued => ValuedAt is not null;
}
