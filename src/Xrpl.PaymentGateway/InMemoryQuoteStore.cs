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

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Take(limit, entry => !entry.IsValued));

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        lock (_gate)
        {
            if (_valuations.ContainsKey(valuation.TransactionHash))
            {
                _valuations[valuation.TransactionHash] = valuation;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Take(limit, entry => entry.IsValued && !entry.Delivered));

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        lock (_gate)
        {
            if (_valuations.TryGetValue(transactionHash, out PaymentValuation? entry))
            {
                _valuations[transactionHash] = Deliver(entry);
            }
        }

        return Task.CompletedTask;
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
        Delivered = true,
    };
}
