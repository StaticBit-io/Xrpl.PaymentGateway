using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>
/// A thread-safe in-process <see cref="IQuoteStore"/>. Reference implementation of the contract, and
/// what tests and samples run on. Everything is lost on restart.
/// </summary>
public sealed class InMemoryQuoteStore : IQuoteStore
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, StoredQuote> _quotes = new Dictionary<string, StoredQuote>(StringComparer.Ordinal);
    private readonly Dictionary<string, PaymentValuation> _valuations = new Dictionary<string, PaymentValuation>(StringComparer.Ordinal);
    private readonly List<string> _order = new List<string>();

    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);

        lock (_gate)
        {
            _quotes[quote.PairKey] = quote;
        }

        return Task.CompletedTask;
    }

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);

        lock (_gate)
        {
            return Task.FromResult(_quotes.TryGetValue(pairKey, out StoredQuote? quote) ? quote : null);
        }
    }

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredQuote>>(_quotes.Values.ToList());
        }
    }

    public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (_valuations.ContainsKey(pending.TransactionHash))
            {
                return Task.FromResult(false);
            }

            _valuations[pending.TransactionHash] = pending;
            _order.Add(pending.TransactionHash);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            // _order is already enqueue-ordered, so a plain filter over it is oldest-enqueued first.
            List<PaymentValuation> result = _order
                .Select(hash => _valuations[hash])
                .Where(entry => entry.State == ValuationState.Pending
                    && string.Equals(entry.PairKey, pairKey, StringComparison.Ordinal))
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<PaymentValuation>>(result);
        }
    }

    public Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            List<PendingValuationsByPair> result = _valuations.Values
                .Where(entry => entry.State == ValuationState.Pending)
                .GroupBy(entry => entry.PairKey, StringComparer.Ordinal)
                .Select(group => new PendingValuationsByPair
                {
                    PairKey = group.Key,
                    Count = group.Count(),
                    OldestEnqueuedAt = group.Min(entry => entry.EnqueuedAt),
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<PendingValuationsByPair>>(result);
        }
    }

    public Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        lock (_gate)
        {
            // A whole-object replace, so a caller moving an entry on from Failed simply builds the new
            // Valued/ValuedManually valuation without FailedAt/FailureReason set — there is nothing here
            // that needs to clear them separately. Guarded by state: only an entry still Pending or Failed
            // may be replaced, so a resolution racing another one (an operator's write-off landing between
            // this read and this write, say) is left alone rather than silently overwritten.
            if (_valuations.TryGetValue(valuation.TransactionHash, out PaymentValuation? current)
                && current.State is ValuationState.Pending or ValuationState.Failed)
            {
                // Delivered is enforced here rather than trusted from valuation itself: a caller that
                // builds the replacement by copying an existing delivered row — the natural way to carry
                // its other fields forward — must not have that copy's flag survive into the resolved row.
                _valuations[valuation.TransactionHash] = valuation.Delivered ? WithDeliveredCleared(valuation) : valuation;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            if (_valuations.TryGetValue(transactionHash, out PaymentValuation? entry) && entry.State == ValuationState.Pending)
            {
                _valuations[transactionHash] = WithFailure(entry, reason, failedAt);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            if (_valuations.TryGetValue(transactionHash, out PaymentValuation? entry)
                && entry.State is ValuationState.Pending or ValuationState.Failed)
            {
                _valuations[transactionHash] = WithWriteOff(entry, reason, writtenOffAt);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_gate)
        {
            // Starting from _order (enqueue order) and sorting with a stable OrderBy is what makes ties on
            // FailedAt fall back to enqueue order, matching PostgresQuoteStore's
            // "ORDER BY failed_at, queued_seq" rather than the unrelated order entries happened to fail in.
            List<PaymentValuation> result = _order
                .Select(hash => _valuations[hash])
                .Where(entry => entry.State == ValuationState.Failed)
                .OrderBy(entry => entry.FailedAt)
                .Skip(offset)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<PaymentValuation>>(result);
        }
    }

    public Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_valuations.Values.Count(entry => entry.State == ValuationState.Failed));
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
        DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_gate)
        {
            // _order is enqueue order already, so a stable OrderBy over it makes ties on EnqueuedAt fall
            // back to enqueue order, matching PostgresQuoteStore's "ORDER BY enqueued_at, queued_seq".
            List<PaymentValuation> result = _order
                .Select(hash => _valuations[hash])
                .Where(entry => entry.State is ValuationState.Pending or ValuationState.Failed
                    && entry.EnqueuedAt <= olderThan)
                .OrderBy(entry => entry.EnqueuedAt)
                .Skip(offset)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<PaymentValuation>>(result);
        }
    }

    public Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_valuations.Values.Count(
                entry => entry.State is ValuationState.Pending or ValuationState.Failed && entry.EnqueuedAt <= olderThan));
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Take(limit, entry => entry.State != ValuationState.Pending && !entry.Delivered));

    public Task<bool> MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        lock (_gate)
        {
            // Only applies when the row is still in the state the caller actually handed to the host
            // handler — otherwise an operator's resolution racing a slow delivery call for the stale
            // content would be marked delivered on the resolution's behalf, and the resolution itself would
            // never reach the host. Left alone here, the row stays undelivered for the next pass.
            if (_valuations.TryGetValue(transactionHash, out PaymentValuation? entry) && entry.State == deliveredState)
            {
                _valuations[transactionHash] = Deliver(entry);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        lock (_gate)
        {
            return Task.FromResult(_valuations.TryGetValue(transactionHash, out PaymentValuation? entry) ? entry : null);
        }
    }

    private IReadOnlyList<PaymentValuation> Take(int limit, Func<PaymentValuation, bool> predicate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            List<PaymentValuation> result = new List<PaymentValuation>();
            foreach (string hash in _order)
            {
                PaymentValuation entry = _valuations[hash];
                if (!predicate(entry))
                {
                    continue;
                }

                result.Add(entry);
                if (result.Count == limit)
                {
                    break;
                }
            }

            return result;
        }
    }

    private static PaymentValuation Deliver(PaymentValuation entry) => new PaymentValuation
    {
        TransactionHash = entry.TransactionHash,
        PairKey = entry.PairKey,
        Amount = entry.Amount,
        PaymentLedgerIndex = entry.PaymentLedgerIndex,
        DestinationTag = entry.DestinationTag,
        EnqueuedAt = entry.EnqueuedAt,
        State = entry.State,
        ValuedAt = entry.ValuedAt,
        QuoteAmount = entry.QuoteAmount,
        EffectivePrice = entry.EffectivePrice,
        MarginalPrice = entry.MarginalPrice,
        SlippagePercent = entry.SlippagePercent,
        FullyFilled = entry.FullyFilled,
        BookTruncated = entry.BookTruncated,
        Route = entry.Route,
        SnapshotLedgerIndex = entry.SnapshotLedgerIndex,
        SnapshotCapturedAt = entry.SnapshotCapturedAt,
        FailedAt = entry.FailedAt,
        FailureReason = entry.FailureReason,
        WrittenOffAt = entry.WrittenOffAt,
        WriteOffReason = entry.WriteOffReason,
        Delivered = true,
    };

    /// <summary>A copy of <paramref name="valuation"/> with <see cref="PaymentValuation.Delivered"/> forced false.</summary>
    private static PaymentValuation WithDeliveredCleared(PaymentValuation valuation) => new PaymentValuation
    {
        TransactionHash = valuation.TransactionHash,
        PairKey = valuation.PairKey,
        Amount = valuation.Amount,
        PaymentLedgerIndex = valuation.PaymentLedgerIndex,
        DestinationTag = valuation.DestinationTag,
        EnqueuedAt = valuation.EnqueuedAt,
        State = valuation.State,
        ValuedAt = valuation.ValuedAt,
        QuoteAmount = valuation.QuoteAmount,
        EffectivePrice = valuation.EffectivePrice,
        MarginalPrice = valuation.MarginalPrice,
        SlippagePercent = valuation.SlippagePercent,
        FullyFilled = valuation.FullyFilled,
        BookTruncated = valuation.BookTruncated,
        Route = valuation.Route,
        SnapshotLedgerIndex = valuation.SnapshotLedgerIndex,
        SnapshotCapturedAt = valuation.SnapshotCapturedAt,
        FailedAt = valuation.FailedAt,
        FailureReason = valuation.FailureReason,
        WrittenOffAt = valuation.WrittenOffAt,
        WriteOffReason = valuation.WriteOffReason,
        Delivered = false,
    };

    private static PaymentValuation WithFailure(PaymentValuation entry, string reason, DateTimeOffset failedAt) => new PaymentValuation
    {
        TransactionHash = entry.TransactionHash,
        PairKey = entry.PairKey,
        Amount = entry.Amount,
        PaymentLedgerIndex = entry.PaymentLedgerIndex,
        DestinationTag = entry.DestinationTag,
        EnqueuedAt = entry.EnqueuedAt,
        State = ValuationState.Failed,
        FailedAt = failedAt,
        FailureReason = reason,
        Delivered = false,
    };

    private static PaymentValuation WithWriteOff(PaymentValuation entry, string reason, DateTimeOffset writtenOffAt) => new PaymentValuation
    {
        TransactionHash = entry.TransactionHash,
        PairKey = entry.PairKey,
        Amount = entry.Amount,
        PaymentLedgerIndex = entry.PaymentLedgerIndex,
        DestinationTag = entry.DestinationTag,
        EnqueuedAt = entry.EnqueuedAt,
        State = ValuationState.WrittenOff,
        FailedAt = entry.FailedAt,
        FailureReason = entry.FailureReason,
        WrittenOffAt = writtenOffAt,
        WriteOffReason = reason,
        Delivered = false,
    };
}
