using Microsoft.Extensions.Logging;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The only component that talks to both the store and the host handler.
/// <see cref="RecordAsync"/> throws on store failures so the caller can retry them.
/// <see cref="DeliverAsync"/> never throws: a broken handler must not stop the ledger being followed.
/// </summary>
/// <remarks>
/// Deliberately does not know about <c>ValuationEnqueuer</c>. Queueing a valuation belongs after the
/// receipt handler has been given its chance, so callers enqueue it explicitly, after
/// <see cref="DeliverAsync"/>, and only for a payment <see cref="RecordAsync"/> just accepted as new — a
/// replay costs neither call. That placement fixes the ordering, not the cost: the caller's method is
/// still the per-transaction sink the catch-up loop awaits, so a quote store that hangs still costs a
/// newly recorded payment during catch-up either way — see the remarks on <c>XrplPaymentMonitor
/// .ProcessTransactionAsync</c> for the full accounting.
/// </remarks>
internal sealed class PaymentDispatcher
{
    private readonly IPaymentStore _store;
    private readonly IPaymentReceivedHandler _handler;
    private readonly ILogger _logger;

    public PaymentDispatcher(IPaymentStore store, IPaymentReceivedHandler handler, ILogger logger)
    {
        _store = store;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Persists the record. Returns false when the hash was already stored.</summary>
    public Task<bool> RecordAsync(PaymentRecord record, CancellationToken cancellationToken) =>
        _store.TryAddPaymentAsync(record, cancellationToken);

    /// <summary>
    /// Resolves the buyer, hands the payment to the host, and marks it handled on success.
    /// Returns true only when the record actually reached the handler and was marked handled.
    /// </summary>
    public async Task<bool> DeliverAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        string? buyerId = null;

        try
        {
            if (record.DestinationTag is { } tag)
            {
                buyerId = await _store.FindBuyerByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            }

            await _handler.OnPaymentReceivedAsync(record, buyerId, cancellationToken).ConfigureAwait(false);
            await _store.MarkHandledAsync(record.TransactionHash, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "payment {Hash} of {Value} {Currency} from {Sender} delivered (buyer {Buyer})",
                record.TransactionHash,
                record.Value,
                record.Currency,
                record.Sender,
                buyerId ?? "unknown");

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "delivering payment {Hash} failed; it stays unhandled and reconciliation will retry it",
                record.TransactionHash);

            return false;
        }
    }
}
