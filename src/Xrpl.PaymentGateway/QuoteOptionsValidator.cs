using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>Fails fast on a misconfigured collector rather than at the first refresh nobody is watching.</summary>
public sealed class QuoteOptionsValidator : IValidateOptions<QuoteOptions>
{
    public ValidateOptionsResult Validate(string? name, QuoteOptions options)
    {
        List<string> failures = new List<string>();

        if (options.Pairs is null || options.Pairs.Count == 0)
        {
            failures.Add($"{nameof(options.Pairs)} must contain at least one pair; quotes were enabled with nothing to quote.");
        }
        else
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (QuotePair pair in options.Pairs)
            {
                if (!seen.Add(pair.Key))
                {
                    // Canonical keys make this catch "XPM" and its hex form as one pair.
                    failures.Add($"duplicate pair {pair.Key}: the same asset is configured twice.");
                }
            }
        }

        if (options.RefreshInterval <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.RefreshInterval)} must be positive.");
        }

        if (options.MinimumPairStagger < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.MinimumPairStagger)} must not be negative.");
        }

        if (options.MaxQuoteAge is { } maxAge)
        {
            if (maxAge <= TimeSpan.Zero)
            {
                failures.Add($"{nameof(options.MaxQuoteAge)} must be positive.");
            }
            else if (options.RefreshInterval > TimeSpan.Zero && maxAge < options.RefreshInterval)
            {
                failures.Add(
                    $"{nameof(options.MaxQuoteAge)} must not be shorter than {nameof(options.RefreshInterval)}; "
                    + "every reading would be stale the moment after it was written.");
            }
        }

        if (options.CaptureTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.CaptureTimeout)} must be positive.");
        }

        if (options.ValuationBatchSize <= 0)
        {
            failures.Add($"{nameof(options.ValuationBatchSize)} must be positive.");
        }

        if (options.ValuationPollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ValuationPollInterval)} must be positive.");
        }

        if (options.EnqueueTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.EnqueueTimeout)} must be positive.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
