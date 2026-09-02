namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Persistence for quotes and valuations. Separate from <see cref="IPaymentStore"/> on purpose.
/// </summary>
/// <remarks>
/// <para>
/// Adding these methods to <see cref="IPaymentStore"/> would break every implementation written against
/// 1.0.0. Keeping them apart also keeps them optional: a host with no interest in prices registers no
/// quote store and nothing about its gateway changes.
/// </para>
/// <para>
/// One hard requirement: <see cref="TryEnqueueValuationAsync"/> must enforce uniqueness of
/// <see cref="PaymentValuation.TransactionHash"/> and return false on a duplicate rather than throwing.
/// The same payment is offered to the queue by live processing, by catch-up and by reconciliation.
/// </para>
/// </remarks>
public interface IQuoteStore
{
    /// <summary>Writes the latest reading for a pair, replacing any previous one.</summary>
    Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken);

    /// <summary>The stored reading for a pair, or null when the pair has never been refreshed.</summary>
    Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken);

    /// <summary>Every stored reading.</summary>
    Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken);

    /// <summary>Queues a payment for valuation. Returns false when the hash is already queued or valued.</summary>
    Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> queued but not yet valued entries, oldest first.</summary>
    Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Replaces a queued entry with its computed valuation.</summary>
    Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> valued but undelivered entries, oldest first.</summary>
    Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Marks a valuation as delivered to the host handler.</summary>
    Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken);

    /// <summary>The valuation for one payment, queued or complete, or null when there is none.</summary>
    Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken);
}
