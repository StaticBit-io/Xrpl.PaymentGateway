namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// How long to wait before the next connection attempt. Kept apart from the monitor so the escalation
/// rule can be checked without waiting for real delays to elapse.
/// </summary>
internal sealed class ReconnectBackoff
{
    /// <summary>Doubling past this would overflow long before it changed the capped result.</summary>
    private const int MaxExponent = 16;

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<double> _jitter;

    /// <param name="baseDelay">The wait after the first failure, and the floor for every later one.</param>
    /// <param name="maxDelay">The ceiling the doubling is capped at.</param>
    /// <param name="jitter">Returns a value in [0, 1). Injected so tests can pin the delay exactly.</param>
    public ReconnectBackoff(TimeSpan baseDelay, TimeSpan maxDelay, Func<double>? jitter = null)
    {
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <summary>Failed attempts since the last session that did its job.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Clears the escalation. Only a session that ran long enough to be working should call this: a node
    /// that accepts the socket and drops it immediately would otherwise be retried forever at the base
    /// delay.
    /// </summary>
    public void RecordProductiveSession() => ConsecutiveFailures = 0;

    public void RecordFailure() => ConsecutiveFailures++;

    /// <summary>
    /// Doubles per consecutive failure, capped, then spread by ±25% so a pool of clients that lost the
    /// same node does not come back in lockstep.
    /// </summary>
    public TimeSpan NextDelay()
    {
        if (ConsecutiveFailures <= 0)
        {
            return _baseDelay;
        }

        double exponent = Math.Min(ConsecutiveFailures - 1, MaxExponent);
        double milliseconds = Math.Min(
            _baseDelay.TotalMilliseconds * Math.Pow(2, exponent),
            _maxDelay.TotalMilliseconds);
        double jittered = milliseconds * (0.75 + (_jitter() * 0.5));

        return TimeSpan.FromMilliseconds(Math.Min(jittered, _maxDelay.TotalMilliseconds));
    }
}
