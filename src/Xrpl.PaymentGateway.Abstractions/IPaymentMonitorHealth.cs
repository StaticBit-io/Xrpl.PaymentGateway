namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Liveness and repair. Drive both from whatever scheduler the host already runs — Hangfire, Quartz,
/// a timer. The library takes no scheduler dependency.
/// </summary>
public interface IPaymentMonitorHealth
{
    /// <summary>Cheap read-only snapshot. Safe to call every few seconds.</summary>
    Task<PaymentMonitorHealthReport> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Redelivers unhandled records and re-verifies a window of ledgers below the cursor.
    /// Long-running; concurrent calls return immediately with <see cref="ReconciliationResult.Skipped"/> set.
    /// </summary>
    Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}
