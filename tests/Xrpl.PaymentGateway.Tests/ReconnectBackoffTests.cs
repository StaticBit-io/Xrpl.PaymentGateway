using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ReconnectBackoffTests
{
    // Mid-range jitter is a factor of exactly 1.0, so the delays below are the escalation itself.
    private static ReconnectBackoff Create(int baseMs = 100, int maxMs = 3200) =>
        new ReconnectBackoff(TimeSpan.FromMilliseconds(baseMs), TimeSpan.FromMilliseconds(maxMs), () => 0.5);

    [Fact]
    public void TheFirstAttemptWaitsTheBaseDelay()
    {
        ReconnectBackoff backoff = Create();

        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextDelay());
    }

    [Fact]
    public void EachConsecutiveFailureDoublesTheWait()
    {
        ReconnectBackoff backoff = Create();

        backoff.RecordFailure();
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextDelay());

        backoff.RecordFailure();
        Assert.Equal(TimeSpan.FromMilliseconds(200), backoff.NextDelay());

        backoff.RecordFailure();
        Assert.Equal(TimeSpan.FromMilliseconds(400), backoff.NextDelay());
    }

    [Fact]
    public void TheWaitIsCappedNoMatterHowManyFailuresPileUp()
    {
        ReconnectBackoff backoff = Create();

        for (int i = 0; i < 100; i++)
        {
            backoff.RecordFailure();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(3200), backoff.NextDelay());
    }

    [Fact]
    public void AProductiveSessionClearsTheEscalation()
    {
        ReconnectBackoff backoff = Create();
        backoff.RecordFailure();
        backoff.RecordFailure();
        backoff.RecordFailure();

        backoff.RecordProductiveSession();

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextDelay());
    }

    [Fact]
    public void JitterSpreadsTheWaitByAQuarterEitherWay()
    {
        ReconnectBackoff earliest = new ReconnectBackoff(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3200), () => 0.0);
        ReconnectBackoff latest = new ReconnectBackoff(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3200), () => 0.999999);
        earliest.RecordFailure();
        earliest.RecordFailure();
        latest.RecordFailure();
        latest.RecordFailure();

        // A pool of clients that lost the same node must not come back in lockstep.
        Assert.Equal(TimeSpan.FromMilliseconds(150), earliest.NextDelay());
        Assert.InRange(latest.NextDelay(), TimeSpan.FromMilliseconds(249), TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void JitterNeverPushesTheWaitPastTheCap()
    {
        ReconnectBackoff backoff = new ReconnectBackoff(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3200), () => 0.999999);

        for (int i = 0; i < 20; i++)
        {
            backoff.RecordFailure();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(3200), backoff.NextDelay());
    }
}
