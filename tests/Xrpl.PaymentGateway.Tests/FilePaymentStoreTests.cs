using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// The same contract as every other store, against a plain file. This is what backs the claim that a
/// gateway can be run without a database.
/// </summary>
public class FilePaymentStoreTests : PaymentStoreContract, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "xrplpg-store-tests", Guid.NewGuid().ToString("N"));

    private readonly List<FilePaymentStore> _opened = new List<FilePaymentStore>();

    private string StatePath => Path.Combine(_directory, "payments.json");

    protected override Task<IPaymentStore> CreateAsync(uint firstDestinationTag = 1)
    {
        Directory.CreateDirectory(_directory);
        FilePaymentStore store = new FilePaymentStore(StatePath, firstDestinationTag);
        _opened.Add(store);
        return Task.FromResult<IPaymentStore>(store);
    }

    protected override Task<IPaymentStore?> ReopenAsync(IPaymentStore store)
    {
        // A restart: the file is all that carries over.
        ((FilePaymentStore)store).Dispose();
        _opened.Remove((FilePaymentStore)store);

        FilePaymentStore reopened = new FilePaymentStore(StatePath);
        _opened.Add(reopened);
        return Task.FromResult<IPaymentStore?>(reopened);
    }

    [Fact]
    public void TagZeroIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FilePaymentStore(StatePath, firstDestinationTag: 0));
    }

    [Fact]
    public async Task TheFileIsCreatedOnFirstWriteRatherThanAtConstruction()
    {
        Directory.CreateDirectory(_directory);
        FilePaymentStore store = new FilePaymentStore(StatePath);
        _opened.Add(store);
        Assert.False(File.Exists(StatePath));

        await store.SetLastProcessedLedgerAsync(1, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBehind()
    {
        IPaymentStore store = await CreateAsync();

        await store.SetLastProcessedLedgerAsync(5, TestContext.Current.CancellationToken);

        // The write goes through a temporary file and an atomic replace; the temporary must not survive it.
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public async Task ATagIsOnDiskBeforeItIsHandedToTheCaller()
    {
        IPaymentStore store = await CreateAsync();

        uint tag = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);

        // Losing this in a crash would issue the same tag twice, to two different buyers.
        string written = await File.ReadAllTextAsync(StatePath, TestContext.Current.CancellationToken);
        Assert.Contains("buyer-1", written);
        Assert.Contains(tag.ToString(), written);
    }

    [Fact]
    public async Task AFailedTryAddPaymentDoesNotBlockRetryingTheSameHash()
    {
        // Pre-existing defect, the same shape as FileQuoteStore's: TryAddPaymentAsync mutated _state and
        // then called SaveAsync without rolling back on failure, so a payment that failed to persist would
        // still read as stored in memory and refuse the retry that would repair it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPaymentStore store = await CreateAsync();
        PaymentRecord record = Record("HASH1");

        using (BlockedSave())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.TryAddPaymentAsync(record, ct));
        }

        Assert.True(await store.TryAddPaymentAsync(record, ct));
        Assert.Single(await store.GetUnhandledPaymentsAsync(10, ct));
    }

    [Fact]
    public async Task AFailedMarkHandledLeavesThePaymentUnhandled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPaymentStore store = await CreateAsync();
        await store.TryAddPaymentAsync(Record("HASH1"), ct);

        using (BlockedSave())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.MarkHandledAsync("HASH1", ct));
        }

        // The write failed, so the file still lists HASH1 unhandled. In-memory state must agree, or
        // reconciliation would never see it as needing redelivery while a restart finds it unhandled again.
        Assert.Single(await store.GetUnhandledPaymentsAsync(10, ct));
    }

    [Fact]
    public async Task AFailedTagAssignmentDoesNotHandOutTheSameTagTwice()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPaymentStore store = await CreateAsync();

        using (BlockedSave())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.GetOrAssignTagAsync("buyer-1", ct));
        }

        // The write failed, so the file never agreed buyer-1 has a tag. If NextTag had not been rolled
        // back, buyer-2 would receive the tag the failed call already handed out in memory.
        uint firstTag = await store.GetOrAssignTagAsync("buyer-1", ct);
        uint secondTag = await store.GetOrAssignTagAsync("buyer-2", ct);
        Assert.NotEqual(firstTag, secondTag);
    }

    private static PaymentRecord Record(string hash) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        Currency = "XRP",
        Value = 1m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>
    /// Makes the store's next save fail deterministically: <c>SaveAsync</c> writes to
    /// <c>StatePath + ".tmp"</c> before the atomic rename, and creating a file where a directory of that
    /// name already exists throws on every platform this runs on. Dispose removes the blocking directory.
    /// </summary>
    private IDisposable BlockedSave()
    {
        Directory.CreateDirectory(_directory);
        string temporary = StatePath + ".tmp";
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

    public void Dispose()
    {
        foreach (FilePaymentStore store in _opened)
        {
            store.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
