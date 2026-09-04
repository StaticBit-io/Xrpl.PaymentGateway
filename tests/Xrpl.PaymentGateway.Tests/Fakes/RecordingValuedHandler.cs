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

    /// <summary>
    /// When set, awaited after recording a delivery but before returning — lets a test hold a call in
    /// flight deterministically (an operator resolving the entry while this call has not yet completed,
    /// say) rather than racing a fixed delay against the worker's own pass.
    /// </summary>
    public Func<Task>? BeforeReturning { get; set; }

    public async Task OnPaymentValuedAsync(
        PaymentValuation valuation,
        string? buyerId,
        CancellationToken cancellationToken)
    {
        lock (_deliveries)
        {
            _deliveries.Add((valuation, buyerId));
        }

        if (BeforeReturning is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        if (Throws)
        {
            throw new InvalidOperationException("handler blew up");
        }
    }
}
