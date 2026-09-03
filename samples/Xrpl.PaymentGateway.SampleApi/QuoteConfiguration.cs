using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Reads the sample's quote configuration out of <c>Xrpl:Quotes</c> — which pairs to keep a reading for,
/// the fixed demo rate for each, and which currencies <see cref="FixedRateQuoteSource"/> refuses to price.
/// </summary>
/// <remarks>
/// Left empty by default. With no pairs configured, <see cref="IsEnabled"/> is false and
/// <c>Program.cs</c> never calls <c>AddXrplPaymentQuotes</c> at all — quotes stay exactly as optional in
/// the sample as they are in the library.
/// </remarks>
public sealed class QuoteConfiguration
{
    private QuoteConfiguration(
        IReadOnlyList<QuotePair> pairs,
        IReadOnlyDictionary<string, decimal> ratesByPairKey,
        IReadOnlyCollection<(string Currency, string? Issuer)> refusedCurrencies)
    {
        Pairs = pairs;
        RatesByPairKey = ratesByPairKey;
        RefusedCurrencies = refusedCurrencies;
    }

    /// <summary>The configured pairs, ready for <see cref="QuoteOptions.Pairs"/>.</summary>
    public IReadOnlyList<QuotePair> Pairs { get; }

    /// <summary>Fixed demo rate per <see cref="QuotePair.Key"/>, for <see cref="FixedRateQuoteSource"/>.</summary>
    public IReadOnlyDictionary<string, decimal> RatesByPairKey { get; }

    /// <summary>Currencies <see cref="FixedRateQuoteSource"/> refuses to price, by currency and issuer.</summary>
    public IReadOnlyCollection<(string Currency, string? Issuer)> RefusedCurrencies { get; }

    /// <summary>Whether any pair is configured. False leaves quoting entirely out of the wiring.</summary>
    public bool IsEnabled => Pairs.Count > 0;

    public static QuoteConfiguration FromConfiguration(IConfiguration configuration)
    {
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
                entry.QuoteCurrency ?? throw new InvalidOperationException("Xrpl:Quotes:Pairs entry needs a QuoteCurrency"),
                NullIfEmpty(entry.QuoteIssuer));

            pairs.Add(pair);
            rates[pair.Key] = entry.Rate;
        }

        List<(string Currency, string? Issuer)> refused = refusedEntries
            .Select(entry => (
                entry.Currency ?? throw new InvalidOperationException("Xrpl:Quotes:RefusedCurrencies entry needs a Currency"),
                NullIfEmpty(entry.Issuer)))
            .ToList();

        return new QuoteConfiguration(pairs, rates, refused);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Binding shape for one <c>Xrpl:Quotes:Pairs</c> entry.</summary>
    private sealed class PairEntry
    {
        public string? Currency { get; set; }

        public string? Issuer { get; set; }

        public string? QuoteCurrency { get; set; }

        public string? QuoteIssuer { get; set; }

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
