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
}

/// <summary>A quote store whose every read fails.</summary>
public sealed class ThrowingQuoteStore : IQuoteStore
{
    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) => throw new IOException("down");

    public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) => throw new IOException("down");

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) => throw new IOException("down");

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) => throw new IOException("down");

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) => throw new IOException("down");
}
