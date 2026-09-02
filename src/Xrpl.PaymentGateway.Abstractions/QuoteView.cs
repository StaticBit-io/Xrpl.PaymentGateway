namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>A quote together with how old the state behind it is.</summary>
/// <remarks>
/// The age travels with the number by construction. A price computed from a stale book looks exactly as
/// confident as a fresh one, so leaving the caller to remember to ask would be leaving them to forget.
/// </remarks>
public sealed class QuoteView
{
    /// <summary>The pair this priced.</summary>
    public required QuotePair Pair { get; init; }

    /// <summary>Best executable price at the snapshot, or null when the pair holds no liquidity.</summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>Validated ledger the snapshot was read at.</summary>
    public required uint LedgerIndex { get; init; }

    /// <summary>When the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>How long ago that was.</summary>
    public required TimeSpan Age { get; init; }

    /// <summary>Whether the snapshot is past the configured age limit.</summary>
    /// <remarks>Only ever true when the host turned <c>RefuseStaleQuotes</c> off.</remarks>
    public required bool IsStale { get; init; }

    /// <summary>The priced amount, or null when only the marginal price was asked for.</summary>
    public QuoteResult? Result { get; init; }
}
