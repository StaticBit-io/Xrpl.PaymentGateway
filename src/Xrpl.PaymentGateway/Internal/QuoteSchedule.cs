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

    /// <summary>
    /// Whether the pauses between pairs at that spacing add up to less than the interval.
    /// </summary>
    /// <remarks>
    /// This measures spacing only — the sum of the delays this class hands out between pairs. It knows
    /// nothing about how long a capture itself takes, so it can answer true while the real refresh period
    /// runs several times longer than <paramref name="interval"/>. For the actual number, see the
    /// collector's own <c>QuoteRegistry.LastCycleDuration</c>, which times a whole cycle rather than
    /// predicting one from its schedule.
    /// </remarks>
    public static bool CycleFitsInInterval(int pairCount, TimeSpan interval, TimeSpan minimumStagger) =>
        pairCount <= 0 || PairDelay(pairCount, interval, minimumStagger) * pairCount <= interval;
}
