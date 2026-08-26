using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Captures every delivery, and can be told to throw.</summary>
public sealed class RecordingHandler : IPaymentReceivedHandler
{
    private readonly List<(PaymentRecord Payment, string? BuyerId)> _deliveries = new List<(PaymentRecord, string?)>();

    public IReadOnlyList<(PaymentRecord Payment, string? BuyerId)> Deliveries
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

    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken)
    {
        lock (_deliveries)
        {
            _deliveries.Add((payment, buyerId));
        }

        return Throws ? Task.FromException(new InvalidOperationException("handler blew up")) : Task.CompletedTask;
    }
}
