using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class UnresolvedValuationAdminTests
{
    private const string PairKey = "XPM.rXPM/USD.rRLU";
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds the admin service and hands back the same store it was built against.</summary>
    private static (UnresolvedValuationAdmin Admin, InMemoryQuoteStore Store) BuildWithStore(DateTimeOffset? now = null)
    {
        InMemoryQuoteStore store = new InMemoryQuoteStore();
        return (new UnresolvedValuationAdmin(store, new FixedTimeProvider(now ?? Now)), store);
    }

    private static PaymentValuation Pending(string hash, decimal amount = 1000m, DateTimeOffset? enqueuedAt = null) => new PaymentValuation
    {
        TransactionHash = hash,
        PairKey = PairKey,
        Amount = amount,
        PaymentLedgerIndex = 901,
        DestinationTag = 42,
        EnqueuedAt = enqueuedAt ?? Now,
    };

    [Fact]
    public async Task ListUnresolvedAsyncReturnsPendingAndFailedEntriesWithATotalCount()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        DateTimeOffset old = Now.AddMinutes(-20);
        await store.TryEnqueueValuationAsync(Pending("STILL-PENDING", enqueuedAt: old), Ct);
        await store.TryEnqueueValuationAsync(Pending("FAILED1", enqueuedAt: old), Ct);
        await store.SaveValuationFailureAsync("FAILED1", "no liquidity", Now, Ct);
        await store.TryEnqueueValuationAsync(Pending("VALUED", enqueuedAt: old), Ct);
        await store.SaveValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "VALUED",
                PairKey = PairKey,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = old,
                State = ValuationState.Valued,
                ValuedAt = Now,
                QuoteAmount = 10m,
            },
            Ct);

        // Both Pending and Failed show up — neither is "the" boundary any more, and Valued is settled work
        // that must not keep showing up here.
        UnresolvedValuationPage page = await admin.ListUnresolvedAsync(10, 0, minAge: TimeSpan.Zero, Ct);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "STILL-PENDING", "FAILED1" }, page.Items.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task ListUnresolvedAsyncDefaultsToFifteenMinutesAndExcludesFresherEntries()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("JUST-QUEUED", enqueuedAt: Now), Ct);
        await store.TryEnqueueValuationAsync(Pending("STUCK", enqueuedAt: Now.AddMinutes(-20)), Ct);

        UnresolvedValuationPage page = await admin.ListUnresolvedAsync(10, 0, minAge: null, Ct);

        // A payment still working through an ordinary transient wait must not show up as something to
        // act on; only the one genuinely sitting past the default threshold does.
        Assert.Equal(new[] { "STUCK" }, page.Items.Select(v => v.TransactionHash));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListUnresolvedAsyncMinAgeCanBeOverridden()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("JUST-QUEUED", enqueuedAt: Now), Ct);

        // The default would hide this entry; an explicit zero minimum age shows everything unresolved.
        UnresolvedValuationPage page = await admin.ListUnresolvedAsync(10, 0, minAge: TimeSpan.Zero, Ct);

        Assert.Equal(new[] { "JUST-QUEUED" }, page.Items.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task ValueManuallyAsyncPricesTheRecordedAmountAtTheSuppliedRate()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
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
    public async Task ValueManuallyAsyncPricesAStillPendingEntryDirectly()
    {
        // The pipeline never classified this one as Failed — it may simply not have a snapshot yet — but
        // an operator can still price it: Pending is unresolved too, not only Failed.
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1", amount: 500m), Ct);

        await admin.ValueManuallyAsync("HASH1", 0.05m, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.ValuedManually, read!.State);
        Assert.Equal(25m, read.QuoteAmount);
    }

    [Fact]
    public async Task ValueManuallyAsyncRejectsAHashThatIsNotUnresolved()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.ValueManuallyAsync("HASH1", 0.01m, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.ValueManuallyAsync("NO-SUCH-HASH", 0.01m, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValueManuallyAsyncRejectsANonPositiveRate(decimal rate)
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => admin.ValueManuallyAsync("HASH1", rate, Ct));
    }

    [Fact]
    public async Task ValueManuallyAsyncThrowsRatherThanReportingSuccessWhenTheStoreLosesTheRace()
    {
        // RequireUnresolvedAsync's read says the row is still Failed, but SaveValuationAsync's guarded
        // write reaches a row that has since moved on — another operator's call landed first. The store
        // reports that honestly by returning false; the admin must not swallow it and claim success.
        InMemoryQuoteStore inner = new InMemoryQuoteStore();
        await inner.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await inner.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);
        UnresolvedValuationAdmin admin = new UnresolvedValuationAdmin(
            new SaveWriteLosesTheRaceStore(inner), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ValueManuallyAsync("HASH1", 0.05m, Ct));

        // The row is untouched — still Failed, not silently moved to ValuedManually.
        PaymentValuation? read = await inner.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.Failed, read!.State);
    }

    [Fact]
    public async Task WriteOffAsyncMovesTheEntryToWrittenOffAndKeepsTheReason()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
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
    public async Task WriteOffAsyncCanWriteOffAStillPendingEntryDirectly()
    {
        // Same widening as ValueManuallyAsync: an operator can decide a still-Pending entry will never be
        // credited without first waiting for the pipeline to fail it.
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
        await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct);

        await admin.WriteOffAsync("HASH1", "dust", Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read!.State);
        Assert.Null(read.FailedAt);
        Assert.Null(read.FailureReason);
    }

    [Fact]
    public async Task WriteOffAsyncRejectsAHashThatIsNotUnresolved()
    {
        (UnresolvedValuationAdmin admin, InMemoryQuoteStore store) = BuildWithStore();
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

    [Fact]
    public async Task WriteOffAsyncThrowsRatherThanReportingSuccessWhenTheStoreLosesTheRace()
    {
        InMemoryQuoteStore inner = new InMemoryQuoteStore();
        await inner.TryEnqueueValuationAsync(Pending("HASH1"), Ct);
        await inner.SaveValuationFailureAsync("HASH1", "no liquidity", Now, Ct);
        UnresolvedValuationAdmin admin = new UnresolvedValuationAdmin(
            new SaveWriteLosesTheRaceStore(inner), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.WriteOffAsync("HASH1", "dust", Ct));

        PaymentValuation? read = await inner.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.Failed, read!.State);
    }

    /// <summary>
    /// An <see cref="IQuoteStore"/> whose reads reflect a real, wrapped store honestly but whose guarded
    /// writes always report "did not apply" without touching anything — the shape a genuine lost race takes
    /// from the admin service's point of view: the row it read is real, but by the time the write reaches
    /// the store it no longer matches.
    /// </summary>
    private sealed class SaveWriteLosesTheRaceStore : IQuoteStore
    {
        private readonly IQuoteStore _inner;

        public SaveWriteLosesTheRaceStore(IQuoteStore inner) => _inner = inner;

        public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) =>
            _inner.SaveQuoteAsync(quote, cancellationToken);

        public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) =>
            _inner.GetQuoteAsync(pairKey, cancellationToken);

        public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
            _inner.GetQuotesAsync(cancellationToken);

        public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken) =>
            _inner.TryEnqueueValuationAsync(pending, cancellationToken);

        public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
            string pairKey, int limit, CancellationToken cancellationToken) =>
            _inner.GetPendingValuationsAsync(pairKey, limit, cancellationToken);

        public Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(CancellationToken cancellationToken) =>
            _inner.GetPendingValuationBreakdownAsync(cancellationToken);

        public Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task SaveValuationFailureAsync(
            string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken) =>
            _inner.SaveValuationFailureAsync(transactionHash, reason, failedAt, cancellationToken);

        public Task<bool> SaveWriteOffAsync(
            string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
            int limit, int offset, CancellationToken cancellationToken) =>
            _inner.GetFailedValuationsAsync(limit, offset, cancellationToken);

        public Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken) =>
            _inner.CountFailedValuationsAsync(cancellationToken);

        public Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
            DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken) =>
            _inner.GetUnresolvedValuationsAsync(olderThan, limit, offset, cancellationToken);

        public Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) =>
            _inner.CountUnresolvedValuationsAsync(olderThan, cancellationToken);

        public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
            _inner.GetUndeliveredValuationsAsync(limit, cancellationToken);

        public Task<bool> MarkValuationDeliveredAsync(
            string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken) =>
            _inner.MarkValuationDeliveredAsync(transactionHash, deliveredState, cancellationToken);

        public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
            _inner.GetValuationAsync(transactionHash, cancellationToken);
    }
}
