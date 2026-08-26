namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>A point-in-time view of the monitor, for dashboards, health endpoints and alerting.</summary>
public sealed class PaymentMonitorHealthReport
{
    public required PaymentMonitorState State { get; init; }

    /// <summary>The node currently in use, or null when not connected.</summary>
    public string? CurrentNode { get; init; }

    /// <summary>Highest validated ledger the monitor has seen.</summary>
    public uint? LastValidatedLedger { get; init; }

    /// <summary>The persisted completeness boundary.</summary>
    public uint? Cursor { get; init; }

    /// <summary>Ledgers between the cursor and the last validated ledger.</summary>
    public uint LedgerLag { get; init; }

    /// <summary>Payments recorded but not yet delivered to the host handler, capped at the sample size.</summary>
    public int UnhandledPaymentCount { get; init; }

    /// <summary>Transactions that credited the account in a shape the gateway does not expect.</summary>
    public long AnomalyCount { get; init; }

    /// <summary>Last error the monitor recorded, or null.</summary>
    public string? LastError { get; init; }

    /// <summary>When the monitor last saw a ledger close.</summary>
    public DateTimeOffset? LastLedgerAt { get; init; }

    /// <summary>True when streaming, within the acceptable ledger lag, and with nothing stuck undelivered.</summary>
    public required bool IsHealthy { get; init; }
}
