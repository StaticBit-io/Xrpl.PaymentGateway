namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// How far apart to space pair refreshes.
/// </summary>
/// <remarks>
/// Pure arithmetic, kept out of the loop that uses it so the pacing can be checked without a clock.
/// Spreading evenly rather than firing a burst and sleeping is the point: the goal was never the interval
/// itself but not hitting the node with every pair at once.
/// </remarks>
internal static class QuoteSchedule
{
    /// <summary>Delay between two consecutive pair refreshes.</summary>
    public static TimeSpan PairDelay(int pairCount, TimeSpan interval, TimeSpan minimumStagger)
    {
        if (pairCount <= 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan even = interval / pairCount;
        return even > minimumStagger ? even : minimumStagger;
    }

    /// <summary>Whether a full cycle at that spacing finishes within the interval.</summary>
    public static bool CycleFitsInInterval(int pairCount, TimeSpan interval, TimeSpan minimumStagger) =>
        pairCount <= 0 || PairDelay(pairCount, interval, minimumStagger) * pairCount <= interval;
}
