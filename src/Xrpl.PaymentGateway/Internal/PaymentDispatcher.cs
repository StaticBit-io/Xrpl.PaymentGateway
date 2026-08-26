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

    public PaymentDispatcher(IPaymentStore store, IPaymentReceivedHandler handler, ILogger logger)
    {
        _store = store;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Persists the record. Returns false when the hash was already stored.</summary>
    public Task<bool> RecordAsync(PaymentRecord record, CancellationToken cancellationToken) =>
        _store.TryAddPaymentAsync(record, cancellationToken);

    /// <summary>Resolves the buyer, hands the payment to the host, and marks it handled on success.</summary>
    public async Task DeliverAsync(PaymentRecord record, CancellationToken cancellationToken)
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
        }
    }
}
