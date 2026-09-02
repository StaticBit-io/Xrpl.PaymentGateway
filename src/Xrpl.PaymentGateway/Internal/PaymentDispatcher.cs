using Microsoft.Extensions.Logging;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The only component that talks to both the store and the host handler.
/// <see cref="RecordAsync"/> throws on store failures so the caller can retry them.
/// <see cref="DeliverAsync"/> never throws: a broken handler must not stop the ledger being followed.
/// </summary>
internal sealed class PaymentDispatcher
{
    private readonly IPaymentStore _store;
    private readonly IPaymentReceivedHandler _handler;
    private readonly ILogger _logger;
    private readonly ValuationEnqueuer? _valuationEnqueuer;

    public PaymentDispatcher(
        IPaymentStore store,
        IPaymentReceivedHandler handler,
        ILogger logger,
        ValuationEnqueuer? valuationEnqueuer = null)
    {
        _store = store;
        _handler = handler;
        _logger = logger;
        _valuationEnqueuer = valuationEnqueuer;
    }

    /// <summary>Persists the record. Returns false when the hash was already stored.</summary>
    public async Task<bool> RecordAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        bool added = await _store.TryAddPaymentAsync(record, cancellationToken).ConfigureAwait(false);

        if (_valuationEnqueuer is not null)
        {
            // Offered even when the payment was already stored. Reconciliation replays a window of
            // ledgers precisely so a valuation that never got queued can still be picked up, and the
            // quote store rejects the duplicate itself.
            await _valuationEnqueuer.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

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
