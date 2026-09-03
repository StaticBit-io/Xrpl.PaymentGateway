using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteHealthTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (QuoteHealth Health, InMemoryQuoteStore Store, QuoteRegistry Registry) Build()
    {
        QuoteOptions options = new QuoteOptions
        {
            Pairs = new[] { Xpm },
            RefreshInterval = TimeSpan.FromMinutes(1),
        };
        InMemoryQuoteStore store = new InMemoryQuoteStore();
        QuoteRegistry registry = new QuoteRegistry(options.Pairs);
        QuoteHealth health = new QuoteHealth(
            Options.Create(options),
            store,
            registry,
            new FixedTimeProvider(Now),
            NullLogger<QuoteHealth>.Instance);

        return (health, store, registry);
    }

    private static StoredQuote Quote(int failures = 0, string? error = null) => new StoredQuote
    {
        PairKey = Xpm.Key,
        Currency = "XPM",
        Issuer = XpmIssuer,
        QuoteCurrency = "USD",
        QuoteIssuer = RlusdIssuer,
        MarginalPrice = 0.01m,
        LedgerIndex = 900,
        CapturedAt = Now.AddSeconds(-20),
        LastAttemptAt = Now,
        ConsecutiveFailures = failures,
        LastError = error,
    };

    [Fact]
    public async Task BeforeTheFirstCycleNothingIsFreshAndNothingIsHealthy()
    {
        (QuoteHealth health, _, _) = Build();

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(1, report.ConfiguredPairs);
        Assert.Equal(0, report.PairsWithFreshQuote);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task AFreshReadingOnEveryPairIsHealthy()
    {
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddSeconds(-20)));

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.True(report.IsHealthy);
        Assert.Equal(1, report.PairsWithFreshQuote);
        Assert.Equal(TimeSpan.FromSeconds(20), report.OldestQuoteAge);
        Assert.True(report.CycleFitsInInterval);
    }

    [Fact]
    public async Task AFailingPairIsReportedWithItsError()
    {
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(failures: 3, error: "node unreachable"), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddSeconds(-20)));

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(1, report.PairsFailing);
        Assert.Equal(3, report.MaxConsecutiveFailures);
        Assert.Equal("node unreachable", report.LastError);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task AReadingPastItsAgeLimitDoesNotCountAsFresh()
    {
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddMinutes(-10)));

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(0, report.PairsWithFreshQuote);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task TheQueueIsReportedButABacklogIsNotUnhealthyByItself()
    {
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now));
        await store.TryEnqueueValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "HASH1",
                PairKey = Xpm.Key,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = Now.AddSeconds(-45),
            },
            Ct);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(1, report.PendingValuations);
        Assert.Equal(TimeSpan.FromSeconds(45), report.OldestPendingAge);
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task AnUndeliveredValuationReportsHowLongItHasBeenWaiting()
    {
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now));
        await store.TryEnqueueValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "HASH1",
                PairKey = Xpm.Key,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = Now.AddMinutes(-30),
            },
            Ct);
        await store.SaveValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "HASH1",
                PairKey = Xpm.Key,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = Now.AddMinutes(-30),
                State = ValuationState.Valued,
                ValuedAt = Now.AddMinutes(-29),
                QuoteAmount = 10m,
            },
            Ct);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(1, report.UndeliveredValuations);
        Assert.Equal(TimeSpan.FromMinutes(30), report.OldestUndeliveredAge);
        // A queue that is not draining is still not unhealthy by itself — the age is what an operator
        // alerts on, not IsHealthy.
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task APairRemovedFromConfigurationDoesNotPinTheReportUnhealthyForever()
    {
        // The failure row for a removed pair is never deleted; health must not keep naming an asset the
        // gateway no longer prices just because its old row is still sitting in the store.
        QuoteOptions options = new QuoteOptions
        {
            // Xpm is deliberately absent: the store still holds its failing row from before it was removed.
            Pairs = new[] { new QuotePair("BAR", XpmIssuer, "USD", RlusdIssuer) },
            RefreshInterval = TimeSpan.FromMinutes(1),
        };
        InMemoryQuoteStore store = new InMemoryQuoteStore();
        await store.SaveQuoteAsync(Quote(failures: 5, error: "node unreachable"), Ct);
        QuoteHealth health = new QuoteHealth(
            Options.Create(options),
            store,
            new QuoteRegistry(options.Pairs),
            new FixedTimeProvider(Now),
            NullLogger<QuoteHealth>.Instance);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(0, report.PairsFailing);
        Assert.Equal(0, report.MaxConsecutiveFailures);
        Assert.Null(report.LastError);
    }

    [Fact]
    public async Task AStoreWhoseWritesFailIsNeverHealthyEvenThoughReadsStillWork()
    {
        // Everything QuoteHealth reads on its own — the store's rows, the in-memory snapshot — can look
        // perfectly fresh while the collector's writes have been failing for hours: those fields describe
        // captures and reads, not whether a persist attempt ever landed. StoreWritable, sourced from the
        // registry the collector itself updates, is the only thing in the report actually derived from a
        // write succeeding.
        (QuoteHealth health, InMemoryQuoteStore store, QuoteRegistry registry) = Build();
        await store.SaveQuoteAsync(Quote(), Ct);
        registry.SetSnapshot(Xpm.Key, new StubQuoteSnapshot(capturedAt: Now.AddSeconds(-20)));
        registry.SetLastWriteSucceeded(Xpm.Key, succeeded: false);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.True(report.StoreReadable);
        Assert.Equal(1, report.PairsWithFreshQuote);
        Assert.Equal(1, report.PairsFailingToPersist);
        Assert.False(report.StoreWritable);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task WritesFailingForOnePairAreNotErasedByAnotherPairsSuccessfulWrite()
    {
        // The write-health flag used to be one process-wide last-write-wins boolean: a store rejecting
        // writes for one pair out of two would be erased by the other pair's success, and the report would
        // go green while one pair's persistence stayed broken. PairsFailingToPersist is per pair, like
        // every other count in this report, so it must not be erased that way.
        QuotePair bar = new QuotePair("BAR", XpmIssuer, "USD", RlusdIssuer);
        QuoteOptions options = new QuoteOptions
        {
            Pairs = new[] { Xpm, bar },
            RefreshInterval = TimeSpan.FromMinutes(1),
        };
        InMemoryQuoteStore store = new InMemoryQuoteStore();
        QuoteRegistry registry = new QuoteRegistry(options.Pairs);
        QuoteHealth health = new QuoteHealth(
            Options.Create(options), store, registry, new FixedTimeProvider(Now), NullLogger<QuoteHealth>.Instance);

        registry.SetLastWriteSucceeded(Xpm.Key, succeeded: false);
        registry.SetLastWriteSucceeded(bar.Key, succeeded: true);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.Equal(1, report.PairsFailingToPersist);
        Assert.False(report.StoreWritable);
    }

    [Fact]
    public async Task AStoreThatCannotBeReadIsNeverHealthy()
    {
        QuoteOptions options = new QuoteOptions { Pairs = new[] { Xpm } };
        QuoteHealth health = new QuoteHealth(
            Options.Create(options),
            new ThrowingQuoteStore(),
            new QuoteRegistry(options.Pairs),
            new FixedTimeProvider(Now),
            NullLogger<QuoteHealth>.Instance);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        Assert.False(report.StoreReadable);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task AHangingStoreDoesNotBlockTheHealthCheckBeyondItsTimeout()
    {
        // QuoteOptions.StoreTimeout's summary claims it bounds the store on every path that must not hang.
        // CheckAsync makes several store calls of its own — GetQuotesAsync is the first — and used to carry
        // only the caller's token, so a store that merely hangs would hang the health check with it. A
        // health check against a hung store must report StoreReadable false, not sit there forever.
        QuoteOptions options = new QuoteOptions { Pairs = new[] { Xpm }, StoreTimeout = TimeSpan.FromMilliseconds(100) };
        QuoteHealth health = new QuoteHealth(
            Options.Create(options),
            new HangingQuoteStore(),
            new QuoteRegistry(options.Pairs),
            TimeProvider.System,
            NullLogger<QuoteHealth>.Instance);

        DateTime startedAt = DateTime.UtcNow;
        QuoteHealthReport report = await health.CheckAsync(Ct);
        TimeSpan elapsed = DateTime.UtcNow - startedAt;

        Assert.False(report.StoreReadable);
        Assert.False(report.IsHealthy);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"expected the health check to give up near its 100ms StoreTimeout, but it took {elapsed}");
    }

    [Fact]
    public async Task FailedValuationsCountsFailedEntriesOnlyNotWrittenOffOnes()
    {
        (QuoteHealth health, InMemoryQuoteStore store, _) = Build();
        await store.TryEnqueueValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "STILL-FAILED",
                PairKey = Xpm.Key,
                Amount = 1000m,
                PaymentLedgerIndex = 901,
                EnqueuedAt = Now,
            },
            Ct);
        await store.SaveValuationFailureAsync("STILL-FAILED", "the pair currently has no liquidity", Now, Ct);

        await store.TryEnqueueValuationAsync(
            new PaymentValuation
            {
                TransactionHash = "WRITTEN-OFF",
                PairKey = Xpm.Key,
                Amount = 1m,
                PaymentLedgerIndex = 902,
                EnqueuedAt = Now,
            },
            Ct);
        await store.SaveValuationFailureAsync("WRITTEN-OFF", "the pair currently has no liquidity", Now, Ct);
        await store.SaveWriteOffAsync("WRITTEN-OFF", "dust", Now, Ct);

        QuoteHealthReport report = await health.CheckAsync(Ct);

        // Settled work must not keep an operator's queue looking non-empty.
        Assert.Equal(1, report.FailedValuations);
    }

}

/// <summary>A quote store whose every read fails.</summary>
public sealed class ThrowingQuoteStore : IQuoteStore
{
    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) => throw new IOException("down");

    public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(CancellationToken cancellationToken) =>
        throw new IOException("down");

    public Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) => throw new IOException("down");

    public Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken) =>
        throw new IOException("down");

    public Task<bool> SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken) =>
        throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
        DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<bool> MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken) =>
        throw new IOException("down");

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) => throw new IOException("down");
}
