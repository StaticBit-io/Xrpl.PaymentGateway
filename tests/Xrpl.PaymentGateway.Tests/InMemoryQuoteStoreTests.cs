using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests;

public class InMemoryQuoteStoreTests : QuoteStoreContract
{
    protected override Task<IQuoteStore> CreateAsync() =>
        Task.FromResult<IQuoteStore>(new InMemoryQuoteStore());
}
