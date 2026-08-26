namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>What the background monitor is doing right now.</summary>
public enum PaymentMonitorState
{
    /// <summary>Not started, or shut down.</summary>
    Stopped,

    /// <summary>Opening a connection to a node.</summary>
    Connecting,

    /// <summary>Replaying ledgers between the cursor and the current validated ledger.</summary>
    CatchingUp,

    /// <summary>Connected and consuming the live stream. The healthy state.</summary>
    Streaming,

    /// <summary>Backing off before the next connection attempt.</summary>
    Reconnecting,

    /// <summary>Nodes are synced but the network is not validating ledgers. Not a local fault.</summary>
    NetworkStalled,

    /// <summary>The store is failing; processing is paused and the cursor is frozen.</summary>
    StoreUnavailable,

    /// <summary>No node in the pool holds the ledger range needed to prove completeness.</summary>
    HistoryGap,
}
