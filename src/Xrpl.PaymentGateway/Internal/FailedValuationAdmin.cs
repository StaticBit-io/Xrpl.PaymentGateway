using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The operator path for a valuation the automatic pipeline gave up on. See <see cref="IFailedValuationAdmin"/>
/// for the contract; this is the one implementation, reached only through that interface.
/// </summary>
/// <remarks>
/// Neither write operation delivers anything itself. Both leave the resolved row undelivered, exactly as
/// an automatic valuation is the moment <c>ValuationWorker</c> computes it, so the same delivery pass that
/// already retries a stuck automatic delivery is what hands this one to <see cref="IPaymentValuedHandler"/>
/// too — one delivery mechanism, not two.
/// </remarks>
internal sealed class FailedValuationAdmin : IFailedValuationAdmin
{
    private readonly IQuoteStore _store;
    private readonly TimeProvider _timeProvider;

    public FailedValuationAdmin(IQuoteStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<FailedValuationPage> ListFailedAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        IReadOnlyList<PaymentValuation> items = await _store
            .GetFailedValuationsAsync(limit, offset, cancellationToken)
            .ConfigureAwait(false);
        int total = await _store.CountFailedValuationsAsync(cancellationToken).ConfigureAwait(false);

        return new FailedValuationPage { Items = items, TotalCount = total };
    }

    public async Task ValueManuallyAsync(string transactionHash, decimal rate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);

        PaymentValuation entry = await RequireFailedAsync(transactionHash, cancellationToken).ConfigureAwait(false);

        PaymentValuation priced = new PaymentValuation
        {
            TransactionHash = entry.TransactionHash,
            PairKey = entry.PairKey,
            Amount = entry.Amount,
            PaymentLedgerIndex = entry.PaymentLedgerIndex,
            DestinationTag = entry.DestinationTag,
            EnqueuedAt = entry.EnqueuedAt,
            State = ValuationState.ValuedManually,
            ValuedAt = _timeProvider.GetUtcNow(),
            QuoteAmount = entry.Amount * rate,
            EffectivePrice = rate,
            FullyFilled = true,
            Delivered = false,
        };

        // SaveValuationAsync is the same call the automatic pipeline uses to move an entry on from
        // Pending; it clears FailedAt/FailureReason on the way, since this entry is resolved now.
        await _store.SaveValuationAsync(priced, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteOffAsync(string transactionHash, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await RequireFailedAsync(transactionHash, cancellationToken).ConfigureAwait(false);

        await _store
            .SaveWriteOffAsync(transactionHash, reason, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PaymentValuation> RequireFailedAsync(string transactionHash, CancellationToken cancellationToken)
    {
        PaymentValuation? entry = await _store.GetValuationAsync(transactionHash, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.State != ValuationState.Failed)
        {
            throw new InvalidOperationException(
                $"\"{transactionHash}\" is not a failed valuation — either it does not exist or it is not in the Failed state");
        }

        return entry;
    }
}
