namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Outcome of replaying a ledger range. <see cref="Completed"/> is the only thing that authorises the
/// cursor to move: it means every ledger in the range was genuinely searched.
/// </summary>
internal sealed class CatchUpResult
{
    private CatchUpResult(bool completed, int processedCount, string? reason)
    {
        Completed = completed;
        ProcessedCount = processedCount;
        Reason = reason;
    }

    public bool Completed { get; }

    public int ProcessedCount { get; }

    /// <summary>Why the replay could not be trusted. Null when it completed.</summary>
    public string? Reason { get; }

    public static CatchUpResult Complete(int processedCount) => new CatchUpResult(true, processedCount, null);

    public static CatchUpResult Incomplete(string reason) => new CatchUpResult(false, 0, reason);
}
