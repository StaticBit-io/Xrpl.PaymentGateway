using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Wraps a real quote store and fails SaveValuationAsync for one chosen hash.</summary>
public sealed class FlakyQuoteStore : IQuoteStore
{
    private readonly IQuoteStore _inner;
    private readonly string _failingHash;
    private int _remainingFailures;

    public FlakyQuoteStore(IQuoteStore inner, string failingHash, int failures = int.MaxValue)
    {
        _inner = inner;
        _failingHash = failingHash;
        _remainingFailures = failures;
    }

    public Task SaveQuoteAsync(StoredQuote quote, CancellationToken cancellationToken) =>
        _inner.SaveQuoteAsync(quote, cancellationToken);

    public Task<StoredQuote?> GetQuoteAsync(string pairKey, CancellationToken cancellationToken) =>
        _inner.GetQuoteAsync(pairKey, cancellationToken);

    public Task<IReadOnlyList<StoredQuote>> GetQuotesAsync(CancellationToken cancellationToken) =>
        _inner.GetQuotesAsync(cancellationToken);

    public Task<bool> TryEnqueueValuationAsync(PaymentValuation pending, CancellationToken cancellationToken) =>
        _inner.TryEnqueueValuationAsync(pending, cancellationToken);

    public Task<IReadOnlyList<PaymentValuation>> GetPendingValuationsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetPendingValuationsAsync(limit, cancellationToken);

    public Task SaveValuationAsync(PaymentValuation valuation, CancellationToken cancellationToken)
    {
        if (string.Equals(valuation.TransactionHash, _failingHash, StringComparison.Ordinal) && _remainingFailures > 0)
        {
            _remainingFailures--;
            return Task.FromException(new InvalidOperationException("quote store rejected the write"));
        }

        return _inner.SaveValuationAsync(valuation, cancellationToken);
    }

    public Task<IReadOnlyList<PaymentValuation>> GetUndeliveredValuationsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetUndeliveredValuationsAsync(limit, cancellationToken);

    public Task MarkValuationDeliveredAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.MarkValuationDeliveredAsync(transactionHash, cancellationToken);

    public Task<PaymentValuation?> GetValuationAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.GetValuationAsync(transactionHash, cancellationToken);
}
