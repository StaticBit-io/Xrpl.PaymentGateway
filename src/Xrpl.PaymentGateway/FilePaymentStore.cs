using System.Text.Json;
using System.Text.Json.Serialization;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>
/// An <see cref="IPaymentStore"/> backed by a single JSON file. It exists because the design promises the
/// host may record payments anywhere, and a claim like that is worth demonstrating rather than asserting:
/// a gateway can be run with no database at all.
/// </summary>
/// <remarks>
/// <para>
/// Suitable for a single process — which is the only supported way to run the monitor anyway. It takes no
/// cross-process lock, so two processes pointed at one file would overwrite each other's work. For more
/// than that, use a database.
/// </para>
/// <para>
/// Every write rewrites the whole file through a temporary file and an atomic replace, so a crash
/// mid-write leaves the previous state rather than a truncated one. That costs a full rewrite per payment,
/// which is the right trade at the volume a single receiving account sees and the wrong one at scale.
/// </para>
/// </remarks>
public sealed class FilePaymentStore : IPaymentStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly State _state;
    private bool _disposed;

    /// <param name="path">The file to keep the state in. Created, with its directory, on first write.</param>
    /// <param name="firstDestinationTag">
    /// The tag for the first buyer, used only when the file does not exist yet. Zero is rejected: many
    /// wallets read it as "no tag".
    /// </param>
    public FilePaymentStore(string path, uint firstDestinationTag = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (firstDestinationTag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDestinationTag), "destination tag 0 is not issued");
        }

        _path = Path.GetFullPath(path);
        _state = Load(_path) ?? new State { NextTag = firstDestinationTag };
    }

    public async Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.TagsByBuyer.TryGetValue(buyerId, out uint existing))
            {
                return existing;
            }

            if (_state.NextTag == uint.MaxValue)
            {
                throw new InvalidOperationException("the destination tag space is exhausted");
            }

            uint assigned = _state.NextTag;
            _state.NextTag = assigned + 1;
            _state.TagsByBuyer[buyerId] = assigned;

            // Persisted before it is returned: a tag handed to a buyer and then forgotten in a crash would
            // be issued again to somebody else, and the first buyer's payment would land on the wrong one.
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return assigned;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (KeyValuePair<string, uint> pair in _state.TagsByBuyer)
            {
                if (pair.Value == tag)
                {
                    return pair.Key;
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Payments.Any(p => string.Equals(p.Record.TransactionHash, record.TransactionHash, StringComparison.Ordinal)))
            {
                return false;
            }

            _state.Payments.Add(new StoredPayment { Record = record, Handled = false });
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredPayment? found = _state.Payments.FirstOrDefault(
                p => string.Equals(p.Record.TransactionHash, transactionHash, StringComparison.Ordinal));

            if (found is null || found.Handled)
            {
                return;
            }

            found.Handled = true;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Payments
                .Where(p => !p.Handled)
                .Take(limit)
                .Select(p => p.Record)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Cursor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state.Cursor = ledgerIndex;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
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
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";

        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, _state, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private sealed class State
    {
        public uint NextTag { get; set; } = 1;

        public uint? Cursor { get; set; }

        public Dictionary<string, uint> TagsByBuyer { get; set; } = new Dictionary<string, uint>(StringComparer.Ordinal);

        public List<StoredPayment> Payments { get; set; } = new List<StoredPayment>();
    }

    private sealed class StoredPayment
    {
        public required PaymentRecord Record { get; set; }

        public bool Handled { get; set; }
    }
}
