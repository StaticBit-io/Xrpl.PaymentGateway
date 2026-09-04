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

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        return TakeAsync(
            limit,
            entry => entry.State == ValuationState.Pending
                && string.Equals(entry.PairKey, pairKey, StringComparison.Ordinal),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Valuations
                .Where(entry => entry.State == ValuationState.Pending)
                .GroupBy(entry => entry.PairKey, StringComparer.Ordinal)
                .Select(group => new PendingValuationsByPair
                {
                    PairKey = group.Key,
                    Count = group.Count(),
                    OldestEnqueuedAt = group.Min(entry => entry.EnqueuedAt),
                })
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, valuation.TransactionHash, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            PaymentValuation previous = _state.Valuations[index];
            // Guarded by state: only an entry still Pending or Failed may be replaced, so a resolution
            // racing another one (an operator's write-off landing between this read and this write, say)
            // is left alone rather than silently overwritten.
            if (previous.State is not (ValuationState.Pending or ValuationState.Failed))
            {
                return false;
            }

            // A whole-object replace: a caller moving an entry on from Failed simply builds the new
            // Valued/ValuedManually valuation without FailedAt/FailureReason set, which is what clears them.
            // Delivered is enforced here rather than trusted from valuation itself: a caller that builds
            // the replacement by copying an existing delivered row — the natural way to carry its other
            // fields forward — must not have that copy's flag survive into the resolved row.
            _state.Valuations[index] = valuation.Delivered ? WithDeliveredCleared(valuation) : valuation;

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

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, transactionHash, StringComparison.Ordinal));
            if (index < 0 || _state.Valuations[index].State != ValuationState.Pending)
            {
                return;
            }

            PaymentValuation previous = _state.Valuations[index];
            _state.Valuations[index] = WithFailure(previous, reason, failedAt);

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

    public async Task<bool> SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, transactionHash, StringComparison.Ordinal));
            if (index < 0 || _state.Valuations[index].State is not (ValuationState.Pending or ValuationState.Failed))
            {
                return false;
            }

            PaymentValuation previous = _state.Valuations[index];
            _state.Valuations[index] = WithWriteOff(previous, reason, writtenOffAt);

            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _state.Valuations[index] = previous;
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // _state.Valuations is already in enqueue order (append-only), so a stable OrderBy over it
            // makes ties on FailedAt fall back to enqueue order — matching PostgresQuoteStore's
            // "ORDER BY failed_at, queued_seq" rather than the unrelated order entries happened to fail in.
            return _state.Valuations
                .Where(entry => entry.State == ValuationState.Failed)
                .OrderBy(entry => entry.FailedAt)
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Valuations.Count(entry => entry.State == ValuationState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
        DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // _state.Valuations is already in enqueue order (append-only), so a stable OrderBy over it
            // makes ties on EnqueuedAt fall back to enqueue order — matching PostgresQuoteStore's
            // "ORDER BY enqueued_at, queued_seq".
            return _state.Valuations
                .Where(entry => entry.State is ValuationState.Pending or ValuationState.Failed
                    && entry.EnqueuedAt <= olderThan)
                .OrderBy(entry => entry.EnqueuedAt)
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Valuations.Count(
                entry => entry.State is ValuationState.Pending or ValuationState.Failed && entry.EnqueuedAt <= olderThan);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        TakeAsync(limit, entry => entry.State != ValuationState.Pending && !entry.Delivered, cancellationToken);

    public async Task<bool> MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionHash);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _state.Valuations.FindIndex(
                v => string.Equals(v.TransactionHash, transactionHash, StringComparison.Ordinal));
            // Only applies when the row is still in the state the caller actually handed to the host
            // handler — otherwise an operator's resolution racing a slow delivery call for the stale
            // content would be marked delivered on the resolution's behalf, and the resolution itself would
            // never reach the host. Left alone here, the row stays undelivered for the next pass.
            if (index < 0 || _state.Valuations[index].State != deliveredState)
            {
                return false;
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

            return true;
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

    private static PaymentValuation Delivered(PaymentValuation entry) => new PaymentValuation
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
