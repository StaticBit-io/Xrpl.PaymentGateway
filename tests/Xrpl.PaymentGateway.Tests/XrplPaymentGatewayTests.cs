using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class XrplPaymentGatewayTests
{
    private const string Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";

    private static XrplPaymentGateway Create(IPaymentStore store) =>
        new XrplPaymentGateway(
            store,
            Options.Create(new PaymentGatewayOptions
            {
                Address = Address,
                Nodes = new[] { new Uri("ws://localhost:6006") },
            }));

    [Fact]
    public async Task InstructionsCarryTheReceivingAddressAndAFreshTag()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());

        PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(Address, instructions.Address);
        Assert.Equal(1u, instructions.DestinationTag);
    }

    [Fact]
    public async Task AReturningBuyerIsGivenTheTagTheyAlreadyHave()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());
        PaymentInstructions first = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);
        await gateway.GetPaymentInstructionsAsync("buyer-2", TestContext.Current.CancellationToken);

        PaymentInstructions again = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(first.DestinationTag, again.DestinationTag);
    }

    [Fact]
    public async Task AnEmptyBuyerIdIsRejected()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());

        await Assert.ThrowsAsync<ArgumentException>(
            () => gateway.GetPaymentInstructionsAsync("  ", TestContext.Current.CancellationToken));
    }
}
