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
    /// Queued, not yet priced. Only transient causes leave an entry here: no snapshot has been captured for
    /// the pair yet, the held one is past its age limit, the snapshot answered that the pair currently has
    /// no liquidity to price this amount against, or the store rejected the write that would have moved the
    /// entry on. Every one of these cures itself — a later snapshot, a later capture, a later store write —
    /// so nothing here is ever retried on a timer or counted against it; the entry simply prices itself once
    /// conditions allow.
    /// </summary>
    Pending,

    /// <summary>Priced by the automatic pipeline from a liquidity snapshot.</summary>
    Valued,

    /// <summary>Priced by an operator, through <see cref="IFailedValuationAdmin"/>, at a rate they supplied.</summary>
    ValuedManually,

    /// <summary>
    /// Terminal. Reached only for a per-entry, non-transient cause: the pair is no longer configured, or
    /// pricing it threw. Both are deterministic — another attempt cannot change either outcome — which is
    /// what distinguishes them from a transient, pair-wide condition like a missing snapshot or "no
    /// liquidity right now", none of which fail an entry; see <see cref="Pending"/>. Never retried
    /// automatically — see <see cref="IFailedValuationAdmin"/> for the operator path this state waits on.
    /// </summary>
    Failed,

    /// <summary>
    /// Terminal. An operator looked at a <see cref="Failed"/> entry and decided not to credit it at all —
    /// dust, a spam token, a mistaken transfer — through <see cref="IFailedValuationAdmin"/>.
    /// </summary>
    WrittenOff,
}
