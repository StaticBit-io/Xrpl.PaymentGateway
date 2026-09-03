using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// A deliberate stand-in for a real pricing engine: fixed rates read from configuration, no network call
/// at all.
/// </summary>
/// <remarks>
/// The gateway does not compute prices — the host does, through <see cref="IQuoteSource"/>. A real
/// implementation would read <c>book_offers</c> or <c>amm_info</c> off the ledger, walk the book to the
/// requested size, and return a snapshot that reflects genuine liquidity at that moment; order-book and
/// AMM arithmetic is a subject of its own, and this sample is not it. This type exists only to show the
/// shape of the integration — what a host registers, and what the gateway then does with it — never to
/// price anything real. Do not point this at anything but the sample's own demo pairs.
/// </remarks>
public sealed class FixedRateQuoteSource : IQuoteSource
{
    private readonly IReadOnlyDictionary<string, decimal> _ratesByPairKey;
    private readonly IReadOnlyCollection<(string Currency, string? Issuer)> _refusedCurrencies;
    private int _capturesTaken;

    public FixedRateQuoteSource(
        IReadOnlyDictionary<string, decimal> ratesByPairKey,
        IReadOnlyCollection<(string Currency, string? Issuer)> refusedCurrencies)
    {
        _ratesByPairKey = ratesByPairKey ?? throw new ArgumentNullException(nameof(ratesByPairKey));
        _refusedCurrencies = refusedCurrencies ?? throw new ArgumentNullException(nameof(refusedCurrencies));
    }

    public Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);

        if (_refusedCurrencies.Any(refused => pair.Matches(refused.Currency, refused.Issuer)))
        {
            // Refusing must throw, not return null. Null means "asked, and there is genuinely nothing
            // there" — the collector treats that as a fact about the market. A refusal is this stand-in
            // saying it will never price this asset at all, which the collector must keep treating like
            // any other capture failure: log it, keep whatever was last held (nothing, here), and try
            // again next cycle. That is what leaves the resulting valuation entries genuinely stuck —
            // exactly the case the unresolved-valuation operator path exists for.
            throw new InvalidOperationException(
                $"the fixed-rate demo source is configured to refuse {pair.Currency}"
                + (pair.Issuer is null ? string.Empty : $".{pair.Issuer}"));
        }

        if (!_ratesByPairKey.TryGetValue(pair.Key, out decimal rate))
        {
            throw new InvalidOperationException($"no fixed demo rate configured for pair {pair.Key}");
        }

        int ledgerIndex = Interlocked.Increment(ref _capturesTaken);
        return Task.FromResult<IQuoteSnapshot?>(new FixedRateQuoteSnapshot(rate, (uint)ledgerIndex));
    }
}

/// <summary>A snapshot that prices any size at one fixed rate — there is no book here to run dry.</summary>
internal sealed class FixedRateQuoteSnapshot : IQuoteSnapshot
{
    private readonly decimal _rate;

    public FixedRateQuoteSnapshot(decimal rate, uint ledgerIndex)
    {
        _rate = rate;
        LedgerIndex = ledgerIndex;
        CapturedAt = DateTimeOffset.UtcNow;
    }

    // Not a real validated ledger index: this stand-in never reads the ledger. A real IQuoteSource would
    // report the ledger its book_offers/amm_info call was answered at, which is what makes an old
    // automatic valuation checkable against history.
    public uint LedgerIndex { get; }

    public DateTimeOffset CapturedAt { get; }

    public decimal? MarginalPrice => _rate;

    public ValueTask<QuoteResult?> EvaluateAsync(
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken)
    {
        // A fixed rate has no depth to exhaust, so every trade fills completely regardless of size or
        // direction — the one simplification a real order book or AMM could never make.
        decimal inputAmount = direction == QuoteDirection.ExactInput ? amount : amount / _rate;
        decimal outputAmount = inputAmount * _rate;

        return new ValueTask<QuoteResult?>(new QuoteResult
        {
            Direction = direction,
            InputAmount = inputAmount,
            FilledInput = inputAmount,
            OutputAmount = outputAmount,
            MarginalPrice = _rate,
            Route = "fixed-rate demo source",
        });
    }
}
