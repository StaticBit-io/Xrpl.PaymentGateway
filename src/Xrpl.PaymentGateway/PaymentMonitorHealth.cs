using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>
/// Liveness reporting and repair. Neither method drives the monitor: reconciliation is a safety net, and
/// the monitor stays correct whether or not anybody ever calls it.
/// </summary>
/// <remarks>
/// Internal for the same reason as the monitor: the constructor takes the internal
/// <c>MonitorSnapshot</c> and <c>IXrplNodeConnectionFactory</c>, and a public constructor may not expose
/// less accessible types (CS0051). Hosts resolve it as <see cref="IPaymentMonitorHealth"/>.
/// </remarks>
internal sealed class PaymentMonitorHealth : IPaymentMonitorHealth
{
    private readonly PaymentGatewayOptions _options;
    private readonly IPaymentStore _store;
    private readonly MonitorSnapshot _snapshot;
    private readonly IXrplNodeConnectionFactory _connectionFactory;
    private readonly ILogger<PaymentMonitorHealth> _logger;
    private readonly PaymentDispatcher _dispatcher;
    private readonly TransactionProcessor _processor;
    private readonly CatchUpRunner _catchUp;
    private readonly SemaphoreSlim _reconcileGate = new SemaphoreSlim(1, 1);

    public PaymentMonitorHealth(
        IOptions<PaymentGatewayOptions> options,
        IPaymentStore store,
        IPaymentReceivedHandler handler,
        MonitorSnapshot snapshot,
        IXrplNodeConnectionFactory connectionFactory,
        ILogger<PaymentMonitorHealth> logger,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _store = store;
        _snapshot = snapshot;
        _connectionFactory = connectionFactory;
        _logger = logger;
        _dispatcher = new PaymentDispatcher(store, handler, logger);
        _processor = new TransactionProcessor(_options.Address, timeProvider, logger);
        _catchUp = new CatchUpRunner(logger);
    }

    public async Task<PaymentMonitorHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        MonitorSnapshotData data = _snapshot.Read();
        string? lastError = data.LastError;
        int unhandled = 0;

        try
        {
            IReadOnlyList<PaymentRecord> pending = await _store
                .GetUnhandledPaymentsAsync(_options.HealthUnhandledSampleSize, cancellationToken)
                .ConfigureAwait(false);
            unhandled = pending.Count;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            _logger.LogError(ex, "reading unhandled payments for the health report failed");
        }

        uint lag = data.LastValidatedLedger is { } validated && data.Cursor is { } cursor && validated > cursor
            ? validated - cursor
            : 0;

        return new PaymentMonitorHealthReport
        {
            State = data.State,
            CurrentNode = data.Node,
            LastValidatedLedger = data.LastValidatedLedger,
            Cursor = data.Cursor,
            LedgerLag = lag,
            UnhandledPaymentCount = unhandled,
            AnomalyCount = data.AnomalyCount,
            LastError = lastError,
            LastLedgerAt = data.LastLedgerAt,
            IsHealthy = data.State == PaymentMonitorState.Streaming
                && lag <= _options.MaxAcceptableLedgerLag
                && unhandled == 0,
        };
    }

    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _reconcileGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ReconciliationResult
            {
                RedeliveredCount = 0,
                RecoveredCount = 0,
                Errors = Array.Empty<string>(),
                Skipped = true,
            };
        }

        try
        {
            List<string> errors = new List<string>();
            int redelivered = await RedeliverAsync(errors, cancellationToken).ConfigureAwait(false);
            int recovered = await SweepAsync(errors, cancellationToken).ConfigureAwait(false);

            return new ReconciliationResult
            {
                RedeliveredCount = redelivered,
                RecoveredCount = recovered,
                Errors = errors,
            };
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// One batch per run, deliberately. Looping until the queue drains would spin forever on a handler
    /// that keeps failing, and the next scheduled run picks up whatever is left.
    /// </summary>
    private async Task<int> RedeliverAsync(List<string> errors, CancellationToken cancellationToken)
    {
        IReadOnlyList<PaymentRecord> pending;

        try
        {
            pending = await _store
                .GetUnhandledPaymentsAsync(_options.HealthUnhandledSampleSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"reading unhandled payments failed: {ex.Message}");
            return 0;
        }

        int delivered = 0;
        foreach (PaymentRecord record in pending)
        {
            await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
            delivered++;
        }

        if (delivered > 0)
        {
            _logger.LogWarning("reconciliation redelivered {Count} payments the handler had not accepted", delivered);
        }

        return delivered;
    }

    /// <summary>
    /// Re-reads a window of ledgers below the cursor. Every payment it has to insert is a defect, so each
    /// one is logged as an error rather than counted quietly.
    /// </summary>
    private async Task<int> SweepAsync(List<string> errors, CancellationToken cancellationToken)
    {
        MonitorSnapshotData data = _snapshot.Read();
        if (data.Cursor is not { } cursor || cursor == 0)
        {
            return 0;
        }

        uint from = cursor > _options.ReconcileWindow ? cursor - _options.ReconcileWindow : 1;
        int recovered = 0;

        foreach (Uri candidate in _options.EffectiveCatchUpNodes)
        {
            try
            {
                await using IXrplNodeConnection connection = _connectionFactory.Create(candidate);
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

                CatchUpResult result = await _catchUp.RunAsync(
                    connection,
                    _options.Address,
                    from,
                    cursor,
                    async (transaction, token) =>
                    {
                        if (await RecoverAsync(transaction, token).ConfigureAwait(false))
                        {
                            recovered++;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.Completed)
                {
                    return recovered;
                }

                errors.Add(result.Reason ?? $"the sweep over {from}-{cursor} on {candidate} could not be proven complete");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"sweep against {candidate} failed: {ex.Message}");
            }
        }

        return recovered;
    }

    /// <summary>Returns true when the payment was missing from the store and had to be recorded now.</summary>
    private async Task<bool> RecoverAsync(IAccountTransaction transaction, CancellationToken cancellationToken)
    {
        ProcessingResult result = _processor.Process(transaction);
        if (result.Record is not { } record)
        {
            return false;
        }

        bool isNew = await _dispatcher.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        if (!isNew)
        {
            return false;
        }

        _logger.LogError(
            "reconciliation found payment {Hash} in ledger {Ledger} that the monitor never recorded; it has been recorded now",
            record.TransactionHash,
            record.LedgerIndex);

        await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
