using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>Hands buyers the address and tag to pay to. All the state lives in the host's store.</summary>
public sealed class XrplPaymentGateway : IPaymentGateway
{
    private readonly IPaymentStore _store;
    private readonly PaymentGatewayOptions _options;

    public XrplPaymentGateway(IPaymentStore store, IOptions<PaymentGatewayOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task<PaymentInstructions> GetPaymentInstructionsAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        uint tag = await _store.GetOrAssignTagAsync(buyerId, cancellationToken).ConfigureAwait(false);

        return new PaymentInstructions
        {
            Address = _options.Address,
            DestinationTag = tag,
        };
    }
}
