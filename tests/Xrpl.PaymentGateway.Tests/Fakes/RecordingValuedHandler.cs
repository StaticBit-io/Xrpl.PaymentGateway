using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Captures every valuation delivery, and can be told to throw.</summary>
public sealed class RecordingValuedHandler : IPaymentValuedHandler
{
    private readonly List<(PaymentValuation Valuation, string? BuyerId)> _deliveries =
        new List<(PaymentValuation, string?)>();

    public IReadOnlyList<(PaymentValuation Valuation, string? BuyerId)> Deliveries
    {
        get
        {
            lock (_deliveries)
            {
                return _deliveries.ToList();
            }
        }
    }

    public bool Throws { get; set; }

    public Task OnPaymentValuedAsync(
        PaymentValuation valuation,
        string? buyerId,
        CancellationToken cancellationToken)
    {
        lock (_deliveries)
        {
            _deliveries.Add((valuation, buyerId));
        }

        return Throws ? Task.FromException(new InvalidOperationException("handler blew up")) : Task.CompletedTask;
    }
}
