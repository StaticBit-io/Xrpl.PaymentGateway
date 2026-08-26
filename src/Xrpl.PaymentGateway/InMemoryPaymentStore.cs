using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>
/// A thread-safe in-process <see cref="IPaymentStore"/>. It ships as the reference for the interface
/// contract and backs tests and samples. Everything is lost on restart, so a production host that must
/// survive a restart supplies its own store.
/// </summary>
public sealed class InMemoryPaymentStore : IPaymentStore
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, uint> _tagsByBuyer = new Dictionary<string, uint>(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _buyersByTag = new Dictionary<uint, string>();
    private readonly Dictionary<string, PaymentEntry> _payments = new Dictionary<string, PaymentEntry>(StringComparer.Ordinal);
    private readonly List<string> _insertionOrder = new List<string>();
    private uint _nextTag;
    private uint? _cursor;

    /// <param name="firstDestinationTag">The tag handed to the first buyer. Zero is rejected: many wallets treat it as "no tag".</param>
    public InMemoryPaymentStore(uint firstDestinationTag = 1)
    {
        if (firstDestinationTag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDestinationTag), "destination tag 0 is not issued");
        }

        _nextTag = firstDestinationTag;
    }

    /// <summary>Every payment ever recorded, oldest first. Not part of <see cref="IPaymentStore"/>; samples use it.</summary>
    public IReadOnlyList<PaymentRecord> Snapshot()
    {
        lock (_gate)
        {
            return _insertionOrder.Select(hash => _payments[hash].Record).ToList();
        }
    }

    public Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        lock (_gate)
        {
            if (_tagsByBuyer.TryGetValue(buyerId, out uint existing))
            {
                return Task.FromResult(existing);
            }

            if (_nextTag == uint.MaxValue)
            {
                throw new InvalidOperationException("the destination tag space is exhausted");
            }

            uint assigned = _nextTag;
            _nextTag++;
            _tagsByBuyer[buyerId] = assigned;
            _buyersByTag[assigned] = buyerId;
            return Task.FromResult(assigned);
        }
    }

    public Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_buyersByTag.TryGetValue(tag, out string? buyer) ? buyer : null);
        }
    }

    public Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_payments.ContainsKey(record.TransactionHash))
            {
                return Task.FromResult(false);
            }

            _payments[record.TransactionHash] = new PaymentEntry(record);
            _insertionOrder.Add(record.TransactionHash);
            return Task.FromResult(true);
        }
    }

    public Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_payments.TryGetValue(transactionHash, out PaymentEntry? entry))
            {
                entry.Handled = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            List<PaymentRecord> result = new List<PaymentRecord>();
            foreach (string hash in _insertionOrder)
            {
                PaymentEntry entry = _payments[hash];
                if (entry.Handled)
                {
                    continue;
                }

                result.Add(entry.Record);
                if (result.Count == limit)
                {
                    break;
                }
            }

            return Task.FromResult<IReadOnlyList<PaymentRecord>>(result);
        }
    }

    public Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_cursor);
        }
    }

    public Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _cursor = ledgerIndex;
        }

        return Task.CompletedTask;
    }

    private sealed class PaymentEntry
    {
        public PaymentEntry(PaymentRecord record) => Record = record;

        public PaymentRecord Record { get; }

        public bool Handled { get; set; }
    }
}
