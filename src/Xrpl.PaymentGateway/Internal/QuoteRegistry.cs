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
}
