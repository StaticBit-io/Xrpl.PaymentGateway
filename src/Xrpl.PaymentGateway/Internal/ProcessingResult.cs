using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>What the processor decided about one transaction.</summary>
internal enum ProcessingResultKind
{
    /// <summary>Not a payment to us. Nothing to do.</summary>
    Skipped,

    /// <summary>A clean incoming payment.</summary>
    Recorded,

    /// <summary>Credited us in a shape a receiving account should not see. Always logged and counted.</summary>
    Anomaly,
}

/// <summary>The processor's verdict, plus the record when there is one.</summary>
internal sealed class ProcessingResult
{
    private ProcessingResult(ProcessingResultKind kind, PaymentRecord? record, string? reason)
    {
        Kind = kind;
        Record = record;
        Reason = reason;
    }

    public ProcessingResultKind Kind { get; }

    /// <summary>The payment to persist, or null when there is nothing to persist.</summary>
    public PaymentRecord? Record { get; }

    /// <summary>Why the transaction was skipped or flagged. Null for a clean record.</summary>
    public string? Reason { get; }

    public static ProcessingResult Skip(string reason) => new ProcessingResult(ProcessingResultKind.Skipped, null, reason);

    public static ProcessingResult Recorded(PaymentRecord record) => new ProcessingResult(ProcessingResultKind.Recorded, record, null);

    public static ProcessingResult Anomaly(PaymentRecord? record, string reason) => new ProcessingResult(ProcessingResultKind.Anomaly, record, reason);
}
