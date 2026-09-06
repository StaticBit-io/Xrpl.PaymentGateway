using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// What every <see cref="IPaymentDirectory"/> must do, whatever it is built on. Separate from
/// <see cref="PaymentStoreContract"/> because the interface is separate: a store may implement it or not,
/// and only the ones that do derive from this.
/// </summary>
/// <remarks>
/// These are read-only listings, so nothing here can corrupt anything — but every one of them feeds a
/// screen somebody makes decisions on, and the failures are all quiet ones. A filter that widens shows
/// every payment on a screen labelled "belongs to nobody". A total that counts the page rather than the
/// set tells an operator the queue is empty when it is not. An ordering that shifts under paging drops
/// rows without saying so. None of that throws.
/// </remarks>
public abstract class PaymentDirectoryContract
{
    /// <summary>Creates a store, exposed as both interfaces, with nothing in it.</summary>
    protected abstract Task<(IPaymentStore Store, IPaymentDirectory Directory)> CreateAsync();

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
    public async Task AnEmptyStoreListsNothing()
    {
        (IPaymentStore _, IPaymentDirectory directory) = await CreateAsync();

        BuyerTagPage buyers = await directory.ListBuyersAsync(10, 0, Ct);
        RecordedPaymentPage payments = await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct);

        Assert.Empty(buyers.Items);
        Assert.Equal(0, buyers.TotalCount);
        Assert.Empty(payments.Items);
        Assert.Equal(0, payments.TotalCount);
    }

    [Fact]
    public async Task BuyersAreListedInTagOrder()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        // Assigned in this order, so the tags increase in this order too.
        await store.GetOrAssignTagAsync("buyer-c", Ct);
        await store.GetOrAssignTagAsync("buyer-a", Ct);
        await store.GetOrAssignTagAsync("buyer-b", Ct);

        BuyerTagPage page = await directory.ListBuyersAsync(10, 0, Ct);

        // Not alphabetical, and not whatever the storage happens to return: tag order is issue order, and
        // it is the only ordering paging can trust here.
        Assert.Equal(["buyer-c", "buyer-a", "buyer-b"], page.Items.Select(item => item.BuyerId));
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task ABuyerIsListedWithTheTagItWasIssued()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        uint tag = await store.GetOrAssignTagAsync("buyer-1", Ct);

        BuyerTag listed = Assert.Single((await directory.ListBuyersAsync(10, 0, Ct)).Items);

        Assert.Equal("buyer-1", listed.BuyerId);
        Assert.Equal(tag, listed.DestinationTag);
    }

    [Fact]
    public async Task BuyerPagingWalksEveryRowExactlyOnce()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        for (int i = 0; i < 5; i++)
        {
            await store.GetOrAssignTagAsync($"buyer-{i}", Ct);
        }

        List<string> walked = [];
        for (int offset = 0; offset < 5; offset += 2)
        {
            BuyerTagPage page = await directory.ListBuyersAsync(2, offset, Ct);

            // The total is of the SET, not of the page: a screen paginates against it, so a last page of
            // one row must still say five.
            Assert.Equal(5, page.TotalCount);
            walked.AddRange(page.Items.Select(item => item.BuyerId));
        }

        Assert.Equal(["buyer-0", "buyer-1", "buyer-2", "buyer-3", "buyer-4"], walked);
    }

    [Fact]
    public async Task PaymentsAreListedNewestFirst()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        await store.TryAddPaymentAsync(Record("A"), Ct);
        await store.TryAddPaymentAsync(Record("B"), Ct);
        await store.TryAddPaymentAsync(Record("C"), Ct);

        RecordedPaymentPage page = await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct);

        // Recording order reversed, not the order the storage keeps them in: the question this listing
        // answers is "what just came in".
        Assert.Equal(["C", "B", "A"], page.Items.Select(item => item.Payment.TransactionHash));
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task APaymentCarriesTheBuyerItsTagWasIssuedTo()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        uint tag = await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.TryAddPaymentAsync(Record("A", tag), Ct);

        RecordedPayment listed = Assert.Single((await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct)).Items);

        // Resolved by the store, because doing it per row on the caller's side is a query per row.
        Assert.Equal("buyer-1", listed.BuyerId);
    }

    [Fact]
    public async Task APaymentWithNoTagBelongsToNobody()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        await store.TryAddPaymentAsync(Record("A", tag: null), Ct);

        RecordedPayment listed = Assert.Single((await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct)).Items);

        Assert.Null(listed.BuyerId);
    }

    [Fact]
    public async Task APaymentWithATagThatWasNeverIssuedAlsoBelongsToNobody()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        // A sender who typed a tag, or reused one from somewhere else. The money is just as untraceable
        // as with no tag at all, and reporting it as attributed would send an operator looking for a
        // buyer that does not exist.
        await store.TryAddPaymentAsync(Record("A", tag: 999_999u), Ct);

        RecordedPayment listed = Assert.Single((await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct)).Items);

        Assert.Null(listed.BuyerId);
    }

    [Fact]
    public async Task TheUnattributedFilterHoldsBothShapesOfNobodyAndNothingElse()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        uint tag = await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.TryAddPaymentAsync(Record("attributed", tag), Ct);
        await store.TryAddPaymentAsync(Record("no-tag", tag: null), Ct);
        await store.TryAddPaymentAsync(Record("unknown-tag", tag: 999_999u), Ct);

        RecordedPaymentPage page = await directory.ListPaymentsAsync(PaymentAttribution.Unattributed, 10, 0, Ct);

        Assert.Equal(["unknown-tag", "no-tag"], page.Items.Select(item => item.Payment.TransactionHash));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task TheAttributedFilterIsTheExactComplement()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        uint tag = await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.TryAddPaymentAsync(Record("attributed", tag), Ct);
        await store.TryAddPaymentAsync(Record("no-tag", tag: null), Ct);
        await store.TryAddPaymentAsync(Record("unknown-tag", tag: 999_999u), Ct);

        RecordedPaymentPage page = await directory.ListPaymentsAsync(PaymentAttribution.Attributed, 10, 0, Ct);

        Assert.Equal(["attributed"], page.Items.Select(item => item.Payment.TransactionHash));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ThePaymentTotalCountsTheFilteredSetNotThePage()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        await store.TryAddPaymentAsync(Record("A"), Ct);
        await store.TryAddPaymentAsync(Record("B"), Ct);
        await store.TryAddPaymentAsync(Record("C"), Ct);

        RecordedPaymentPage page = await directory.ListPaymentsAsync(PaymentAttribution.Any, 1, 0, Ct);

        // One row asked for, three exist. A screen that paginates against the page size never shows the
        // second page.
        Assert.Single(page.Items);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task APageBeyondTheEndIsEmptyButStillReportsTheTotal()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        await store.GetOrAssignTagAsync("buyer-1", Ct);
        await store.TryAddPaymentAsync(Record("A"), Ct);

        BuyerTagPage buyers = await directory.ListBuyersAsync(10, 50, Ct);
        RecordedPaymentPage payments = await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 50, Ct);

        // The case a count computed alongside the rows gets wrong: no rows, so nothing to carry a count
        // on, so the screen is told there is nothing at all.
        Assert.Empty(buyers.Items);
        Assert.Equal(1, buyers.TotalCount);
        Assert.Empty(payments.Items);
        Assert.Equal(1, payments.TotalCount);
    }

    [Fact]
    public async Task DeliveryToTheHostHandlerIsVisible()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        await store.TryAddPaymentAsync(Record("A"), Ct);
        await store.TryAddPaymentAsync(Record("B"), Ct);
        await store.MarkHandledAsync("A", Ct);

        RecordedPaymentPage page = await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct);

        // Unlike GetUnhandledPaymentsAsync, this listing keeps handled payments: it is a history, not a
        // work queue, and an unhandled payment that is not recent is the shape a stuck delivery has.
        Assert.True(page.Items.Single(item => item.Payment.TransactionHash == "A").Handled);
        Assert.False(page.Items.Single(item => item.Payment.TransactionHash == "B").Handled);
    }

    [Fact]
    public async Task ARecordedPaymentKeepsEveryFieldItWasStoredWith()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();

        PaymentRecord original = new PaymentRecord
        {
            TransactionHash = "A",
            TransactionType = "Payment",
            Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
            DestinationTag = 42u,
            Currency = "524C555344000000000000000000000000000000",
            Issuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De",
            Value = 12.345678m,
            LedgerIndex = 98_765_432u,
            ProcessedAt = new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.Zero),
        };
        await store.TryAddPaymentAsync(original, Ct);

        PaymentRecord listed = Assert.Single((await directory.ListPaymentsAsync(PaymentAttribution.Any, 10, 0, Ct)).Items).Payment;

        Assert.Equal(original.TransactionHash, listed.TransactionHash);
        Assert.Equal(original.TransactionType, listed.TransactionType);
        Assert.Equal(original.Sender, listed.Sender);
        Assert.Equal(original.DestinationTag, listed.DestinationTag);
        Assert.Equal(original.Currency, listed.Currency);
        Assert.Equal(original.Issuer, listed.Issuer);
        Assert.Equal(original.Value, listed.Value);
        Assert.Equal(original.LedgerIndex, listed.LedgerIndex);
        Assert.Equal(original.ProcessedAt, listed.ProcessedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositivePageSizeIsRefused(int limit)
    {
        (IPaymentStore _, IPaymentDirectory directory) = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.ListBuyersAsync(limit, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => directory.ListPaymentsAsync(PaymentAttribution.Any, limit, 0, Ct));
    }

    [Fact]
    public async Task ANegativeOffsetIsRefused()
    {
        (IPaymentStore _, IPaymentDirectory directory) = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.ListBuyersAsync(10, -1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => directory.ListPaymentsAsync(PaymentAttribution.Any, 10, -1, Ct));
    }

    [Fact]
    public async Task AnUnknownAttributionIsRefusedRatherThanMatchingEverything()
    {
        (IPaymentStore store, IPaymentDirectory directory) = await CreateAsync();
        await store.TryAddPaymentAsync(Record("A"), Ct);

        // A filter that silently widens is how a screen labelled "belongs to nobody" ends up showing
        // every payment there is.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => directory.ListPaymentsAsync((PaymentAttribution)99, 10, 0, Ct));
    }
}
