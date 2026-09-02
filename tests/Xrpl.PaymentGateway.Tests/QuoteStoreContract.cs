using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// What every <see cref="IQuoteStore"/> must do. A new store is validated by deriving from this and
/// nothing else, exactly as with <see cref="PaymentStoreContract"/>.
/// </summary>
public abstract class QuoteStoreContract
{
    protected abstract Task<IQuoteStore> CreateAsync();

    /// <summary>Opens the same storage again, as a restart would. Null when the store cannot outlive its process.</summary>
    protected virtual Task<IQuoteStore?> ReopenAsync(IQuoteStore store) =>
        Task.FromResult<IQuoteStore?>(null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PairKey = "00000000000000000000000058504D0000000000.rXPM/00000000000000000000000055534400000000.rUSD";

    private static StoredQuote Quote(decimal? marginal = 0.01m, int failures = 0) => new StoredQuote
    {
        PairKey = PairKey,
        Currency = "XPM",
        Issuer = "rXPM",
        QuoteCurrency = "USD",
        QuoteIssuer = "rRLU",
        MarginalPrice = marginal,
        LedgerIndex = 900,
        CapturedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
        LastAttemptAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
        ConsecutiveFailures = failures,
    };

    private static PaymentValuation Pending(string hash, decimal amount = 1000m) => new PaymentValuation
    {
        TransactionHash = hash,
        PairKey = PairKey,
        Amount = amount,
        PaymentLedgerIndex = 901,
        DestinationTag = 42,
        EnqueuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 5, TimeSpan.Zero),
    };

