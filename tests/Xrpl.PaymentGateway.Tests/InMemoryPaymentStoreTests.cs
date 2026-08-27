using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// The reference implementation. Everything it must do lives in <see cref="PaymentStoreContract"/>; only
/// what is specific to this store is written out here.
/// </summary>
public class InMemoryPaymentStoreTests : PaymentStoreContract
{
    protected override Task<IPaymentStore> CreateAsync(uint firstDestinationTag = 1) =>
        Task.FromResult<IPaymentStore>(new InMemoryPaymentStore(firstDestinationTag));

    [Fact]
    public void TagZeroIsRefusedAtConstruction()
    {
        // Many wallets read a destination tag of 0 as "no tag", so it is never handed out.
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryPaymentStore(firstDestinationTag: 0));
    }

    [Fact]
    public async Task TheSnapshotShowsHandledAndUnhandledPaymentsAlike()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        await store.TryAddPaymentAsync(
            new PaymentRecord
            {
                TransactionHash = "A",
                TransactionType = "Payment",
                Sender = "rSender",
                Currency = "XRP",
                Value = 1m,
                LedgerIndex = 10,
                ProcessedAt = DateTimeOffset.UnixEpoch,
            },
            TestContext.Current.CancellationToken);
        await store.MarkHandledAsync("A", TestContext.Current.CancellationToken);

        // GetUnhandledPaymentsAsync has nothing left to show; the snapshot is what the sample reads.
        Assert.Empty(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
        Assert.Single(store.Snapshot());
    }
}
