namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Outcome of a reconciliation run.</summary>
public sealed class ReconciliationResult
{
    /// <summary>Records that were unhandled and reached the host handler on this run.</summary>
    public required int RedeliveredCount { get; init; }

    /// <summary>Payments found on the ledger that were missing from the store. Any value above zero is a defect.</summary>
    public required int RecoveredCount { get; init; }

    /// <summary>Errors encountered. A non-empty list means the run did not fully complete.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when another reconciliation was already running and this call did nothing.</summary>
    public bool Skipped { get; init; }
}
