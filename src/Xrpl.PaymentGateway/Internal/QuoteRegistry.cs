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

    /// <summary>Ticks of when the cycle currently in progress started, or -1 when none is in progress.</summary>
    private long _cycleStartedTicks = -1;

    /// <summary>
    /// Whether the collector's most recent persist attempt reached the store, per pair. Absent means no
    /// write has been attempted for that pair yet — not a failure, since nothing has happened to fail.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _lastWriteSucceededByPair =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

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
    /// <remarks>
    /// Only ever reflects a cycle that finished. <see cref="CycleStartedAt"/> is the other half a caller
    /// needs to notice a cycle that is running long right now — see <c>QuoteHealth.CheckAsync</c>, which
    /// combines the two so the health report's own <c>LastCycleDuration</c> does not go quiet exactly when
    /// a stall makes it matter most.
    /// </remarks>
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

    /// <summary>When the cycle currently in progress started, or null when none is in progress (before the collector's first pass).</summary>
    public DateTimeOffset? CycleStartedAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _cycleStartedTicks);
            return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Records that a new refresh cycle has started.</summary>
    public void SetCycleStarted(DateTimeOffset startedAt) =>
        Interlocked.Exchange(ref _cycleStartedTicks, startedAt.UtcTicks);

    /// <summary>
    /// Whether every pair's most recent persist attempt reached the store — <see cref="PairsFailingToPersist"/>
    /// being zero, spelled as a bool. Starts true: nothing has failed before any attempt, and the freshness
    /// fields already read as unhealthy until a first cycle completes, so there is no window where this
    /// default alone hides a problem.
    /// </summary>
    public bool LastWriteSucceeded => PairsFailingToPersist == 0;

    /// <summary>
    /// How many pairs' most recent persist attempt failed to reach the store, right now. Per pair rather
    /// than one process-wide flag: a store rejecting writes for two pairs out of three must not be erased
    /// by the third pair's next successful write — every other count in the health report is per pair, and
    /// this is the write-side one.
    /// </summary>
    public int PairsFailingToPersist => _lastWriteSucceededByPair.Count(pair => !pair.Value);

    /// <summary>Records whether the collector's most recent persist attempt for one pair reached the store.</summary>
    public void SetLastWriteSucceeded(string pairKey, bool succeeded) =>
        _lastWriteSucceededByPair[pairKey] = succeeded;
}
