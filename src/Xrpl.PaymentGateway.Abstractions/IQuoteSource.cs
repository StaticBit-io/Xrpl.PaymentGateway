namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Reads liquidity for a pair off the ledger. The host supplies this; the gateway owns only the rhythm.
/// </summary>
/// <remarks>
/// The gateway deliberately does not compute prices. Order-book and AMM arithmetic is a subject of its
/// own, and a payment gateway that assumed one engine would be as wrong as one that assumed a database.
/// This is required once <c>AddXrplPaymentQuotes</c> is called — the collector calls it on every refresh
/// cycle regardless of configuration — even when <c>ValuateWithFreshSnapshot</c> is off, which only
/// controls whether <c>ValuationWorker</c> also calls it per payment.
/// </remarks>
public interface IQuoteSource
{
    /// <summary>
    /// Reads the current liquidity state for a pair. Called only by the background refresh loop.
    /// </summary>
    /// <returns>The snapshot, or null when the pair genuinely has no liquidity.</returns>
    /// <remarks>
    /// The distinction between the two failure shapes is load-bearing, so implementations must honour it:
    /// null means "asked, and there is nothing there", while a node that cannot be reached must throw.
    /// An empty book is what a disconnected client returns AND what an empty pair returns, and treating a
    /// dropped socket as an empty pair would overwrite a working quote with nothing.
    /// </remarks>
    Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken);
}
