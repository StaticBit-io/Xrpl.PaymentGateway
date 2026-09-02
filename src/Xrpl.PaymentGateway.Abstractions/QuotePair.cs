namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// An asset the gateway receives, and the asset its value is expressed in.
/// </summary>
/// <remarks>
/// Validation happens here rather than at first use: a pair that names an issued currency without an
/// issuer, or XRP with one, addresses nothing on the ledger, and finding that out during a background
/// refresh means finding it out where nobody is looking.
/// </remarks>
public sealed record QuotePair
{
    /// <param name="currency">Received asset's currency code, readable or hex.</param>
    /// <param name="issuer">Received asset's issuer. Null only for XRP.</param>
    /// <param name="quoteCurrency">Currency the value is expressed in.</param>
    /// <param name="quoteIssuer">Quote currency's issuer. Null only for XRP.</param>
    public QuotePair(string currency, string? issuer, string quoteCurrency, string? quoteIssuer)
    {
        CurrencyCanonical = CurrencyKey.Canonical(currency);
        QuoteCurrencyCanonical = CurrencyKey.Canonical(quoteCurrency);

        RequireIssuerConsistency(CurrencyCanonical, issuer, nameof(currency));
        RequireIssuerConsistency(QuoteCurrencyCanonical, quoteIssuer, nameof(quoteCurrency));

        Currency = currency;
        Issuer = issuer;
        QuoteCurrency = quoteCurrency;
        QuoteIssuer = quoteIssuer;

        if (string.Equals(CurrencyCanonical, QuoteCurrencyCanonical, StringComparison.Ordinal)
            && string.Equals(issuer, quoteIssuer, StringComparison.Ordinal))
        {
            throw new ArgumentException("an asset cannot be quoted against itself", nameof(quoteCurrency));
        }

        Key = $"{CurrencyCanonical}.{issuer ?? "-"}/{QuoteCurrencyCanonical}.{quoteIssuer ?? "-"}";
    }

    /// <summary>Received asset's currency code as configured.</summary>
    public string Currency { get; }

    /// <summary>Received asset's issuer, or null for XRP.</summary>
    public string? Issuer { get; }

    /// <summary>Currency the value is expressed in, as configured.</summary>
    public string QuoteCurrency { get; }

    /// <summary>Quote currency's issuer, or null for XRP.</summary>
    public string? QuoteIssuer { get; }

    /// <summary>Received currency in canonical form.</summary>
    public string CurrencyCanonical { get; }

    /// <summary>Quote currency in canonical form.</summary>
    public string QuoteCurrencyCanonical { get; }

    /// <summary>Storage key. Stable across the two ways a currency code can be written.</summary>
    public string Key { get; }

    /// <summary>Whether an asset seen on the ledger is the one this pair prices.</summary>
    public bool Matches(string currency, string? issuer) =>
        string.Equals(CurrencyKey.Canonical(currency), CurrencyCanonical, StringComparison.Ordinal)
        && string.Equals(issuer, Issuer, StringComparison.Ordinal);

    private static void RequireIssuerConsistency(string canonical, string? issuer, string parameterName)
    {
        bool isXrp = string.Equals(canonical, "XRP", StringComparison.Ordinal);
        if (isXrp && issuer is not null)
        {
            throw new ArgumentException("XRP has no issuer", parameterName);
        }

        if (!isXrp && string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException($"currency \"{canonical}\" needs an issuer", parameterName);
        }
    }
}
