using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class StoreRetryPolicyTests
{
    private static StoreRetryPolicy CreatePolicy(Action<bool>? onAvailabilityChanged = null) =>
        new StoreRetryPolicy(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(5),
            TimeProvider.System,
            NullLogger.Instance,
            onAvailabilityChanged);

    [Fact]
    public async Task ASucceedingOperationRunsOnce()
    {
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy();

        int result = await policy.ExecuteAsync(_ => { calls++; return Task.FromResult(7); }, "op", TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFailingOperationIsRetriedUntilItSucceeds()
    {
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy();

        int result = await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 4 ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(11);
            },
            "op",
            TestContext.Current.CancellationToken);

        Assert.Equal(11, result);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task AvailabilityIsReportedFalseOnTheFirstFailureAndTrueOnRecovery()
    {
        List<bool> availability = new List<bool>();
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy(availability.Add);

        await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 2 ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(1);
            },
            "op",
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { false, true }, availability);
    }

    [Fact]
    public async Task CancellationStopsTheRetryLoop()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        StoreRetryPolicy policy = CreatePolicy();

        Task<int> pending = policy.ExecuteAsync(
            _ => Task.FromException<int>(new TimeoutException()),
            "op",
            cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
