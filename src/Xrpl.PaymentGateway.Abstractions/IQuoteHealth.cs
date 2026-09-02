namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Liveness of the quote collector, for whatever scheduler the host already runs.</summary>
public interface IQuoteHealth
{
    /// <summary>Reads the current state. Cheap enough to call every few seconds.</summary>
    Task<QuoteHealthReport> CheckAsync(CancellationToken cancellationToken);
}
