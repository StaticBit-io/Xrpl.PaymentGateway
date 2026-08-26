namespace Xrpl.PaymentGateway.Internal;

/// <summary>The parts of <c>server_info</c> the monitor reasons about.</summary>
internal sealed class NodeStatus
{
    /// <summary>Lower-cased <c>server_state</c>, e.g. "full", "syncing", "proposing".</summary>
    public required string ServerState { get; init; }

    /// <summary>Sequence of the node's latest validated ledger, or null when it has none.</summary>
    public uint? ValidatedLedgerIndex { get; init; }

    /// <summary>Raw <c>complete_ledgers</c> string, fed to <see cref="LedgerRangeSet"/>.</summary>
    public string? CompleteLedgers { get; init; }

    /// <summary>
    /// True when the node is in sync with the network and its view can be trusted. A standalone rippled
    /// reports "proposing", which is why the list is wider than just "full".
    /// </summary>
    public bool IsSynced => ServerState is "full" or "validating" or "proposing";
}
