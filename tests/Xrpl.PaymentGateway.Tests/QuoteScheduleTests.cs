using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteScheduleTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public void FewPairsAreSpreadAcrossTheWholeInterval()
    {
        // Three pairs in a minute: no reason to bunch them into the first thirty seconds.
        Assert.Equal(TimeSpan.FromSeconds(20), QuoteSchedule.PairDelay(3, Minute, TenSeconds));
    }

    [Fact]
    public void TheSpreadNeverGoesBelowTheMinimumStagger()
    {
        // Twenty pairs in a minute would want three seconds apart; the floor says ten.
        Assert.Equal(TenSeconds, QuoteSchedule.PairDelay(20, Minute, TenSeconds));
    }

    [Fact]
    public void ExactlyEnoughPairsLandOnTheFloor()
    {
        Assert.Equal(TenSeconds, QuoteSchedule.PairDelay(6, Minute, TenSeconds));
    }

    [Fact]
    public void OnePairWaitsOutTheWholeInterval()
    {
        Assert.Equal(Minute, QuoteSchedule.PairDelay(1, Minute, TenSeconds));
    }

    [Fact]
    public void NoPairsMeansNoDelayToCompute()
    {
        Assert.Equal(TimeSpan.Zero, QuoteSchedule.PairDelay(0, Minute, TenSeconds));
    }

    [Fact]
    public void ACycleThatCannotFitTheIntervalIsReported()
    {
        // This is what the validator warns about at startup, and what the health report shows later.
        Assert.True(QuoteSchedule.CycleFitsInInterval(6, Minute, TenSeconds));
        Assert.False(QuoteSchedule.CycleFitsInInterval(7, Minute, TenSeconds));
    }
}
