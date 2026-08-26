using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// Wraps a real store so a test can make reads fail, or hold them open, without touching the rest of the
/// contract.
/// </summary>
public sealed class ScriptedPaymentStore : IPaymentStore
{
    private readonly IPaymentStore _inner;

    public ScriptedPaymentStore(IPaymentStore inner) => _inner = inner;

    private readonly TaskCompletionSource _readEntered =
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, every read of unhandled payments throws this.</summary>
    public Exception? UnhandledReadFailure { get; set; }

    /// <summary>When set, reads of unhandled payments wait on it before returning.</summary>
    public TaskCompletionSource? HoldUnhandledReads { get; set; }

    /// <summary>
    /// Completes once a read of unhandled payments has actually started. A test that wants a call to be
    /// genuinely in flight waits on this rather than on the caller's task not being finished yet, which
    /// is true immediately and proves nothing.
    /// </summary>
    public Task UnhandledReadStarted => _readEntered.Task;

    public Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken) =>
        _inner.GetOrAssignTagAsync(buyerId, cancellationToken);

    public Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken) =>
        _inner.FindBuyerByTagAsync(tag, cancellationToken);

    public Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken) =>
        _inner.TryAddPaymentAsync(record, cancellationToken);

    public Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.MarkHandledAsync(transactionHash, cancellationToken);

    public async Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken)
    {
        _readEntered.TrySetResult();

        if (UnhandledReadFailure is { } failure)
        {
            throw failure;
        }

        if (HoldUnhandledReads is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return await _inner.GetUnhandledPaymentsAsync(limit, cancellationToken);
    }

    public Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken) =>
        _inner.GetLastProcessedLedgerAsync(cancellationToken);

    public Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken) =>
        _inner.SetLastProcessedLedgerAsync(ledgerIndex, cancellationToken);
}
