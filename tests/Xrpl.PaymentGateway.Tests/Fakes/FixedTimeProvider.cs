namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>A clock frozen at a chosen instant, so assertions on timestamps are exact.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
