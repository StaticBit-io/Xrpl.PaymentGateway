namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>What stage a <see cref="PaymentValuation"/> is at.</summary>
/// <remarks>
/// There is deliberately no "none" member: a valuation row always has a state once it exists, and a
/// payment with no row at all is represented by a null <see cref="PaymentValuation"/>, not by a
/// zero-valued state on one that does not exist.
/// </remarks>
public enum ValuationState
{
    /// <summary>
    /// Queued, not yet priced. Only the transient causes leave an entry here: no snapshot has been
    /// captured for the pair yet, or the held one is past its age limit. Both cure themselves — the entry
    /// prices itself as soon as a usable snapshot exists — so nothing here is ever retried on a timer or
    /// counted against it.
    /// </summary>
    Pending,

    /// <summary>Priced by the automatic pipeline from a liquidity snapshot.</summary>
    Valued,

    /// <summary>Priced by an operator, through <see cref="IFailedValuationAdmin"/>, at a rate they supplied.</summary>
    ValuedManually,

    /// <summary>
    /// Terminal. Reached only for a per-entry, non-transient cause: the pair is no longer configured,
    /// pricing it threw, the pair currently holds no liquidity to price against, or the store rejected this
    /// specific row on save. Never retried automatically — see <see cref="IFailedValuationAdmin"/> for the
    /// operator path this state waits on.
    /// </summary>
    Failed,

    /// <summary>
    /// Terminal. An operator looked at a <see cref="Failed"/> entry and decided not to credit it at all —
    /// dust, a spam token, a mistaken transfer — through <see cref="IFailedValuationAdmin"/>.
    /// </summary>
    WrittenOff,
}
