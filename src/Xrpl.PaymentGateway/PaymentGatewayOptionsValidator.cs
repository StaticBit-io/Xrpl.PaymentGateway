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

        // Checked outside the else so that a bad CatchUpNodes entry is still reported when Nodes is empty.
        foreach (Uri node in (options.Nodes ?? Array.Empty<Uri>()).Concat(options.CatchUpNodes ?? Array.Empty<Uri>()))
        {
            if (node.Scheme is not ("ws" or "wss"))
            {
                failures.Add($"node {node} must use the ws or wss scheme.");
            }
        }

        if (options.FirstDestinationTag == 0)
        {
            failures.Add($"{nameof(options.FirstDestinationTag)} must be greater than zero; tag 0 reads as \"no tag\" in many wallets.");
        }

        RequirePositive(options.LedgerStallTimeout, nameof(options.LedgerStallTimeout));
        RequirePositive(options.NetworkStallProbeInterval, nameof(options.NetworkStallProbeInterval));
        RequirePositive(options.ReconnectBaseDelay, nameof(options.ReconnectBaseDelay));
        RequirePositive(options.ReconnectMaxDelay, nameof(options.ReconnectMaxDelay));
        RequirePositive(options.StoreRetryBaseDelay, nameof(options.StoreRetryBaseDelay));
        RequirePositive(options.StoreRetryMaxDelay, nameof(options.StoreRetryMaxDelay));

        if (options.ReconnectBaseDelay > options.ReconnectMaxDelay)
        {
            failures.Add($"{nameof(options.ReconnectBaseDelay)} must not exceed {nameof(options.ReconnectMaxDelay)}.");
        }

        if (options.StoreRetryBaseDelay > options.StoreRetryMaxDelay)
        {
            failures.Add($"{nameof(options.StoreRetryBaseDelay)} must not exceed {nameof(options.StoreRetryMaxDelay)}.");
        }

        void RequirePositive(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{name} must be positive.");
            }
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
