using System.Globalization;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The <c>complete_ledgers</c> string a node reports in <c>server_info</c>, parsed into ranges.
/// A node that does not hold the whole span we intend to replay would answer <c>account_tx</c> with a
/// silently partial result, so we ask before we trust it.
/// </summary>
internal sealed class LedgerRangeSet
{
    private readonly IReadOnlyList<LedgerRange> _ranges;

    private LedgerRangeSet(IReadOnlyList<LedgerRange> ranges) => _ranges = ranges;

    /// <summary>A set that covers nothing.</summary>
    public static LedgerRangeSet Empty { get; } = new LedgerRangeSet(Array.Empty<LedgerRange>());

    /// <summary>
    /// Parses forms rippled emits: "empty", "32570-99383752", "24900901-24900984,24901116-24901158",
    /// and a bare single index. Returns false on anything else, leaving <paramref name="result"/> covering nothing.
    /// </summary>
    public static bool TryParse(string? completeLedgers, out LedgerRangeSet result)
    {
        result = Empty;

        if (string.IsNullOrWhiteSpace(completeLedgers))
        {
            return false;
        }

        string trimmed = completeLedgers.Trim();
        if (trimmed.Equals("empty", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        List<LedgerRange> ranges = new List<LedgerRange>();
        foreach (string part in trimmed.Split(','))
        {
            string chunk = part.Trim();
            if (chunk.Length == 0)
            {
                continue;
            }

            int dash = chunk.IndexOf('-');
            if (dash < 0)
            {
                if (!uint.TryParse(chunk, NumberStyles.None, CultureInfo.InvariantCulture, out uint single))
                {
                    return false;
                }

                ranges.Add(new LedgerRange(single, single));
                continue;
            }

            if (!uint.TryParse(chunk.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint from)
                || !uint.TryParse(chunk.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint to)
                || to < from)
            {
                return false;
            }

            ranges.Add(new LedgerRange(from, to));
        }

        if (ranges.Count == 0)
        {
            return false;
        }

        result = new LedgerRangeSet(ranges);
        return true;
    }

    /// <summary>
    /// True when one contiguous reported range contains the whole span. Adjacent ranges are deliberately not
    /// merged: rippled reports contiguous history as one range, so two ranges mean a real gap between them.
    /// </summary>
    public bool Covers(uint from, uint to)
    {
        if (to < from)
        {
            return true;
        }

        foreach (LedgerRange range in _ranges)
        {
            if (range.From <= from && range.To >= to)
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct LedgerRange
    {
        public LedgerRange(uint from, uint to)
        {
            From = from;
            To = to;
        }

        public uint From { get; }

        public uint To { get; }
    }
}
