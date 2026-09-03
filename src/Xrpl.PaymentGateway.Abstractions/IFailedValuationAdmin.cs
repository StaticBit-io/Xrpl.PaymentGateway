namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// The operator path for a valuation the automatic pipeline gave up on.
/// </summary>
/// <remarks>
/// <para>
/// This library has no UI and never will; a host draws whatever admin screen it wants and calls through
/// here. The two write operations both act on an entry in <see cref="ValuationState.Failed"/> and both
/// leave it undelivered, exactly as an automatic valuation does the moment it is computed: the same
/// <c>ValuationWorker</c> delivery pass that retries a stuck automatic delivery picks these up on its next
/// poll and hands them to <see cref="IPaymentValuedHandler"/>, so crediting a manually priced or written-off
/// payment runs through the one code path the host already trusts, rather than a second one built for this.
/// </para>
/// </remarks>
public interface IFailedValuationAdmin
{
    /// <summary>
    /// Failed valuations — <see cref="ValuationState.Failed"/> only, not <see cref="ValuationState.WrittenOff"/>
    /// — for an operator to page through and act on.
    /// </summary>
    /// <param name="limit">Page size.</param>
    /// <param name="offset">How many failed entries, oldest-failed first, to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FailedValuationPage> ListFailedAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>
    /// Prices a failed entry manually: the recorded amount at <paramref name="rate"/>, moved to
    /// <see cref="ValuationState.ValuedManually"/> and left for the normal delivery pass to hand to
    /// <see cref="IPaymentValuedHandler"/>.
    /// </summary>
    /// <param name="transactionHash">The failed entry to price.</param>
    /// <param name="rate">Quote-asset units per unit of the received asset. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No entry exists for <paramref name="transactionHash"/>, or it is not currently
    /// <see cref="ValuationState.Failed"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is zero or negative.</exception>
    Task ValueManuallyAsync(string transactionHash, decimal rate, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a failed entry off: no quote amount, moved to <see cref="ValuationState.WrittenOff"/> and left
    /// for the normal delivery pass to tell <see cref="IPaymentValuedHandler"/> the case is closed.
    /// </summary>
    /// <param name="transactionHash">The failed entry to write off.</param>
    /// <param name="reason">Why — dust, a spam token, a mistaken transfer. Kept for the record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No entry exists for <paramref name="transactionHash"/>, or it is not currently
    /// <see cref="ValuationState.Failed"/> — writing off something that priced normally is a mistake, not a
    /// workflow.
    /// </exception>
    Task WriteOffAsync(string transactionHash, string reason, CancellationToken cancellationToken);
}

/// <summary>One page of failed valuations.</summary>
public sealed class FailedValuationPage
{
    /// <summary>The entries in this page.</summary>
    public required IReadOnlyList<PaymentValuation> Items { get; init; }

    /// <summary>Total failed entries, not just those in this page — what an admin screen paginates against.</summary>
    public required int TotalCount { get; init; }
}
