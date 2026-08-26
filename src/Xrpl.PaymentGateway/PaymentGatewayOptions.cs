namespace Xrpl.PaymentGateway;

/// <summary>Everything the gateway needs to know. One instance means one receiving account.</summary>
public sealed class PaymentGatewayOptions
{
    /// <summary>The receiving r-address. Payments to it are recorded; its own transactions are ignored.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Allowed WebSocket nodes, tried round-robin on every reconnect.</summary>
    public IReadOnlyList<Uri> Nodes { get; set; } = Array.Empty<Uri>();

    /// <summary>
    /// Optional full-history nodes used only for catch-up and reconciliation, so ordinary streaming can
    /// run against light nodes. Falls back to <see cref="Nodes"/> when unset.
    /// </summary>
    public IReadOnlyList<Uri>? CatchUpNodes { get; set; }

    /// <summary>
    /// Where to begin when the store has no cursor. This ledger is treated as already processed, so the
    /// first catch-up starts at the one after it. Unset means start at the current validated ledger, and a
    /// value above it is clamped down to it.
    /// </summary>
    public uint? StartLedgerIndex { get; set; }

    /// <summary>
    /// The tag handed to the first buyer. Zero is not issued: many wallets treat it as "no tag".
    /// Tag allocation belongs to the store, so this is the value to hand your store implementation —
    /// the library validates it but cannot apply it on the store's behalf.
    /// </summary>
    public uint FirstDestinationTag { get; set; } = 1;

    /// <summary>How long without a ledger close before the node is suspected. Normal close time is 3-5 seconds.</summary>
    public TimeSpan LedgerStallTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often to re-check a network-wide stall before doing anything about it.</summary>
    public TimeSpan NetworkStallProbeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First reconnect delay. Doubles per attempt, jittered, capped by <see cref="ReconnectMaxDelay"/>.</summary>
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the reconnect delay.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a session must last to count as productive and reset the reconnect backoff. Without it,
    /// a node that accepts the socket and drops it immediately would be retried forever at the base delay.
    /// </summary>
    public TimeSpan ProductiveSessionThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First store retry delay.</summary>
    public TimeSpan StoreRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the store retry delay. Retries never give up.</summary>
    public TimeSpan StoreRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// In-memory buffer between the socket and processing. Overflowing it drops the connection on purpose:
    /// catch-up recollects everything from the ledger, so the buffer is never the system of record.
    /// </summary>
    public int StreamBufferCapacity { get; set; } = 1000;

    /// <summary>How many ledgers below the cursor reconciliation re-verifies. 2000 is roughly two hours.</summary>
    public uint ReconcileWindow { get; set; } = 2000;

    /// <summary>Cap on the unhandled-payment count a health check reads.</summary>
    public int HealthUnhandledSampleSize { get; set; } = 100;

    /// <summary>Ledger lag above which the health report stops calling itself healthy.</summary>
    public uint MaxAcceptableLedgerLag { get; set; } = 10;

    /// <summary>Nodes used for catch-up: <see cref="CatchUpNodes"/> when set, otherwise <see cref="Nodes"/>.</summary>
    public IReadOnlyList<Uri> EffectiveCatchUpNodes =>
        CatchUpNodes is { Count: > 0 } dedicated ? dedicated : Nodes;
}
