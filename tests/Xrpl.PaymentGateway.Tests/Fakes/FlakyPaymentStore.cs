using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Wraps a real store and fails the first N calls to TryAddPaymentAsync.</summary>
public sealed class FlakyPaymentStore : IPaymentStore
{
    private readonly IPaymentStore _inner;
    private int _remainingFailures;

    public FlakyPaymentStore(IPaymentStore inner, int failures)
    {
        _inner = inner;
        _remainingFailures = failures;
    }

    public int AddAttempts { get; private set; }

    public Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken) =>
        _inner.GetOrAssignTagAsync(buyerId, cancellationToken);

    public Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken) =>
        _inner.FindBuyerByTagAsync(tag, cancellationToken);

    public Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        AddAttempts++;
        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            return Task.FromException<bool>(new TimeoutException("store unavailable"));
        }

        return _inner.TryAddPaymentAsync(record, cancellationToken);
    }

    public Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.MarkHandledAsync(transactionHash, cancellationToken);

    public Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetUnhandledPaymentsAsync(limit, cancellationToken);

    public Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken) =>
        _inner.GetLastProcessedLedgerAsync(cancellationToken);

    public Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken) =>
        _inner.SetLastProcessedLedgerAsync(ledgerIndex, cancellationToken);
}
