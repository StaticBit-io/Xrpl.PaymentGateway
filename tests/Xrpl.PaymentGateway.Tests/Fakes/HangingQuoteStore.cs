using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// An <see cref="IQuoteStore"/> whose read, write and enqueue calls never complete on their own, for
/// proving that every path bounds it with <see cref="QuoteOptions.StoreTimeout"/> rather than blocking
/// indefinitely — the collector's own <see cref="GetQuoteAsync"/>/<see cref="SaveQuoteAsync"/>, and
/// <c>ValuationEnqueuer</c>'s <see cref="TryEnqueueValuationAsync"/>.
/// </summary>
public sealed class HangingQuoteStore : IQuoteStore
{
    public async Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

    public async Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredQuote>>(Array.Empty<StoredQuote>());

    public async Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task MarkValuationAttemptedAsync(string transactionHash, DateTimeOffset attemptedAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
        Task.FromResult<PaymentValuation?>(null);
}
