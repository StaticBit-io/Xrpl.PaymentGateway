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
