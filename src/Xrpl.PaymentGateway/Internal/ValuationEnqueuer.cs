using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Puts a recorded payment in line to be priced.
/// </summary>
/// <remarks>
/// Never throws. This runs on the path that records money, and an optional feature has no business
/// stopping it: a quote store that is down costs a valuation, which reconciliation re-offers, whereas a
/// thrown exception here would cost the ledger cursor its progress. The store write is additionally bounded
/// by <see cref="QuoteOptions.EnqueueTimeout"/>: a store that merely hangs, rather than throwing outright,
/// must not be allowed to stall this path indefinitely either.
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
                new CancellationTokenSource(_options.EnqueueTimeout, _timeProvider);
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
            // Covers both an outright store failure and the enqueue timeout above. Either way the payment
            // is already recorded; reconciliation re-offers every payment in its window, so a miss here
            // heals itself.
            _logger.LogError(
                ex,
                "queueing payment {Hash} for valuation failed; reconciliation will offer it again",
                record.TransactionHash);
        }
    }
}
