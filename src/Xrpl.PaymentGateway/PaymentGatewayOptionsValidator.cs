using Microsoft.Extensions.Options;

namespace Xrpl.PaymentGateway;

/// <summary>Fails fast on a misconfigured gateway rather than at the first payment.</summary>
public sealed class PaymentGatewayOptionsValidator : IValidateOptions<PaymentGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentGatewayOptions options)
    {
        List<string> failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Address))
        {
            failures.Add($"{nameof(options.Address)} must be the receiving r-address.");
        }

        if (options.Nodes is null || options.Nodes.Count == 0)
        {
            failures.Add($"{nameof(options.Nodes)} must contain at least one node.");
        }
        else
        {
            foreach (Uri node in options.Nodes.Concat(options.CatchUpNodes ?? Array.Empty<Uri>()))
            {
                if (node.Scheme is not ("ws" or "wss"))
                {
                    failures.Add($"node {node} must use the ws or wss scheme.");
                }
            }
        }

        if (options.FirstDestinationTag == 0)
        {
            failures.Add($"{nameof(options.FirstDestinationTag)} must be greater than zero; tag 0 reads as \"no tag\" in many wallets.");
        }

        if (options.LedgerStallTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.LedgerStallTimeout)} must be positive.");
        }

        if (options.StreamBufferCapacity <= 0)
        {
            failures.Add($"{nameof(options.StreamBufferCapacity)} must be positive.");
        }

        if (options.HealthUnhandledSampleSize <= 0)
        {
            failures.Add($"{nameof(options.HealthUnhandledSampleSize)} must be positive.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
