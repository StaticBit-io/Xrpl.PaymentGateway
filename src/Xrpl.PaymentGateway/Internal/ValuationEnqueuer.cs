using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Puts a recorded payment in line to be priced.
/// </summary>
/// <remarks>
/// Never throws. Called after the receipt handler has had its chance — first on the live and catch-up
/// path, once <c>DeliverAsync</c> has been attempted and only for a payment just recorded as new, and
/// again unconditionally from reconciliation's sweep, which re-offers every payment it re-reads regardless
/// of newness because it is the recovery path for anything the live/catch-up call below lost.
/// <para>
/// This call is bounded by <see cref="QuoteOptions.StoreTimeout"/> so a hanging quote store cannot stall
/// indefinitely, but the bound is not zero cost: on the live and catch-up path it is still awaited by
/// <c>XrplPaymentMonitor.ProcessTransactionAsync</c>, the same per-transaction sink the catch-up loop
/// replays through, so a newly recorded payment can still cost up to <see cref="QuoteOptions.StoreTimeout"/>
/// there. It is never on the path that records money — <c>PaymentDispatcher.RecordAsync</c> — so it never
/// blocks a payment from being stored or delivered, and a replayed payment (already in the store) reaches
/// neither this call nor delivery at all, which is what keeps a long catch-up window cheap in the common
/// case.
/// </para>
/// </remarks>
internal sealed class ValuationEnqueuer
{
    private readonly QuoteOptions _options;
    private readonly IQuoteStore _store;
    private readonly QuoteRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public ValuationEnqueuer(
        IOptions<QuoteOptions> options,
        IQuoteStore store,
        QuoteRegistry registry,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _options = options.Value;
        _store = store;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task EnqueueAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        QuotePair? pair;
        try
        {
            pair = _registry.FindPair(record.Currency, record.Issuer);
        }
        catch (ArgumentException ex)
        {
            // A currency code the canonicalizer cannot parse. Nothing to price it against.
            _logger.LogWarning(ex, "payment {Hash} carries an unreadable currency code", record.TransactionHash);
            return;
        }

        if (pair is null)
        {
            // No pair configured for this asset. Not an error: most hosts quote only some of what they take.
            return;
        }

        try
        {
            // CancelAfter has no TimeProvider overload; the constructor does. This is what lets an
            // injected clock govern the timeout in tests while still honouring the caller's token. A
            // timeout surfaces here as an OperationCanceledException whose token is not the caller's,
            // which the catch below treats exactly like any other store failure.
            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await _store.TryEnqueueValuationAsync(
                new PaymentValuation
                {
                    TransactionHash = record.TransactionHash,
                    PairKey = pair.Key,
                    Amount = record.Value,
                    PaymentLedgerIndex = record.LedgerIndex,
                    DestinationTag = record.DestinationTag,
                    EnqueuedAt = _timeProvider.GetUtcNow(),
                },
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Covers both an outright store failure and the store timeout above. Either way the payment
            // is already recorded and its receipt already announced, so nothing here costs the payment
            // itself — only its price. Nothing in this library re-offers it on its own: the host must call
            // IPaymentMonitorHealth.ReconcileAsync, and even then only ledgers still inside its configured
            // ReconcileWindow are re-read. An outage that outlasts that window loses the valuation for good.
            _logger.LogError(
                ex,
                "queueing payment {Hash} for valuation failed; it is lost unless the host runs reconciliation, and only within its ReconcileWindow",
                record.TransactionHash);
        }
    }
}
