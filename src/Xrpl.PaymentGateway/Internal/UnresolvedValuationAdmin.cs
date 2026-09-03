using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The operator path for a valuation the automatic pipeline has not resolved. See
/// <see cref="IUnresolvedValuationAdmin"/> for the contract; this is the one implementation, reached only
/// through that interface.
/// </summary>
/// <remarks>
/// Neither write operation delivers anything itself. Both leave the resolved row undelivered, exactly as
/// an automatic valuation is the moment <c>ValuationWorker</c> computes it, so the same delivery pass that
/// already retries a stuck automatic delivery is what hands this one to <see cref="IPaymentValuedHandler"/>
/// too — one delivery mechanism, not two.
/// </remarks>
internal sealed class UnresolvedValuationAdmin : IUnresolvedValuationAdmin
{
    /// <summary>
    /// Applied when a caller passes null for <c>minAge</c> — long enough that an entry still working
    /// through an ordinary transient wait does not show up as something to act on, short enough that a
    /// genuinely stuck entry does not sit unnoticed for long. See <see cref="IUnresolvedValuationAdmin.ListUnresolvedAsync"/>.
    /// </summary>
    private static readonly TimeSpan DefaultMinAge = TimeSpan.FromMinutes(15);

    private readonly IQuoteStore _store;
    private readonly TimeProvider _timeProvider;

    public UnresolvedValuationAdmin(IQuoteStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<UnresolvedValuationPage> ListUnresolvedAsync(
        int limit, int offset, TimeSpan? minAge, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        DateTimeOffset olderThan = _timeProvider.GetUtcNow() - (minAge ?? DefaultMinAge);

        IReadOnlyList<PaymentValuation> items = await _store
            .GetUnresolvedValuationsAsync(olderThan, limit, offset, cancellationToken)
            .ConfigureAwait(false);
        int total = await _store.CountUnresolvedValuationsAsync(olderThan, cancellationToken).ConfigureAwait(false);

        return new UnresolvedValuationPage { Items = items, TotalCount = total };
    }

    public async Task ValueManuallyAsync(string transactionHash, decimal rate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);

        PaymentValuation entry = await RequireUnresolvedAsync(transactionHash, cancellationToken).ConfigureAwait(false);

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
        // Pending; it clears FailedAt/FailureReason on the way, since this entry is resolved now. Its
        // return says whether the row was still Pending or Failed when the write reached the store — if
        // another operation resolved it between the read above and this write, nothing was written, and
        // that must not be reported as a successful price.
        bool applied = await _store.SaveValuationAsync(priced, cancellationToken).ConfigureAwait(false);
        if (!applied)
        {
            throw new InvalidOperationException(
                $"\"{transactionHash}\" was resolved by another operation before this price could be applied");
        }
    }

    public async Task WriteOffAsync(string transactionHash, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await RequireUnresolvedAsync(transactionHash, cancellationToken).ConfigureAwait(false);

        bool applied = await _store
            .SaveWriteOffAsync(transactionHash, reason, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (!applied)
        {
            throw new InvalidOperationException(
                $"\"{transactionHash}\" was resolved by another operation before this write-off could be applied");
        }
    }

    private async Task<PaymentValuation> RequireUnresolvedAsync(string transactionHash, CancellationToken cancellationToken)
    {
        PaymentValuation? entry = await _store.GetValuationAsync(transactionHash, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.State is not (ValuationState.Pending or ValuationState.Failed))
        {
            throw new InvalidOperationException(
                $"\"{transactionHash}\" is not an unresolved valuation — either it does not exist or it is not "
                + "Pending or Failed");
        }

        return entry;
    }
}
