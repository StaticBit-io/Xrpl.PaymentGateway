using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Prices queued payments and delivers the results.
/// </summary>
/// <remarks>
/// Runs behind the payment path, never on it. A payment that cannot be priced — no snapshot yet, a stale
/// one, an evaluation that threw — simply stays queued: the money is already recorded and its receipt
/// already announced, so waiting costs nothing but a later number.
/// </remarks>
internal sealed class ValuationWorker : BackgroundService
{
    private readonly QuoteOptions _options;
    private readonly IQuoteStore _quotes;
    private readonly IPaymentStore _payments;
    private readonly QuoteRegistry _registry;
    private readonly IQuoteSource _source;
    private readonly IPaymentValuedHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValuationWorker> _logger;

    public ValuationWorker(
        IOptions<QuoteOptions> options,
        IQuoteStore quotes,
        IPaymentStore payments,
        QuoteRegistry registry,
        IQuoteSource source,
        IPaymentValuedHandler handler,
        TimeProvider timeProvider,
        ILogger<ValuationWorker> logger)
    {
        _options = options.Value;
        _quotes = quotes;
        _payments = payments;
        _registry = registry;
        _source = source;
        _handler = handler;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ValuePendingAsync(stoppingToken).ConfigureAwait(false);
                await DeliverValuedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad pass must not end the loop; the queue is durable and the next pass retries.
                _logger.LogError(ex, "a valuation pass failed");
            }

            try
            {
                await Task.Delay(_options.ValuationPollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ValuePendingAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<PaymentValuation> pending = await _quotes
            .GetPendingValuationsAsync(_options.ValuationBatchSize, stoppingToken)
            .ConfigureAwait(false);

        foreach (PaymentValuation entry in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();

            QuotePair? pair = _registry.Pairs
                .FirstOrDefault(p => string.Equals(p.Key, entry.PairKey, StringComparison.Ordinal));
            if (pair is null)
            {
                // The pair was removed from configuration after the payment was queued. Leaving it queued
                // keeps the record: put the pair back and it prices, drop the row and it is gone for good.
                continue;
            }

            IQuoteSnapshot? snapshot = await SnapshotForAsync(pair, stoppingToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            if (_timeProvider.GetUtcNow() - snapshot.CapturedAt > _options.EffectiveMaxQuoteAge
                && _options.RefuseStaleQuotes)
            {
                continue;
            }

            QuoteResult? result;
            try
            {
                result = await snapshot
                    .EvaluateAsync(entry.Amount, QuoteDirection.ExactInput, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "pricing payment {Hash} failed; it stays queued", entry.TransactionHash);
                continue;
            }

            if (result is null)
            {
                continue;
            }

            await _quotes.SaveValuationAsync(Complete(entry, result, snapshot), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<IQuoteSnapshot?> SnapshotForAsync(QuotePair pair, CancellationToken stoppingToken)
    {
        if (!_options.ValuateWithFreshSnapshot)
        {
            return _registry.GetSnapshot(pair.Key);
        }

        try
        {
            // CancelAfter has no TimeProvider overload; the constructor does. This is what lets an
            // injected clock govern the timeout in tests while still honouring stoppingToken.
            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(_options.CaptureTimeout, _timeProvider);
            using CancellationTokenSource capture =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
            return await _source.CaptureAsync(pair, capture.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "capturing a fresh snapshot for {Pair} failed", pair.Key);
            return null;
        }
    }

    private async Task DeliverValuedAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<PaymentValuation> undelivered = await _quotes
            .GetUndeliveredValuationsAsync(_options.ValuationBatchSize, stoppingToken)
            .ConfigureAwait(false);

        foreach (PaymentValuation entry in undelivered)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                string? buyerId = entry.DestinationTag is { } tag
                    ? await _payments.FindBuyerByTagAsync(tag, stoppingToken).ConfigureAwait(false)
                    : null;

                await _handler.OnPaymentValuedAsync(entry, buyerId, stoppingToken).ConfigureAwait(false);
                await _quotes.MarkValuationDeliveredAsync(entry.TransactionHash, stoppingToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "payment {Hash} valued at {Quote} (buyer {Buyer})",
                    entry.TransactionHash,
                    entry.QuoteAmount,
                    buyerId ?? "unknown");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "delivering the valuation of {Hash} failed; it stays undelivered and will be retried",
                    entry.TransactionHash);
            }
        }
    }

    private PaymentValuation Complete(PaymentValuation entry, QuoteResult result, IQuoteSnapshot snapshot) =>
        new PaymentValuation
        {
            TransactionHash = entry.TransactionHash,
            PairKey = entry.PairKey,
            Amount = entry.Amount,
            PaymentLedgerIndex = entry.PaymentLedgerIndex,
            DestinationTag = entry.DestinationTag,
            EnqueuedAt = entry.EnqueuedAt,
            ValuedAt = _timeProvider.GetUtcNow(),
            QuoteAmount = result.OutputAmount,
            EffectivePrice = result.EffectivePrice,
            MarginalPrice = result.MarginalPrice,
            SlippagePercent = result.SlippagePercent,
            FullyFilled = result.IsFullyFilled,
            BookTruncated = result.BookTruncated,
            Route = result.Route,
            SnapshotLedgerIndex = snapshot.LedgerIndex,
            SnapshotCapturedAt = snapshot.CapturedAt,
            Delivered = false,
        };
}
