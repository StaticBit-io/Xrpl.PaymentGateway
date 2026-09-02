namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Liquidity state for one pair, captured at one ledger, able to price any size against itself.
/// </summary>
/// <remarks>
/// The snapshot evaluates rather than exposing its contents, so the gateway can hold it without
/// depending on how a route is built. It also keeps the age of an answer inseparable from the answer:
/// a precise number computed from a stale book looks exactly like a fresh one, which is more dangerous
/// than an obviously old price.
/// </remarks>
public interface IQuoteSnapshot
{
    /// <summary>Validated ledger the state was read at.</summary>
    uint LedgerIndex { get; }

    /// <summary>When the capture completed.</summary>
    DateTimeOffset CapturedAt { get; }

    /// <summary>Best executable price for the pair, or null when there is no liquidity.</summary>
    decimal? MarginalPrice { get; }

    /// <summary>
    /// Prices an amount against the captured state. Returns null when the pair holds no liquidity at all.
    /// </summary>
    /// <remarks>
    /// Implementations must not perform network I/O: this is called on the checkout request path, and
    /// the whole reason the gateway holds a snapshot is that pricing a size costs nothing afterwards.
    /// Needing fresh data means capturing a new snapshot, not reaching out from inside this call.
    /// </remarks>
    ValueTask<QuoteResult?> EvaluateAsync(
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken);
}