    private static PaymentValuation Valued(PaymentValuation pending, decimal quoteAmount) => new PaymentValuation
    {
        TransactionHash = pending.TransactionHash,
        PairKey = pending.PairKey,
        Amount = pending.Amount,
        PaymentLedgerIndex = pending.PaymentLedgerIndex,
        DestinationTag = pending.DestinationTag,
        EnqueuedAt = pending.EnqueuedAt,
        ValuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 30, TimeSpan.Zero),
        QuoteAmount = quoteAmount,
        EffectivePrice = quoteAmount / pending.Amount,
        MarginalPrice = 0.01m,
        SlippagePercent = 1m,
        FullyFilled = true,
        SnapshotLedgerIndex = 900,
        SnapshotCapturedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
        Route = "XPM->XRP->USD",
    };

    [Fact]
    public async Task APairWithNoReadingYetHasNoQuote()
    {
        IQuoteStore store = await CreateAsync();

        Assert.Null(await store.GetQuoteAsync(PairKey, Ct));
        Assert.Empty(await store.GetQuotesAsync(Ct));
    }

    [Fact]
    public async Task AQuoteIsReadBackAsItWasWritten()
    {
        IQuoteStore store = await CreateAsync();
        await store.SaveQuoteAsync(Quote(), Ct);

        StoredQuote? read = await store.GetQuoteAsync(PairKey, Ct);

        Assert.NotNull(read);
        Assert.Equal(0.01m, read.MarginalPrice);
        Assert.Equal(900u, read.LedgerIndex);
        Assert.Equal(0, read.ConsecutiveFailures);
    }

    [Fact]
    public async Task AStoredQuoteSurvivesTheRoundTripIntactRatherThanApproximately()
    {
        IQuoteStore store = await CreateAsync();
        StoredQuote written = new StoredQuote
        {
            PairKey = PairKey,
            Currency = "XPM",
            Issuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
            QuoteCurrency = "USD",
            QuoteIssuer = "rRLUSDIssuerAddress00000000000000",
            MarginalPrice = 0.010203m,
            LedgerIndex = 987654u,
            CapturedAt = new DateTimeOffset(2026, 8, 30, 11, 55, 0, TimeSpan.Zero),
            LastAttemptAt = new DateTimeOffset(2026, 8, 30, 12, 3, 0, TimeSpan.Zero),
            ConsecutiveFailures = 4,
            LastError = "tecPATH_DRY: no offers crossed the pair",
        };

        await store.SaveQuoteAsync(written, Ct);
        StoredQuote? read = await store.GetQuoteAsync(PairKey, Ct);

        Assert.NotNull(read);
        Assert.Equal(written.PairKey, read.PairKey);
        Assert.Equal(written.Currency, read.Currency);
        Assert.Equal(written.Issuer, read.Issuer);
        Assert.Equal(written.QuoteCurrency, read.QuoteCurrency);
        Assert.Equal(written.QuoteIssuer, read.QuoteIssuer);
        Assert.Equal(written.MarginalPrice, read.MarginalPrice);
        Assert.Equal(written.LedgerIndex, read.LedgerIndex);
        Assert.Equal(written.CapturedAt, read.CapturedAt);
        Assert.Equal(written.LastAttemptAt, read.LastAttemptAt);
        Assert.Equal(written.ConsecutiveFailures, read.ConsecutiveFailures);
        Assert.Equal(written.LastError, read.LastError);
    }

    [Fact]
    public async Task WritingAPairAgainReplacesItRatherThanAddingASecondRow()
    {
        IQuoteStore store = await CreateAsync();
        await store.SaveQuoteAsync(Quote(marginal: 0.01m), Ct);
        await store.SaveQuoteAsync(Quote(marginal: 0.02m), Ct);

        Assert.Single(await store.GetQuotesAsync(Ct));
        Assert.Equal(0.02m, (await store.GetQuoteAsync(PairKey, Ct))!.MarginalPrice);
    }

    [Fact]
    public async Task AQueuedValuationIsPendingUntilItIsValued()
    {
        IQuoteStore store = await CreateAsync();

        Assert.True(await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct));

        IReadOnlyList<PaymentValuation> pending = await store.GetPendingValuationsAsync(10, Ct);
        Assert.Single(pending);
        Assert.False(pending[0].IsValued);
        Assert.Empty(await store.GetUndeliveredValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task TheSameHashIsQueuedOnceHoweverManyTimesItIsOffered()
    {
        // Live processing, catch-up and reconciliation all offer the same payment.
        IQuoteStore store = await CreateAsync();

        Assert.True(await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct));
        Assert.False(await store.TryEnqueueValuationAsync(Pending("HASH1"), Ct));

        Assert.Single(await store.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task AValuedEntryLeavesThePendingQueueAndBecomesUndelivered()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);

        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        Assert.Empty(await store.GetPendingValuationsAsync(10, Ct));
        IReadOnlyList<PaymentValuation> undelivered = await store.GetUndeliveredValuationsAsync(10, Ct);
        Assert.Single(undelivered);
        Assert.Equal(9.9m, undelivered[0].QuoteAmount);
    }

    [Fact]
    public async Task ADeliveredValuationLeavesTheUndeliveredQueueButStaysReadable()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        await store.MarkValuationDeliveredAsync("HASH1", Ct);

        Assert.Empty(await store.GetUndeliveredValuationsAsync(10, Ct));
        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.True(read.Delivered);
        Assert.Equal("XPM->XRP->USD", read.Route);
    }

    [Fact]
    public async Task AValuationSurvivesTheRoundTripIntactRatherThanApproximately()
    {
        IQuoteStore store = await CreateAsync();
        const string hash = "FULL-ROUNDTRIP";
        PaymentValuation pending = new PaymentValuation
        {
            TransactionHash = hash,
            PairKey = PairKey,
            Amount = 1234.5m,
            PaymentLedgerIndex = 555555u,
            // DestinationTag is how IPaymentValuedHandler resolves the buyer: IPaymentStore offers no
            // lookup by transaction hash, and cannot gain one without breaking every 1.0.0 implementation.
            // A store that drops this field on the way in or out silently breaks buyer attribution.
            DestinationTag = 909090u,
            EnqueuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 5, TimeSpan.Zero),
        };
        await store.TryEnqueueValuationAsync(pending, Ct);

        PaymentValuation valued = new PaymentValuation
        {
            TransactionHash = hash,
            PairKey = pending.PairKey,
            Amount = pending.Amount,
            PaymentLedgerIndex = pending.PaymentLedgerIndex,
            DestinationTag = pending.DestinationTag,
            EnqueuedAt = pending.EnqueuedAt,
            ValuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 42, TimeSpan.Zero),
            QuoteAmount = 12.375m,
            EffectivePrice = 0.010025m,
            MarginalPrice = 0.0102m,
            SlippagePercent = 1.72m,
            FullyFilled = false,
            BookTruncated = true,
            Route = "XPM->XRP->USD",
            SnapshotLedgerIndex = 555000u,
            SnapshotCapturedAt = new DateTimeOffset(2026, 8, 30, 11, 59, 30, TimeSpan.Zero),
        };
        await store.SaveValuationAsync(valued, Ct);
        await store.MarkValuationDeliveredAsync(hash, Ct);

        PaymentValuation? read = await store.GetValuationAsync(hash, Ct);

        Assert.NotNull(read);
        Assert.Equal(valued.TransactionHash, read.TransactionHash);
        Assert.Equal(valued.PairKey, read.PairKey);
        Assert.Equal(valued.Amount, read.Amount);
        Assert.Equal(valued.PaymentLedgerIndex, read.PaymentLedgerIndex);
        Assert.Equal(valued.DestinationTag, read.DestinationTag);
        Assert.Equal(valued.EnqueuedAt, read.EnqueuedAt);
        Assert.Equal(valued.ValuedAt, read.ValuedAt);
        Assert.Equal(valued.QuoteAmount, read.QuoteAmount);
        Assert.Equal(valued.EffectivePrice, read.EffectivePrice);
        Assert.Equal(valued.MarginalPrice, read.MarginalPrice);
        Assert.Equal(valued.SlippagePercent, read.SlippagePercent);
        Assert.Equal(valued.FullyFilled, read.FullyFilled);
        // BookTruncated defaults to false, so asserting true here catches a store that never
        // persists it and always reads the default back.
        Assert.True(read.BookTruncated);
        Assert.Equal(valued.Route, read.Route);
        Assert.Equal(valued.SnapshotLedgerIndex, read.SnapshotLedgerIndex);
        Assert.Equal(valued.SnapshotCapturedAt, read.SnapshotCapturedAt);
        Assert.True(read.Delivered);
        Assert.True(read.IsValued);
    }

    [Fact]
    public async Task PendingEntriesComeBackOldestFirstAndRespectTheLimit()
    {
        IQuoteStore store = await CreateAsync();
        for (int i = 1; i <= 5; i++)
        {
            await store.TryEnqueueValuationAsync(Pending($"HASH{i}"), Ct);
        }

        IReadOnlyList<PaymentValuation> page = await store.GetPendingValuationsAsync(3, Ct);

        Assert.Equal(3, page.Count);
        Assert.Equal("HASH1", page[0].TransactionHash);
        Assert.Equal("HASH3", page[2].TransactionHash);
    }

    [Fact]
    public async Task MarkingSomethingThatIsNotThereIsNotAnError()
    {
        IQuoteStore store = await CreateAsync();

        await store.MarkValuationDeliveredAsync("NOSUCHHASH", Ct);

        Assert.Null(await store.GetValuationAsync("NOSUCHHASH", Ct));
    }

    [Fact]
    public async Task ConcurrentQueueingOfOneHashLetsExactlyOneWin()
    {
        IQuoteStore store = await CreateAsync();

        Task<bool>[] attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.TryEnqueueValuationAsync(Pending("HASH1"), Ct), Ct))
            .ToArray();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, won => won);
        Assert.Single(await store.GetPendingValuationsAsync(10, Ct));
    }

    [Fact]
    public async Task EverythingSurvivesAReopen()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.SaveQuoteAsync(Quote(), Ct);
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        IQuoteStore? reopened = await ReopenAsync(store);
        if (reopened is null)
        {
            Assert.Skip("this store does not persist across a reopen");
            return;
        }

        Assert.Equal(0.01m, (await reopened.GetQuoteAsync(PairKey, Ct))!.MarginalPrice);
        PaymentValuation? valuation = await reopened.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(valuation);
        Assert.Equal(9.9m, valuation.QuoteAmount);
        Assert.Single(await reopened.GetUndeliveredValuationsAsync(10, Ct));
    }
}
