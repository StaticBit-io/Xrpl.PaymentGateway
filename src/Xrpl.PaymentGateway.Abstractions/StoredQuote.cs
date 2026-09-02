namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>The last liquidity reading for one pair, as persisted.</summary>
public sealed class StoredQuote
{
    /// <summary>Canonical pair key, from <see cref="QuotePair.Key"/>.</summary>
    public required string PairKey { get; init; }

    /// <summary>Received asset's currency, as configured.</summary>
    public required string Currency { get; init; }

    /// <summary>Received asset's issuer, null for XRP.</summary>
    public string? Issuer { get; init; }

    /// <summary>Quote asset's currency, as configured.</summary>
    public required string QuoteCurrency { get; init; }

    /// <summary>Quote asset's issuer, null for XRP.</summary>
    public string? QuoteIssuer { get; init; }

    /// <summary>Marginal price at the capture, or null when the pair had no liquidity.</summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>Validated ledger the capture read, or null when there was no liquidity to read.</summary>
    public uint? LedgerIndex { get; init; }

    /// <summary>When the last successful capture completed, or null when there has never been one.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>When a refresh was last attempted, successfully or not.</summary>
    public required DateTimeOffset LastAttemptAt { get; init; }

    /// <summary>Consecutive failed attempts since the last success. Zero after a success.</summary>
    public required int ConsecutiveFailures { get; init; }

    /// <summary>Message from the last failure, or null when the last attempt succeeded.</summary>
    public string? LastError { get; init; }
}
