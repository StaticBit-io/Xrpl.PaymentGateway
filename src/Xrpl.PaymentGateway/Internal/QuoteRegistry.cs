using System.Collections.Concurrent;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The configured pairs and the liquidity snapshot currently held for each.
/// </summary>
/// <remarks>
/// Snapshots live in memory only. They are worthless after a restart — a book from before the process
/// died prices nothing — so persisting them would buy a stale number and the illusion of continuity.
/// What is persisted is the metadata around them, which is what an operator needs to see.
/// </remarks>
internal sealed class QuoteRegistry
{
    private readonly ConcurrentDictionary<string, IQuoteSnapshot> _snapshots =
        new ConcurrentDictionary<string, IQuoteSnapshot>(StringComparer.Ordinal);

    /// <summary>Ticks of the last full refresh cycle's duration, or -1 when no cycle has completed yet.</summary>
    private long _lastCycleDurationTicks = -1;

    /// <summary>
    /// Whether the collector's most recent persist attempt actually reached the store. Backed by an int
    /// rather than a bool so it can share the same Interlocked pattern as <see cref="_lastCycleDurationTicks"/>.
    /// Starts true: nothing has failed before the first attempt, and the freshness fields already read as
    /// unhealthy until a first cycle completes, so there is no window where this default alone hides a problem.
    /// </summary>
    private int _lastWriteSucceeded = 1;

    public QuoteRegistry(IReadOnlyList<QuotePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Pairs = pairs;
    }

    public IReadOnlyList<QuotePair> Pairs { get; }

    /// <summary>The pair that prices this asset, or null when none is configured for it.</summary>
    public QuotePair? FindPair(string currency, string? issuer)
    {
        foreach (QuotePair pair in Pairs)
        {
            if (pair.Matches(currency, issuer))
            {
                return pair;
            }
        }

        return null;
    }

    /// <summary>The snapshot held for a pair, or null when there is none right now.</summary>
    public IQuoteSnapshot? GetSnapshot(string pairKey) =>
        _snapshots.TryGetValue(pairKey, out IQuoteSnapshot? snapshot) ? snapshot : null;

    /// <summary>Replaces the snapshot for a pair. Null drops it, which is what an empty pair means.</summary>
    public void SetSnapshot(string pairKey, IQuoteSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _snapshots.TryRemove(pairKey, out _);
            return;
        }

        _snapshots[pairKey] = snapshot;
    }

    /// <summary>
    /// How long the collector's last full refresh cycle actually took, or null before the first one
    /// completes. Measured, not predicted — see <c>QuoteSchedule.CycleFitsInInterval</c>, which only
    /// checks the spacing between pairs and knows nothing about how long a capture itself runs.
    /// </summary>
    public TimeSpan? LastCycleDuration
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastCycleDurationTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>Records how long a full refresh cycle just took.</summary>
    public void SetLastCycleDuration(TimeSpan duration) =>
        Interlocked.Exchange(ref _lastCycleDurationTicks, duration.Ticks);

    /// <summary>
    /// Whether the collector's most recent attempt to persist a quote actually succeeded. A store whose
    /// writes hang or throw while its reads keep answering would otherwise be invisible: captures keep
    /// updating the in-memory snapshot every cycle, so the freshness fields alone would report healthy for
    /// as long as the process stays up, however long persistence has been broken.
    /// </summary>
    public bool LastWriteSucceeded => Interlocked.CompareExchange(ref _lastWriteSucceeded, 0, 0) != 0;

    /// <summary>Records whether the collector's most recent persist attempt reached the store.</summary>
    public void SetLastWriteSucceeded(bool succeeded) =>
        Interlocked.Exchange(ref _lastWriteSucceeded, succeeded ? 1 : 0);
}
