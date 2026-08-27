using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// What every <see cref="IPaymentStore"/> must do, whatever it is built on. The interface's two hard
/// requirements — atomic tag allocation and uniqueness of the transaction hash — are easy to state and
/// easy to implement wrongly, so they are checked here rather than in each implementation's own tests.
/// A new store is validated by deriving from this class and nothing else.
/// </summary>
public abstract class PaymentStoreContract
{
    /// <summary>Creates a store with nothing in it.</summary>
    protected abstract Task<IPaymentStore> CreateAsync(uint firstDestinationTag = 1);

    /// <summary>
    /// Opens the same underlying storage a second time, as a restart would. Returns null for a store that
    /// cannot outlive its process, which skips the durability checks rather than pretending they passed.
    /// </summary>
    protected virtual Task<IPaymentStore?> ReopenAsync(IPaymentStore store) =>
        Task.FromResult<IPaymentStore?>(null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentRecord Record(string hash, uint? tag = null, decimal value = 1m) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        DestinationTag = tag,
        Currency = "XRP",
        Issuer = null,
        Value = value,
        LedgerIndex = 10,
        ProcessedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task TheFirstBuyerGetsTheConfiguredFirstTag()
    {
        IPaymentStore store = await CreateAsync(firstDestinationTag: 7);

        Assert.Equal(7u, await store.GetOrAssignTagAsync("buyer-1", Ct));
    }

    [Fact]
    public async Task AReturningBuyerGetsTheSameTag()
    {
        IPaymentStore store = await CreateAsync();

        uint first = await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.GetOrAssignTagAsync("buyer-2", Ct);

        Assert.Equal(first, await store.GetOrAssignTagAsync("buyer-1", Ct));
    }

    [Fact]
    public async Task TagsAreSequentialAndResolveBackToTheirBuyer()
    {
        IPaymentStore store = await CreateAsync();

        uint one = await store.GetOrAssignTagAsync("a", Ct);
        uint two = await store.GetOrAssignTagAsync("b", Ct);

        Assert.Equal(1u, one);
        Assert.Equal(2u, two);
        Assert.Equal("a", await store.FindBuyerByTagAsync(one, Ct));
        Assert.Equal("b", await store.FindBuyerByTagAsync(two, Ct));
    }

    [Fact]
    public async Task ATagThatWasNeverIssuedResolvesToNobody()
    {
        IPaymentStore store = await CreateAsync();

        Assert.Null(await store.FindBuyerByTagAsync(4242u, Ct));
    }

    [Fact]
    public async Task ConcurrentAllocationForOneBuyerYieldsOneTag()
    {
        IPaymentStore store = await CreateAsync();

        uint[] tags = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.GetOrAssignTagAsync("buyer-1", Ct), Ct)));

        Assert.Single(tags.Distinct());
    }

    [Fact]
    public async Task ConcurrentAllocationForDistinctBuyersNeverSharesATag()
    {
        IPaymentStore store = await CreateAsync();

        uint[] tags = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => store.GetOrAssignTagAsync($"buyer-{i}", Ct), Ct)));

        // One tag reaching two buyers would credit one buyer's payment to the other.
        Assert.Equal(32, tags.Distinct().Count());
    }

    [Fact]
    public async Task ANewPaymentIsAcceptedAndADuplicateIsRefused()
    {
        IPaymentStore store = await CreateAsync();

        Assert.True(await store.TryAddPaymentAsync(Record("HASH-1"), Ct));
        Assert.False(await store.TryAddPaymentAsync(Record("HASH-1"), Ct));
    }

    [Fact]
    public async Task ConcurrentWritesOfOneHashAcceptExactlyOne()
    {
        IPaymentStore store = await CreateAsync();

        bool[] accepted = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.TryAddPaymentAsync(Record("HASH-RACE"), Ct), Ct)));

        // Two accepted writes would deliver one payment to the host twice.
        Assert.Single(accepted, accepted => accepted);
    }

    [Fact]
    public async Task UnhandledPaymentsComeBackOldestFirstAndLeaveWhenMarked()
    {
        IPaymentStore store = await CreateAsync();
        await store.TryAddPaymentAsync(Record("A"), Ct);
        await store.TryAddPaymentAsync(Record("B"), Ct);

        IReadOnlyList<PaymentRecord> before = await store.GetUnhandledPaymentsAsync(10, Ct);
        Assert.Equal(new[] { "A", "B" }, before.Select(p => p.TransactionHash));

        await store.MarkHandledAsync("A", Ct);

        IReadOnlyList<PaymentRecord> after = await store.GetUnhandledPaymentsAsync(10, Ct);
        Assert.Equal(new[] { "B" }, after.Select(p => p.TransactionHash));
    }

    [Fact]
    public async Task TheUnhandledLimitIsRespected()
    {
        IPaymentStore store = await CreateAsync();
        for (int i = 0; i < 5; i++)
        {
            await store.TryAddPaymentAsync(Record($"H{i}"), Ct);
        }

        Assert.Equal(2, (await store.GetUnhandledPaymentsAsync(2, Ct)).Count);
    }

    [Fact]
    public async Task MarkingAHashTheStoreNeverSawIsNotAnError()
    {
        IPaymentStore store = await CreateAsync();

        await store.MarkHandledAsync("NEVER-SEEN", Ct);
    }

    [Fact]
    public async Task ARecordSurvivesTheRoundTripIntactRatherThanApproximately()
    {
        IPaymentStore store = await CreateAsync();
        PaymentRecord written = new PaymentRecord
        {
            TransactionHash = "ROUND-TRIP",
            TransactionType = "Payment",
            Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
            DestinationTag = uint.MaxValue,
            Currency = "USD",
            Issuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
            // Sixteen significant digits, which is what the ledger carries.
            Value = 1234.567890123456m,
            LedgerIndex = uint.MaxValue,
            ProcessedAt = new DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.Zero),
        };

        await store.TryAddPaymentAsync(written, Ct);

        PaymentRecord read = Assert.Single(await store.GetUnhandledPaymentsAsync(10, Ct));
        Assert.Equal(written.TransactionHash, read.TransactionHash);
        Assert.Equal(written.TransactionType, read.TransactionType);
        Assert.Equal(written.Sender, read.Sender);
        Assert.Equal(written.DestinationTag, read.DestinationTag);
        Assert.Equal(written.Currency, read.Currency);
        Assert.Equal(written.Issuer, read.Issuer);
        Assert.Equal(written.Value, read.Value);
        Assert.Equal(written.LedgerIndex, read.LedgerIndex);
        Assert.Equal(written.ProcessedAt, read.ProcessedAt);
    }

    [Fact]
    public async Task APaymentWithoutATagRoundTripsAsHavingNone()
    {
        IPaymentStore store = await CreateAsync();
        await store.TryAddPaymentAsync(Record("NO-TAG", tag: null), Ct);

        Assert.Null(Assert.Single(await store.GetUnhandledPaymentsAsync(10, Ct)).DestinationTag);
    }

    [Fact]
    public async Task TheCursorStartsEmptyAndRoundTrips()
    {
        IPaymentStore store = await CreateAsync();

        Assert.Null(await store.GetLastProcessedLedgerAsync(Ct));

        await store.SetLastProcessedLedgerAsync(4242u, Ct);

        Assert.Equal(4242u, await store.GetLastProcessedLedgerAsync(Ct));
    }

    [Fact]
    public async Task TheCursorTakesTheWholeLedgerRange()
    {
        IPaymentStore store = await CreateAsync();

        await store.SetLastProcessedLedgerAsync(uint.MaxValue, Ct);

        Assert.Equal(uint.MaxValue, await store.GetLastProcessedLedgerAsync(Ct));
    }

    [Fact]
    public async Task EverythingSurvivesAReopen()
    {
        IPaymentStore store = await CreateAsync();
        uint tag = await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.TryAddPaymentAsync(Record("KEEP-ME", tag: tag, value: 12.5m), Ct);
        await store.SetLastProcessedLedgerAsync(900u, Ct);

        IPaymentStore? reopened = await ReopenAsync(store);
        if (reopened is null)
        {
            // An in-process store cannot outlive its process, and saying so beats a green tick.
            Assert.Skip("this store does not persist across a reopen");
            return;
        }

        Assert.Equal(900u, await reopened.GetLastProcessedLedgerAsync(Ct));
        Assert.Equal("buyer-1", await reopened.FindBuyerByTagAsync(tag, Ct));
        Assert.Equal(tag, await reopened.GetOrAssignTagAsync("buyer-1", Ct));
        Assert.False(await reopened.TryAddPaymentAsync(Record("KEEP-ME"), Ct));

        PaymentRecord kept = Assert.Single(await reopened.GetUnhandledPaymentsAsync(10, Ct));
        Assert.Equal(12.5m, kept.Value);
    }

    [Fact]
    public async Task ANewBuyerAfterAReopenDoesNotReuseATagAlreadyIssued()
    {
        IPaymentStore store = await CreateAsync();
        uint first = await store.GetOrAssignTagAsync("buyer-1", Ct);

        IPaymentStore? reopened = await ReopenAsync(store);
        if (reopened is null)
        {
            Assert.Skip("this store does not persist across a reopen");
            return;
        }

        // A counter that restarts from the beginning would hand a paid-up buyer's tag to somebody new.
        Assert.NotEqual(first, await reopened.GetOrAssignTagAsync("buyer-2", Ct));
    }
}
