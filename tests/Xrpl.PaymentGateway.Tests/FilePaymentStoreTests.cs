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
