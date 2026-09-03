using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Reads the sample's quote configuration out of <c>Xrpl:Quotes</c> — the common asset every accepted
/// currency is priced into, which currencies are priced against it and at what fixed demo rate, and which
/// currencies <see cref="FixedRateQuoteSource"/> refuses to price.
/// </summary>
/// <remarks>
/// Left empty by default. With no pairs configured, <see cref="IsEnabled"/> is false and
/// <c>Program.cs</c> never calls <c>AddXrplPaymentQuotes</c> at all — quotes stay exactly as optional in
/// the sample as they are in the library.
/// </remarks>
public sealed class QuoteConfiguration
{
    private readonly string? _quoteCurrencyCanonical;
    private readonly string? _quoteIssuer;

    private QuoteConfiguration(
        IReadOnlyList<QuotePair> pairs,
        IReadOnlyDictionary<string, decimal> ratesByPairKey,
        IReadOnlyCollection<(string Currency, string? Issuer)> refusedCurrencies,
        string? quoteCurrency,
        string? quoteIssuer)
    {
        Pairs = pairs;
        RatesByPairKey = ratesByPairKey;
        RefusedCurrencies = refusedCurrencies;

        // Canonicalized only when there is at least one pair to go with it — with none configured,
        // quoteCurrency may be the shipped default's empty string, which CurrencyKey.Canonical would
        // reject outright, and IsQuoteAsset has nothing to compare against either way.
        _quoteCurrencyCanonical = pairs.Count > 0 && quoteCurrency is not null
            ? CurrencyKey.Canonical(quoteCurrency)
            : null;
        _quoteIssuer = quoteIssuer;

        List<AcceptedAsset> accepted = new List<AcceptedAsset>(pairs.Count + 1);
        foreach (QuotePair pair in pairs)
        {
            accepted.Add(new AcceptedAsset(pair.Currency, pair.Issuer, false));
        }

        // The quote asset last, and only when there is a pair to quote into it: it has no pair of its own
        // — QuotePair refuses to quote an asset against itself — so nothing above would have listed it.
        if (pairs.Count > 0 && quoteCurrency is not null)
        {
            accepted.Add(new AcceptedAsset(quoteCurrency, quoteIssuer, true));
        }

        AcceptedAssets = accepted;
    }

    /// <summary>The configured pairs, ready for <see cref="QuoteOptions.Pairs"/>.</summary>
    public IReadOnlyList<QuotePair> Pairs { get; }

    /// <summary>Fixed demo rate per <see cref="QuotePair.Key"/>, for <see cref="FixedRateQuoteSource"/>.</summary>
    public IReadOnlyDictionary<string, decimal> RatesByPairKey { get; }

    /// <summary>Currencies <see cref="FixedRateQuoteSource"/> refuses to price, by currency and issuer.</summary>
    public IReadOnlyCollection<(string Currency, string? Issuer)> RefusedCurrencies { get; }

    /// <summary>
    /// Every asset this sample can name: each pair's received asset, plus the asset they are all priced
    /// into. Empty with quotes off — the gateway accepts whatever is sent to it either way, but with no
    /// pairs configured the sample knows the name of none of it.
    /// </summary>
    public IReadOnlyList<AcceptedAsset> AcceptedAssets { get; }

    /// <summary>Whether any pair is configured. False leaves quoting entirely out of the wiring.</summary>
    public bool IsEnabled => Pairs.Count > 0;

    /// <summary>
    /// Whether a payment in this currency and issuer is already the asset every pair prices into — USD in
    /// the shipped sample. <see cref="QuotePair"/> rejects quoting an asset against itself, so this asset
    /// has no pair and the library queues no valuation for it; the sample shows it at its own amount
    /// instead of leaving the payment row waiting on a signal that will never arrive. Always false with no
    /// pairs configured, the same as every other quoting behaviour here.
    /// </summary>
    public bool IsQuoteAsset(string currency, string? issuer) =>
        _quoteCurrencyCanonical is not null
        && string.Equals(CurrencyKey.Canonical(currency), _quoteCurrencyCanonical, StringComparison.Ordinal)
        && string.Equals(issuer, _quoteIssuer, StringComparison.Ordinal);

    public static QuoteConfiguration FromConfiguration(IConfiguration configuration)
    {
        string? quoteCurrency = NullIfEmpty(configuration["Xrpl:Quotes:QuoteCurrency"]);
        string? quoteIssuer = NullIfEmpty(configuration["Xrpl:Quotes:QuoteIssuer"]);

        List<PairEntry> pairEntries =
            configuration.GetSection("Xrpl:Quotes:Pairs").Get<List<PairEntry>>() ?? new List<PairEntry>();
        List<CurrencyEntry> refusedEntries =
            configuration.GetSection("Xrpl:Quotes:RefusedCurrencies").Get<List<CurrencyEntry>>() ?? new List<CurrencyEntry>();

        List<QuotePair> pairs = new List<QuotePair>(pairEntries.Count);
        Dictionary<string, decimal> rates = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (PairEntry entry in pairEntries)
        {
            QuotePair pair = new QuotePair(
                entry.Currency ?? throw new InvalidOperationException("Xrpl:Quotes:Pairs entry needs a Currency"),
                NullIfEmpty(entry.Issuer),
                quoteCurrency ?? throw new InvalidOperationException(
                    "Xrpl:Quotes:QuoteCurrency is required once Xrpl:Quotes:Pairs has an entry"),
                quoteIssuer);

            pairs.Add(pair);
            rates[pair.Key] = entry.Rate;
        }

        List<(string Currency, string? Issuer)> refused = refusedEntries
            .Select(entry => (
                entry.Currency ?? throw new InvalidOperationException("Xrpl:Quotes:RefusedCurrencies entry needs a Currency"),
                NullIfEmpty(entry.Issuer)))
            .ToList();

        return new QuoteConfiguration(pairs, rates, refused, quoteCurrency, quoteIssuer);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Binding shape for one <c>Xrpl:Quotes:Pairs</c> entry — an asset accepted and priced into
    /// the shared <c>QuoteCurrency</c>/<c>QuoteIssuer</c> above it.</summary>
    private sealed class PairEntry
    {
        public string? Currency { get; set; }

        public string? Issuer { get; set; }

        /// <summary>Quote-asset units per unit of the received asset — what <see cref="FixedRateQuoteSource"/> always answers with.</summary>
        public decimal Rate { get; set; }
    }

    /// <summary>Binding shape for one <c>Xrpl:Quotes:RefusedCurrencies</c> entry.</summary>
    private sealed class CurrencyEntry
    {
        public string? Currency { get; set; }

        public string? Issuer { get; set; }
    }
}

/// <summary>One asset the sample accepts, as the page and the demo payer need to see it.</summary>
/// <param name="Currency">The currency code as configured — readable rather than hex, if that is how it was written.</param>
/// <param name="Issuer">The issuing account, or null for XRP.</param>
/// <param name="IsQuoteAsset">Whether this is the asset everything else is priced into, and so needs no pair.</param>
public sealed record AcceptedAsset(string Currency, string? Issuer, bool IsQuoteAsset);
