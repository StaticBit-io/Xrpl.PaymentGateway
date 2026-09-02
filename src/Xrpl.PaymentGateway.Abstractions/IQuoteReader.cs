namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Reads the gateway's current quotes. Answers come from held state, not from the network.</summary>
public interface IQuoteReader
{
    /// <summary>
    /// The current marginal price for an asset, or null when there is no usable reading.
    /// </summary>
    /// <returns>
    /// Null when the asset has no configured pair, when nothing has been captured yet, or when the
    /// reading is past its age limit and stale readings are refused.
    /// </returns>
    Task<QuoteView?> GetPriceAsync(string currency, string? issuer, CancellationToken cancellationToken);

    /// <summary>
    /// Prices a specific amount against the current reading.
    /// </summary>
    /// <param name="currency">The currency code to price.</param>
    /// <param name="issuer">The issuer address, or null for XRP.</param>
    /// <param name="amount">
    /// For <see cref="QuoteDirection.ExactInput"/>, how much of the asset is being sent; for
    /// <see cref="QuoteDirection.ExactOutput"/>, how much of the quote asset must arrive.
    /// </param>
    /// <param name="direction">The direction of the quote: exact input or exact output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null on the same conditions as <see cref="GetPriceAsync"/>.</returns>
    Task<QuoteView?> QuoteAsync(
        string currency,
        string? issuer,
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken);
}
