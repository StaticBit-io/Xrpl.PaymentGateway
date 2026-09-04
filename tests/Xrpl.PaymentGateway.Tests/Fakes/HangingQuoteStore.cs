using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// An <see cref="IQuoteStore"/> whose read, write and enqueue calls never complete on their own, for
/// proving that every path bounds it with <see cref="QuoteOptions.StoreTimeout"/> rather than blocking
/// indefinitely — the collector's own <see cref="GetQuoteAsync"/>/<see cref="SaveQuoteAsync"/>,
/// <c>ValuationEnqueuer</c>'s <see cref="TryEnqueueValuationAsync"/>, and <c>QuoteHealth.CheckAsync</c>'s
/// own <see cref="GetQuotesAsync"/> — the first store call it makes.
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

    public async Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return Array.Empty<StoredQuote>();
    }

    public async Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(
        string pairKey, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task<IReadOnlyList<PendingValuationsByPair>> GetPendingValuationBreakdownAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingValuationsByPair>>(Array.Empty<PendingValuationsByPair>());

    public Task<bool> SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task SaveValuationFailureAsync(
        string transactionHash, string reason, DateTimeOffset failedAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> SaveWriteOffAsync(
        string transactionHash, string reason, DateTimeOffset writtenOffAt, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<PaymentValuation>> GetFailedValuationsAsync(
        int limit, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task<int> CountFailedValuationsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<PaymentValuation>> GetUnresolvedValuationsAsync(
        DateTimeOffset olderThan, int limit, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task<int> CountUnresolvedValuationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentValuation>>(Array.Empty<PaymentValuation>());

    public Task<bool> MarkValuationDeliveredAsync(
        string transactionHash, ValuationState deliveredState, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
        Task.FromResult<PaymentValuation?>(null);
}
