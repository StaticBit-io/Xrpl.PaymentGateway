using System.Collections.Concurrent;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Stands in for whatever a real host does once a payment's value is known: credit a balance, release an
/// order. In the same spirit as <see cref="SamplePaymentHandler"/>, it only logs and remembers, so the
/// sample stays inspectable over HTTP.
/// </summary>
public sealed class SampleValuedHandler : IPaymentValuedHandler
{
    private readonly ConcurrentQueue<ValuedPayment> _valued = new ConcurrentQueue<ValuedPayment>();
    private readonly ILogger<SampleValuedHandler> _logger;

    public SampleValuedHandler(ILogger<SampleValuedHandler> logger) => _logger = logger;

    public IReadOnlyCollection<ValuedPayment> Valued => _valued.ToArray();

    public Task OnPaymentValuedAsync(PaymentValuation valuation, string? buyerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "payment {Hash} reached {State} (quote {Quote}, buyer {Buyer})",
            valuation.TransactionHash,
            valuation.State,
            valuation.QuoteAmount,
            buyerId ?? "unknown");

        // Same value, automatically priced or an operator's own — both flow through this one handler, so
        // the page shows a resolved payment landing exactly the way an automatic valuation does.
        // Note: The readable quote currency code is resolved on the API endpoint using the pair configuration.
        _valued.Enqueue(new ValuedPayment(
            valuation.TransactionHash,
            buyerId,
            valuation.PairKey,
            null,
            valuation.State,
            valuation.Amount,
            valuation.QuoteAmount,
            valuation.EffectivePrice,
            valuation.FailureReason,
            valuation.WriteOffReason,
            valuation.ValuedAt ?? valuation.FailedAt ?? valuation.WrittenOffAt));

        return Task.CompletedTask;
    }
}

/// <summary>What the sample shows for a payment's valuation, whichever state it landed in.</summary>
public sealed record ValuedPayment(
    string TransactionHash,
    string? BuyerId,
    string PairKey,
    string? QuoteCurrency,
    ValuationState State,
    decimal Amount,
    decimal? QuoteAmount,
    decimal? EffectivePrice,
    string? FailureReason,
    string? WriteOffReason,
    DateTimeOffset? ResolvedAt);
