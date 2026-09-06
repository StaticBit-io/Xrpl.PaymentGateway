using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>The same directory contract against a plain file.</summary>
public class FilePaymentDirectoryTests : PaymentDirectoryContract, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "xrplpg-directory-tests", Guid.NewGuid().ToString("N"));

    private readonly List<FilePaymentStore> _opened = new List<FilePaymentStore>();

    protected override Task<(IPaymentStore Store, IPaymentDirectory Directory)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        FilePaymentStore store = new FilePaymentStore(Path.Combine(_directory, "payments.json"));
        _opened.Add(store);
        return Task.FromResult<(IPaymentStore, IPaymentDirectory)>((store, store));
    }

    public void Dispose()
    {
        foreach (FilePaymentStore store in _opened)
        {
            store.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever written, which is the only reason this can be missing.
        }
    }
}
