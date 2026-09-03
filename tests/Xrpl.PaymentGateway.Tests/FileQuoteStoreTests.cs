using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class FileQuoteStoreTests : QuoteStoreContract, IDisposable
{
    private readonly List<string> _paths = new List<string>();
    private readonly List<FileQuoteStore> _stores = new List<FileQuoteStore>();

    protected override Task<IQuoteStore> CreateAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xrplpg-quotes-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return Task.FromResult<IQuoteStore>(Track(new FileQuoteStore(path)));
    }

    protected override Task<IQuoteStore?> ReopenAsync(IQuoteStore store)
    {
        // The same file, opened again, is what a restart looks like.
        string path = ((FileQuoteStore)store).Path;
        return Task.FromResult<IQuoteStore?>(Track(new FileQuoteStore(path)));
    }

    [Fact]
    public async Task AFileThatDoesNotExistYetIsAnEmptyStoreRatherThanAnError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xrplpg-quotes-{Guid.NewGuid():N}.json");
        _paths.Add(path);

        FileQuoteStore store = Track(new FileQuoteStore(path));

        Assert.False(File.Exists(path));
        Assert.Empty(await store.GetQuotesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedEnqueueDoesNotBlockRetryingTheSameHash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileQuoteStore store = Track(NewStore(out string path));

        using (BlockedSave(path))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => store.TryEnqueueValuationAsync(Pending("HASH1"), ct));
        }

        // If the failed attempt had left the hash marked queued in memory while the file never agreed,
        // this would be refused as a duplicate instead of accepted.
        Assert.True(await store.TryEnqueueValuationAsync(Pending("HASH1"), ct));
        Assert.Single(await store.GetPendingValuationsAsync(10, ct));
    }

    [Fact]
    public async Task AFailedSaveValuationLeavesTheEntryPendingRatherThanUndelivered()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileQuoteStore store = Track(NewStore(out string path));
        PaymentValuation pending = Pending("HASH1");
        await store.TryEnqueueValuationAsync(pending, ct);

        using (BlockedSave(path))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => store.SaveValuationAsync(Valued(pending), ct));
        }

        // The write failed, so the file still holds the entry pending. In-memory state must agree: not
        // valued-but-undelivered (which would deliver it now from memory and, after a restart, price and
        // deliver it again from a file that never recorded the valuation at all).
        IReadOnlyList<PaymentValuation> stillPending = await store.GetPendingValuationsAsync(10, ct);
        Assert.Single(stillPending);
        Assert.False(stillPending[0].IsValued);
        Assert.Empty(await store.GetUndeliveredValuationsAsync(10, ct));
    }

    private FileQuoteStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"xrplpg-quotes-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new FileQuoteStore(path);
    }

    private static PaymentValuation Pending(string hash) => new PaymentValuation
    {
        TransactionHash = hash,
        PairKey = "PAIR",
        Amount = 1000m,
        PaymentLedgerIndex = 901,
        EnqueuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 5, TimeSpan.Zero),
    };

    private static PaymentValuation Valued(PaymentValuation pending) => new PaymentValuation
    {
        TransactionHash = pending.TransactionHash,
        PairKey = pending.PairKey,
        Amount = pending.Amount,
        PaymentLedgerIndex = pending.PaymentLedgerIndex,
        EnqueuedAt = pending.EnqueuedAt,
        State = ValuationState.Valued,
        ValuedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 30, TimeSpan.Zero),
        QuoteAmount = 9.9m,
    };

    /// <summary>
    /// Makes the store's next save fail deterministically: <c>SaveAsync</c> writes to <c>path + ".tmp"</c>
    /// before the atomic rename, and creating a file where a directory of that name already exists throws
    /// on every platform this runs on. Dispose removes the blocking directory again.
    /// </summary>
    private static IDisposable BlockedSave(string path)
    {
        string temporary = path + ".tmp";
        Directory.CreateDirectory(temporary);
        return new BlockedSaveScope(temporary);
    }

    private sealed class BlockedSaveScope : IDisposable
    {
        private readonly string _temporary;

        public BlockedSaveScope(string temporary) => _temporary = temporary;

        public void Dispose()
        {
            if (Directory.Exists(_temporary))
            {
                Directory.Delete(_temporary);
            }
        }
    }

    private FileQuoteStore Track(FileQuoteStore store)
    {
        _stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (FileQuoteStore store in _stores)
        {
            store.Dispose();
        }

        foreach (string path in _paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
