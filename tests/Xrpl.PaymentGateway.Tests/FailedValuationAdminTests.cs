using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class FailedValuationAdminTests
{
    private const string PairKey = "XPM.rXPM/USD.rRLU";
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds the admin service and hands back the same store it was built against.</summary>
    private static (FailedValuationAdmin Admin, InMemoryQuoteStore Store) BuildWithStore(DateTimeOffset? now = null)
    {
        InMemoryQuoteStore store = new InMemoryQuoteStore();
        return (new FailedValuationAdmin(store, new FixedTimeProvider(now ?? Now)), store);
    }

    private static PaymentValuation Pending(string hash, decimal amount = 1000m) => new PaymentValuation
    {
        TransactionHash = hash,
        PairKey = PairKey,
        Amount = amount,
        PaymentLedgerIndex = 901,
        DestinationTag = 42,
        EnqueuedAt = Now,
    };

    [Fact]
    public async Task ListFailedAsyncReturnsOnlyFailedEntriesWithATotalCount()
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("STILL-PENDING"), Ct);
        await store.TryEnqueueValuationAsync(Pending("FAILED1"), Ct);
        await store.SaveValuationFailureAsync("FAILED1", "no liquidity", Now, Ct);
        await store.TryEnqueueValuationAsync(Pending("FAILED2"), Ct);
        await store.SaveValuationFailureAsync("FAILED2", "no liquidity", Now, Ct);

        FailedValuationPage page = await admin.ListFailedAsync(10, 0, Ct);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "FAILED1", "FAILED2" }, page.Items.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task ValueManuallyAsyncPricesTheRecordedAmountAtTheSuppliedRate()
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1", amount: 500m), Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        await admin.ValueManuallyAsync("HASH1", 0.05m, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.ValuedManually, read!.State);
        Assert.Equal(25m, read.QuoteAmount);
        Assert.Equal(0.05m, read.EffectivePrice);
        Assert.Equal(Now, read.ValuedAt);
        Assert.Null(read.FailedAt);
        Assert.Null(read.FailureReason);
        // Left undelivered: the normal ValuationWorker delivery pass is what hands this to the host, not
        // this service — one delivery mechanism, not two.
        Assert.False(read.Delivered);
        Assert.Single(await store.GetUndeliveredValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task ValueManuallyAsyncRejectsAHashThatIsNotFailed()
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("STILL-PENDING"), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.ValueManuallyAsync("STILL-PENDING", 0.01m, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.ValueManuallyAsync("NO-SUCH-HASH", 0.01m, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValueManuallyAsyncRejectsANonPositiveRate(decimal rate)
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => admin.ValueManuallyAsync("HASH1", rate, Ct));
    }

    [Fact]
    public async Task WriteOffAsyncMovesTheEntryToWrittenOffAndKeepsTheReason()
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        await admin.WriteOffAsync("HASH1", "dust", Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read!.State);
        Assert.Equal("dust", read.WriteOffReason);
        Assert.Equal(Now, read.WrittenOffAt);
        Assert.Null(read.QuoteAmount);
        Assert.False(read.Delivered);
        Assert.Single(await store.GetUndeliveredValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task WriteOffAsyncRejectsAHashThatIsNotFailed()
    {
        (FailedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await store.SaveValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "HASH1",
                PairKey = PairKey,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = Now,
                State = ValuationState.Valued,
                ValuedAt = Now,
                QuoteAmount = 10m,
            },
            Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.WriteOffAsync("HASH1", "dust", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.WriteOffAsync("NO-SUCH-HASH", "dust", Ct));
    }
}
