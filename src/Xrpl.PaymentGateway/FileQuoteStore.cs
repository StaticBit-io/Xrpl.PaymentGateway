using System.Text.Json;
using System.Text.Json.Serialization;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>
/// An <see cref="IQuoteStore"/> in one JSON file, rewritten atomically per write. For a host that wants
/// quotes without a database. One process only, which is the only supported way to run the gateway anyway.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="FilePaymentStore"/> and its file. Quotes are an optional
/// addition, and an optional addition has no business rewriting the file that holds the money.
/// </remarks>
public sealed class FileQuoteStore : IQuoteStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly State _state;
    private bool _disposed;

    /// <param name="path">The file to keep quotes in. Created, with its directory, on first write.</param>
    public FileQuoteStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = System.IO.Path.GetFullPath(path);
        _state = Load(Path) ?? new State();
    }

    /// <summary>Absolute path of the backing file. Not part of the interface; tests and samples use it.</summary>
    public string Path { get; }

    public async Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredQuote? previous = _state.Quotes.FirstOrDefault(
                q => string.Equals(q.PairKey, quote.PairKey, StringComparison.Ordinal));
            _state.Quotes.RemoveAll(q => string.Equals(q.PairKey, quote.PairKey, StringComparison.Ordinal));
            _state.Quotes.Add(quote);

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The write failed, so the file still holds whatever it held before. In-memory state must
                // match that exactly, or a caller told this call threw would see the new quote anyway on
                // the next read — a mutation the disk never agreed to.
                _state.Quotes.Remove(quote);
                if (previous is not null)
                {
                    _state.Quotes.Add(previous);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Quotes.FirstOrDefault(q => string.Equals(q.PairKey, pairKey, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Quotes.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Find(pending.TransactionHash) is not null)
            {
                return false;
            }

            _state.Valuations.Add(pending);

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Otherwise the hash reads as queued in memory forever while the file never agreed, and
                // the caller who retries on the exception is refused with "already queued".
                _state.Valuations.Remove(pending);
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        TakeOrderedAsync(limit, entry => !entry.IsValued, cancellationToken);

    public async Task MarkValuationAttemptedAsync(string transactionHash, DateTimeOffset attemptedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, transactionHash, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            PaymentValuation previous = _state.Valuations[index];
            _state.Valuations[index] = WithAttempt(previous, attemptedAt);

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _state.Valuations[index] = previous;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, valuation.TransactionHash, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            PaymentValuation previous = _state.Valuations[index];
            _state.Valuations[index] = valuation;

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Otherwise the entry reads as valued in memory while the file still holds it pending: it
                // would be delivered now from memory and, after a restart, priced and delivered again.
                _state.Valuations[index] = previous;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        TakeAsync(limit, entry => entry.IsValued && !entry.Delivered, cancellationToken);

    public async Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, transactionHash, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            PaymentValuation previous = _state.Valuations[index];
            _state.Valuations[index] = Delivered(previous);

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _state.Valuations[index] = previous;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Find(transactionHash);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private PaymentValuation? Find(string hash) =>
        _state.Valuations.FirstOrDefault(v => string.Equals(v.TransactionHash, hash, StringComparison.Ordinal));

    private async Task<IReadOnlyList<PaymentValuation>> TakeAsync(
        int limit,
        Func<PaymentValuation, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Valuations.Where(predicate).Take(limit).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Like <see cref="TakeAsync"/>, but ordered for fairness rather than plain enqueue order: by when the
    /// entry was last considered, treating "never attempted" as its enqueue time. "Never-attempted first"
    /// looks like the same fairness rule but is not: it demotes any entry below every payment queued after
    /// its one attempt, so once arrivals keep the never-attempted group at or above the batch size the
    /// stamped backlog is never fetched again — the same starvation, just moved to the whole attempted
    /// set. <c>OrderBy</c> is a stable sort, and <see cref="State.Valuations"/> is already in enqueue
    /// order, so entries tied on the key keep their enqueue order for free, matching queued_seq in the
    /// SQL store.
    /// </summary>
    private async Task<IReadOnlyList<PaymentValuation>> TakeOrderedAsync(
        int limit,
        Func<PaymentValuation, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Valuations
                .Where(predicate)
                .OrderBy(entry => entry.LastAttemptAt ?? entry.EnqueuedAt)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PaymentValuation Delivered(PaymentValuation entry) => new PaymentValuation
    {
        TransactionHash = entry.TransactionHash,
        PairKey = entry.PairKey,
        Amount = entry.Amount,
        PaymentLedgerIndex = entry.PaymentLedgerIndex,
        DestinationTag = entry.DestinationTag,
        EnqueuedAt = entry.EnqueuedAt,
        LastAttemptAt = entry.LastAttemptAt,
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

    private static PaymentValuation WithAttempt(PaymentValuation entry, DateTimeOffset attemptedAt) => new PaymentValuation
    {
        TransactionHash = entry.TransactionHash,
        PairKey = entry.PairKey,
        Amount = entry.Amount,
        PaymentLedgerIndex = entry.PaymentLedgerIndex,
        DestinationTag = entry.DestinationTag,
        EnqueuedAt = entry.EnqueuedAt,
        LastAttemptAt = attemptedAt,
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
        Delivered = entry.Delivered,
    };

    private static State? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<State>(stream, SerializerOptions);
    }

    /// <summary>
    /// Writes beside the target and then replaces it, so an interrupted write cannot leave the file
    /// half-rewritten. The caller already holds the gate.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = Path + ".tmp";

        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, _state, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, Path, overwrite: true);
    }

    private sealed class State
    {
        public List<StoredQuote> Quotes { get; set; } = new List<StoredQuote>();

        public List<PaymentValuation> Valuations { get; set; } = new List<PaymentValuation>();
    }
}
