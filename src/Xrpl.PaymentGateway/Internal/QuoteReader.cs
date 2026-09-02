using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Serves quotes off the held snapshots, applying the age policy.
/// </summary>
/// <remarks>
/// Internal because its constructor takes <see cref="QuoteRegistry"/>, which is internal; hosts reach it
/// through <see cref="IQuoteReader"/>. Same reason the monitor and the health service are internal.
/// </remarks>
internal sealed class QuoteReader : IQuoteReader
{
    private readonly QuoteOptions _options;
    private readonly QuoteRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public QuoteReader(IOptions<QuoteOptions> options, QuoteRegistry registry, TimeProvider timeProvider)
    {
        _options = options.Value;
        _registry = registry;
        _timeProvider = timeProvider;
    }

    public Task<QuoteView?> GetPriceAsync(string currency, string? issuer, CancellationToken cancellationToken) =>
        ViewAsync(currency, issuer, amount: null, QuoteDirection.ExactInput, cancellationToken);

    public Task<QuoteView?> QuoteAsync(
        string currency,
        string? issuer,
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        return ViewAsync(currency, issuer, amount, direction, cancellationToken);
    }

    private async Task<QuoteView?> ViewAsync(
        string currency,
        string? issuer,
        decimal? amount,
        QuoteDirection direction,
        CancellationToken cancellationToken)
    {
        // Throws on a malformed code rather than returning null: "you asked for nonsense" and "we have no
        // quote for that pair" are different answers and the caller acts differently on each.
        _ = CurrencyKey.Canonical(currency);

        QuotePair? pair = _registry.FindPair(currency, issuer);
        if (pair is null)
        {
            return null;
        }

        IQuoteSnapshot? snapshot = _registry.GetSnapshot(pair.Key);
        if (snapshot is null)
        {
            return null;
        }

        TimeSpan age = _timeProvider.GetUtcNow() - snapshot.CapturedAt;
        bool stale = age > _options.EffectiveMaxQuoteAge;
        if (stale && _options.RefuseStaleQuotes)
        {
            return null;
        }

        QuoteResult? result = amount is { } value
            ? await snapshot.EvaluateAsync(value, direction, cancellationToken).ConfigureAwait(false)
            : null;

        return new QuoteView
        {
            Pair = pair,
            MarginalPrice = snapshot.MarginalPrice,
            LedgerIndex = snapshot.LedgerIndex,
            CapturedAt = snapshot.CapturedAt,
            Age = age,
            IsStale = stale,
            Result = result,
        };
    }
}
