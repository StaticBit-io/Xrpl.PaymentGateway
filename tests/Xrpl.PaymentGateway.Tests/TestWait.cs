using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>Waits for a background service to reach a state, instead of sleeping and hoping.</summary>
public static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string description, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"timed out after {timeoutMs} ms waiting for: {description}");
    }
}
