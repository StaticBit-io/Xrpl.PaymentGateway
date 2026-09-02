using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// An <see cref="IQuoteStore"/> whose enqueue write never completes on its own, for proving that
/// <c>ValuationEnqueuer</c> bounds it with <see cref="QuoteOptions.EnqueueTimeout"/> rather than
/// blocking the payment path indefinitely.
/// </summary>
public sealed class HangingQuoteStore : IQuoteStore
{
    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) =>
        Task.FromResult<StoredQuote?>(null);

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredQuote>>(Array.Empty<StoredQuote>());

    public async Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
        Task.FromResult<PaymentValuation?>(null);
}
