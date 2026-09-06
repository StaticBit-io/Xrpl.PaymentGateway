using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>
/// The reference implementation of the directory. Everything it must do lives in
/// <see cref="PaymentDirectoryContract"/>.
/// </summary>
public class InMemoryPaymentDirectoryTests : PaymentDirectoryContract
{
    protected override Task<(IPaymentStore Store, IPaymentDirectory Directory)> CreateAsync()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        return Task.FromResult<(IPaymentStore, IPaymentDirectory)>((store, store));
    }
}
