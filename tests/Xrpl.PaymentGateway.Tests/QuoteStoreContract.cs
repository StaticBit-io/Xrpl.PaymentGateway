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

    private const string OtherPairKey = "00000000000000000000000042415200000000000.rBAR/00000000000000000000000055534400000000.rUSD";

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

    private static PaymentValuation Pending(
        string hash, decimal amount = 1000m, DateTimeOffset? enqueuedAt = null, string pairKey = PairKey) => new PaymentValuation
    {
        TransactionHash = hash,
        PairKey = pairKey,
        Amount = amount,
        PaymentLedgerIndex = 901,
        DestinationTag = 42,
        EnqueuedAt = enqueuedAt ?? new DateTimeOffset(2026, 8, 30, 12, 0, 5, TimeSpan.Zero),
    };

    private static PaymentValuation Valued(PaymentValuation pending, decimal quoteAmount) => new PaymentValuation
    {
        TransactionHash = pending.TransactionHash,
        PairKey = pending.PairKey,
        Amount = pending.Amount,
        PaymentLedgerIndex = pending.PaymentLedgerIndex,
        DestinationTag = pending.DestinationTag,
        EnqueuedAt = pending.EnqueuedAt,
        State = ValuationState.Valued,
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

    private static PaymentValuation ValuedManually(PaymentValuation pending, decimal rate) => new PaymentValuation
    {
        TransactionHash = pending.TransactionHash,
        PairKey = pending.PairKey,
        Amount = pending.Amount,
        PaymentLedgerIndex = pending.PaymentLedgerIndex,
        DestinationTag = pending.DestinationTag,
        EnqueuedAt = pending.EnqueuedAt,
        State = ValuationState.ValuedManually,
        ValuedAt = new DateTimeOffset(2026, 8, 30, 12, 5, 0, TimeSpan.Zero),
        QuoteAmount = pending.Amount * rate,
        EffectivePrice = rate,
        FullyFilled = true,
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

        IReadOnlyList<PaymentValuation> pending = await store.GetPendingValuationsAsync(PairKey, 10, Ct);
        Assert.Single(pending);
        Assert.Equal(ValuationState.Pending, pending[0].State);
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

        Assert.Single(await store.GetPendingValuationsAsync(PairKey, 10, Ct));
    }

    [Fact]
    public async Task AValuedEntryLeavesThePendingQueueAndBecomesUndelivered()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);

        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        Assert.Empty(await store.GetPendingValuationsAsync(PairKey, 10, Ct));
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

        await store.MarkValuationDeliveredAsync("HASH1", ValuationState.Valued, Ct);

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
            State = ValuationState.Valued,
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
        await store.MarkValuationDeliveredAsync(hash, ValuationState.Valued, Ct);

        PaymentValuation? read = await store.GetValuationAsync(hash, Ct);

        Assert.NotNull(read);
        Assert.Equal(valued.TransactionHash, read.TransactionHash);
        Assert.Equal(valued.PairKey, read.PairKey);
        Assert.Equal(valued.Amount, read.Amount);
        Assert.Equal(valued.PaymentLedgerIndex, read.PaymentLedgerIndex);
        Assert.Equal(valued.DestinationTag, read.DestinationTag);
        Assert.Equal(valued.EnqueuedAt, read.EnqueuedAt);
        Assert.Equal(ValuationState.Valued, read.State);
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

        IReadOnlyList<PaymentValuation> page = await store.GetPendingValuationsAsync(PairKey, 3, Ct);

        Assert.Equal(3, page.Count);
        Assert.Equal("HASH1", page[0].TransactionHash);
        Assert.Equal("HASH3", page[2].TransactionHash);
    }

    [Fact]
    public async Task AFailedEntryLeavesThePendingQueueAndSurvivesTheRoundTripIntact()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);

        DateTimeOffset failedAt = new DateTimeOffset(2026, 8, 30, 12, 1, 0, TimeSpan.Zero);
        await store.SaveValuationFailureAsync("HASH1", "the pair currently has no liquidity", failedAt, Ct);

        Assert.Empty(await store.GetPendingValuationsAsync(PairKey, 10, Ct));

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.Failed, read.State);
        Assert.True(read.IsFailed);
        Assert.False(read.IsValued);
        Assert.Equal(failedAt, read.FailedAt);
        Assert.Equal("the pair currently has no liquidity", read.FailureReason);
        Assert.Null(read.QuoteAmount);
    }

    [Fact]
    public async Task FailingSomethingThatIsNotPendingDoesNothing()
    {
        // Only a Pending entry can fail — an entry the automatic pipeline already priced, or already
        // failed, must not be knocked out of its state by a stray failure write racing behind it.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        await store.SaveValuationFailureAsync("HASH1", "should not apply", DateTimeOffset.UtcNow, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.Valued, read.State);
        Assert.Equal(9.9m, read.QuoteAmount);
    }

    [Fact]
    public async Task AWrittenOffEntrySurvivesTheRoundTripIntactAndKeepsWhyItOriginallyFailed()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        DateTimeOffset failedAt = new DateTimeOffset(2026, 8, 30, 12, 1, 0, TimeSpan.Zero);
        await store.SaveValuationFailureAsync("HASH1", "the pair currently has no liquidity", failedAt, Ct);

        DateTimeOffset writtenOffAt = new DateTimeOffset(2026, 8, 30, 12, 10, 0, TimeSpan.Zero);
        await store.SaveWriteOffAsync("HASH1", "dust", writtenOffAt, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read.State);
        Assert.True(read.IsWrittenOff);
        Assert.False(read.IsFailed);
        Assert.Equal(writtenOffAt, read.WrittenOffAt);
        Assert.Equal("dust", read.WriteOffReason);
        // The original failure is kept for the record, alongside the operator's own reason.
        Assert.Equal(failedAt, read.FailedAt);
        Assert.Equal("the pair currently has no liquidity", read.FailureReason);
    }

    [Fact]
    public async Task WritingOffSomethingThatIsNotFailedDoesNothing()
    {
        // Writing off something that priced normally is a mistake, not a workflow — only a Failed entry
        // can be written off.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationAsync(Valued(pending, 9.9m), Ct);

        await store.SaveWriteOffAsync("HASH1", "should not apply", DateTimeOffset.UtcNow, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.Valued, read.State);
    }

    [Fact]
    public async Task AManuallyPricedEntrySurvivesTheRoundTripAndClearsTheFailureItReplaced()
    {
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1", amount: 1000m);
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationFailureAsync("HASH1", "the pair currently has no liquidity", DateTimeOffset.UtcNow, Ct);

        await store.SaveValuationAsync(ValuedManually(pending, rate: 0.02m), Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.ValuedManually, read.State);
        Assert.True(read.IsValued);
        Assert.Equal(20m, read.QuoteAmount);
        Assert.Equal(0.02m, read.EffectivePrice);
        // Resolved now, not failed and valued at once.
        Assert.Null(read.FailedAt);
        Assert.Null(read.FailureReason);
    }

    [Fact]
    public async Task FailedAndWrittenOffEntriesAreDeliveredLikeValuedOnes()
    {
        // A host learns about all four non-pending outcomes, not only a successful price — see
        // IPaymentValuedHandler. GetUndeliveredValuationsAsync is what ValuationWorker's delivery pass
        // polls, so it must not filter Failed and WrittenOff out.
        IQuoteStore store = await CreateAsync();

        PaymentValuation failedPending = Pending("FAILED");
        await store.TryEnqueueValuationAsync(failedPending, Ct);
        await store.SaveValuationFailureAsync("FAILED", "no liquidity", DateTimeOffset.UtcNow, Ct);

        PaymentValuation writtenOffPending = Pending("WRITTENOFF");
        await store.TryEnqueueValuationAsync(writtenOffPending, Ct);
        await store.SaveValuationFailureAsync("WRITTENOFF", "no liquidity", DateTimeOffset.UtcNow, Ct);
        await store.SaveWriteOffAsync("WRITTENOFF", "dust", DateTimeOffset.UtcNow, Ct);

        IReadOnlyList<PaymentValuation> undelivered = await store.GetUndeliveredValuationsAsync(10, Ct);

        Assert.Equal(2, undelivered.Count);
        Assert.Contains(undelivered, v => v.TransactionHash == "FAILED" && v.State == ValuationState.Failed);
        Assert.Contains(undelivered, v => v.TransactionHash == "WRITTENOFF" && v.State == ValuationState.WrittenOff);
    }

    [Fact]
    public async Task FailedValuationsArePagedOldestFailedFirstAndCounted()
    {
        IQuoteStore store = await CreateAsync();
        for (int i = 1; i <= 5; i++)
        {
            await store.TryEnqueueValuationAsync(
                Pending($"HASH{i}", enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, i, TimeSpan.Zero)), Ct);
            await store.SaveValuationFailureAsync(
                $"HASH{i}", "no liquidity", new DateTimeOffset(2026, 8, 30, 12, 1, i, TimeSpan.Zero), Ct);
        }

        Assert.Equal(5, await store.CountFailedValuationsAsync(Ct));

        IReadOnlyList<PaymentValuation> firstPage = await store.GetFailedValuationsAsync(2, 0, Ct);
        Assert.Equal(new[] { "HASH1", "HASH2" }, firstPage.Select(v => v.TransactionHash));

        IReadOnlyList<PaymentValuation> secondPage = await store.GetFailedValuationsAsync(2, 2, Ct);
        Assert.Equal(new[] { "HASH3", "HASH4" }, secondPage.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task MarkingSomethingThatIsNotThereIsNotAnError()
    {
        IQuoteStore store = await CreateAsync();

        await store.MarkValuationDeliveredAsync("NOSUCHHASH", ValuationState.Valued, Ct);

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
        Assert.Single(await store.GetPendingValuationsAsync(PairKey, 10, Ct));
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

    [Fact]
    public async Task APricedEntryThatWasDeliveredWhileFailedBecomesUndeliveredAgain()
    {
        // The sequence PostgresQuoteStore used to get wrong: an entry fails, the delivery pass hands the
        // Failed row to the host and marks it delivered, and only then does an operator price it manually.
        // The manual price is a new fact the host has not heard — it must come back undelivered so the
        // normal delivery pass picks it up, not sit forever behind a Delivered flag that describes stale
        // content.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", DateTimeOffset.UtcNow, Ct);
        await store.MarkValuationDeliveredAsync("HASH1", ValuationState.Failed, Ct);
        Assert.Empty(await store.GetUndeliveredValuationsAsync(10, Ct));

        await store.SaveValuationAsync(ValuedManually(pending, rate: 0.02m), Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.ValuedManually, read.State);
        Assert.False(read.Delivered);
        IReadOnlyList<PaymentValuation> undelivered = await store.GetUndeliveredValuationsAsync(10, Ct);
        Assert.Contains(undelivered, v => v.TransactionHash == "HASH1");
    }

    [Fact]
    public async Task AWriteOffThatWasDeliveredWhileFailedBecomesUndeliveredAgain()
    {
        // Same sequence as above, for the other resolution a Failed entry can take: a write-off is a new
        // fact the host has not heard either, even though the row it replaces was already delivered.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", DateTimeOffset.UtcNow, Ct);
        await store.MarkValuationDeliveredAsync("HASH1", ValuationState.Failed, Ct);
        Assert.Empty(await store.GetUndeliveredValuationsAsync(10, Ct));

        await store.SaveWriteOffAsync("HASH1", "dust", DateTimeOffset.UtcNow, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read.State);
        Assert.False(read.Delivered);
        IReadOnlyList<PaymentValuation> undelivered = await store.GetUndeliveredValuationsAsync(10, Ct);
        Assert.Contains(undelivered, v => v.TransactionHash == "HASH1");
    }

    [Fact]
    public async Task SavingAValuationOverAnEntryThatIsAlreadyResolvedDoesNothing()
    {
        // Two operators racing — one pricing manually, one writing off — must not have one silently
        // overwrite the other. SaveValuationAsync only ever replaces a row still Pending or Failed; once a
        // write-off has landed, a late-arriving manual price must find the row already moved on and leave
        // it alone.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", DateTimeOffset.UtcNow, Ct);
        await store.SaveWriteOffAsync("HASH1", "dust", DateTimeOffset.UtcNow, Ct);

        await store.SaveValuationAsync(ValuedManually(pending, rate: 0.02m), Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.WrittenOff, read.State);
        Assert.Equal("dust", read.WriteOffReason);
        Assert.Null(read.QuoteAmount);
    }

    [Fact]
    public async Task MarkValuationDeliveredAsyncDoesNothingWhenTheRowHasMovedOnFromTheDeliveredState()
    {
        // The race DeliverValuedAsync must survive: it reads a Failed entry, calls the host handler with
        // that content, and while the call is in flight an operator resolves the entry. The mark must be
        // conditional on the row still being Failed — the state actually handed to the handler — or the
        // resolution's own content is lost: marked delivered without the host ever having seen it.
        IQuoteStore store = await CreateAsync();
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, Ct);
        await store.SaveValuationFailureAsync("HASH1", "no liquidity", DateTimeOffset.UtcNow, Ct);

        // The operator resolves the entry before the stale-state delivery mark below is applied.
        await store.SaveValuationAsync(ValuedManually(pending, rate: 0.02m), Ct);

        await store.MarkValuationDeliveredAsync("HASH1", ValuationState.Failed, Ct);

        PaymentValuation? read = await store.GetValuationAsync("HASH1", Ct);
        Assert.NotNull(read);
        Assert.Equal(ValuationState.ValuedManually, read.State);
        Assert.False(read.Delivered);
        IReadOnlyList<PaymentValuation> undelivered = await store.GetUndeliveredValuationsAsync(10, Ct);
        Assert.Contains(undelivered, v => v.TransactionHash == "HASH1");
    }

    [Fact]
    public async Task FailedValuationsAreOrderedByWhenTheyFailedNotByWhenTheyWereEnqueued()
    {
        // IQuoteStore documents oldest-failed-first. Enqueueing HASH-A before HASH-B but failing them in
        // the opposite order is what makes that order distinguishable from plain enqueue order — a store
        // that (like InMemoryQuoteStore and FileQuoteStore used to) pages failed entries in enqueue order
        // would return them the wrong way round here.
        IQuoteStore store = await CreateAsync();
        await store.TryEnqueueValuationAsync(
            Pending("HASH-A", enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 1, TimeSpan.Zero)), Ct);
        await store.TryEnqueueValuationAsync(
            Pending("HASH-B", enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 2, TimeSpan.Zero)), Ct);

        await store.SaveValuationFailureAsync(
            "HASH-B", "no liquidity", new DateTimeOffset(2026, 8, 30, 12, 1, 0, TimeSpan.Zero), Ct);
        await store.SaveValuationFailureAsync(
            "HASH-A", "no liquidity", new DateTimeOffset(2026, 8, 30, 12, 2, 0, TimeSpan.Zero), Ct);

        IReadOnlyList<PaymentValuation> failed = await store.GetFailedValuationsAsync(10, 0, Ct);

        Assert.Equal(new[] { "HASH-B", "HASH-A" }, failed.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task PendingValuationsAreScopedToOnePairOldestFirst()
    {
        IQuoteStore store = await CreateAsync();
        await store.TryEnqueueValuationAsync(
            Pending("PAIR1-A", pairKey: PairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 1, TimeSpan.Zero)), Ct);
        await store.TryEnqueueValuationAsync(
            Pending("OTHER-A", pairKey: OtherPairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 2, TimeSpan.Zero)), Ct);
        await store.TryEnqueueValuationAsync(
            Pending("PAIR1-B", pairKey: PairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 3, TimeSpan.Zero)), Ct);

        IReadOnlyList<PaymentValuation> pairOnePage = await store.GetPendingValuationsAsync(PairKey, 10, Ct);
        Assert.Equal(new[] { "PAIR1-A", "PAIR1-B" }, pairOnePage.Select(v => v.TransactionHash));

        IReadOnlyList<PaymentValuation> otherPage = await store.GetPendingValuationsAsync(OtherPairKey, 10, Ct);
        Assert.Equal(new[] { "OTHER-A" }, otherPage.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task OnePairsBacklogDoesNotAppearInAnotherPairsPage()
    {
        // A pair whose snapshot is missing or stale must not bury payments on a healthy pair behind it —
        // the whole point of a per-pair queue. A backlog on PairKey deep enough to fill the page must not
        // leak into a call scoped to OtherPairKey.
        IQuoteStore store = await CreateAsync();
        for (int i = 1; i <= 5; i++)
        {
            await store.TryEnqueueValuationAsync(Pending($"BACKLOG{i}", pairKey: PairKey), Ct);
        }

        await store.TryEnqueueValuationAsync(Pending("HEALTHY", pairKey: OtherPairKey), Ct);

        IReadOnlyList<PaymentValuation> otherPage = await store.GetPendingValuationsAsync(OtherPairKey, 3, Ct);

        Assert.Equal(new[] { "HEALTHY" }, otherPage.Select(v => v.TransactionHash));
    }

    [Fact]
    public async Task ThePendingBreakdownReportsCountAndOldestEnqueuePerPair()
    {
        IQuoteStore store = await CreateAsync();
        await store.TryEnqueueValuationAsync(
            Pending("PAIR1-A", pairKey: PairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 1, TimeSpan.Zero)), Ct);
        await store.TryEnqueueValuationAsync(
            Pending("PAIR1-B", pairKey: PairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 5, TimeSpan.Zero)), Ct);
        await store.TryEnqueueValuationAsync(
            Pending("OTHER-A", pairKey: OtherPairKey, enqueuedAt: new DateTimeOffset(2026, 8, 30, 12, 0, 3, TimeSpan.Zero)), Ct);
        // Not Pending, so it must not be counted.
        PaymentValuation valued = Pending("VALUED", pairKey: OtherPairKey);
        await store.TryEnqueueValuationAsync(valued, Ct);
        await store.SaveValuationAsync(Valued(valued, 1m), Ct);

        IReadOnlyList<PendingValuationsByPair> breakdown = await store.GetPendingValuationBreakdownAsync(Ct);

        PendingValuationsByPair pairOne = Assert.Single(breakdown, b => b.PairKey == PairKey);
        Assert.Equal(2, pairOne.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 12, 0, 1, TimeSpan.Zero), pairOne.OldestEnqueuedAt);

        PendingValuationsByPair otherPair = Assert.Single(breakdown, b => b.PairKey == OtherPairKey);
        Assert.Equal(1, otherPair.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 12, 0, 3, TimeSpan.Zero), otherPair.OldestEnqueuedAt);
    }
}
