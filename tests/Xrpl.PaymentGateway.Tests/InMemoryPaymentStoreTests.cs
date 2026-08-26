using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class InMemoryPaymentStoreTests
{
    private static PaymentRecord Record(string hash, uint? tag = null) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = tag,
        Currency = "XRP",
        Value = 1m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task FirstBuyerGetsTheConfiguredFirstTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore(firstDestinationTag: 7);

        uint tag = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(7u, tag);
    }

    [Fact]
    public async Task AReturningBuyerGetsTheSameTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        uint first = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);
        await store.GetOrAssignTagAsync("buyer-2", TestContext.Current.CancellationToken);
        uint again = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task TagsAreSequentialAndNeverShared()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        uint one = await store.GetOrAssignTagAsync("a", TestContext.Current.CancellationToken);
        uint two = await store.GetOrAssignTagAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal(1u, one);
        Assert.Equal(2u, two);
        Assert.Equal("a", await store.FindBuyerByTagAsync(one, TestContext.Current.CancellationToken));
        Assert.Equal("b", await store.FindBuyerByTagAsync(two, TestContext.Current.CancellationToken));
        Assert.Null(await store.FindBuyerByTagAsync(999u, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentAllocationForOneBuyerYieldsOneTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Task<uint>[] calls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken)))
            .ToArray();
        uint[] tags = await Task.WhenAll(calls);

        Assert.Single(tags.Distinct());
    }

    [Fact]
    public async Task AddingTheSameHashTwiceReturnsFalseTheSecondTime()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Assert.True(await store.TryAddPaymentAsync(Record("HASH-1"), TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddPaymentAsync(Record("HASH-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnhandledPaymentsComeBackOldestFirstAndDisappearWhenMarked()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        await store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);
        await store.TryAddPaymentAsync(Record("B"), TestContext.Current.CancellationToken);

        IReadOnlyList<PaymentRecord> before = await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "A", "B" }, before.Select(p => p.TransactionHash));

        await store.MarkHandledAsync("A", TestContext.Current.CancellationToken);

        IReadOnlyList<PaymentRecord> after = await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "B" }, after.Select(p => p.TransactionHash));
    }

    [Fact]
    public async Task TheCursorStartsEmptyAndRoundTrips()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Assert.Null(await store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));

        await store.SetLastProcessedLedgerAsync(4242u, TestContext.Current.CancellationToken);

        Assert.Equal(4242u, await store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }
}
