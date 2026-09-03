using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Prices queued payments and delivers the results.
/// </summary>
/// <remarks>
/// Runs behind the payment path, never on it. A payment that cannot be priced for a transient, pair-wide
/// reason — no snapshot yet, or a stale one — simply stays queued: the money is already recorded and its
/// receipt already announced, so waiting costs nothing but a later number. A payment that cannot be priced
/// for a per-entry, non-transient reason — its pair is gone, pricing it threw, the pair has no liquidity,
/// or the store rejected the row — is moved to <see cref="ValuationState.Failed"/> instead of retried
/// forever: see <see cref="ValuePendingAsync"/> and <see cref="IFailedValuationAdmin"/>, the operator path
/// a failed entry then waits on.
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
                // Per-entry, and nothing will re-add a removed pair behind this worker's back — terminal,
                // not transient.
                await FailAsync(
                        entry.TransactionHash,
                        $"pair \"{entry.PairKey}\" is no longer configured",
                        stoppingToken)
                    .ConfigureAwait(false);
                continue;
            }

            IQuoteSnapshot? snapshot = await SnapshotForAsync(pair, stoppingToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                // Transient and shared by every entry against this pair: nothing has been captured for it
                // yet, or the last capture failed. The entry simply stays queued and prices itself once a
                // snapshot exists — nothing to record here.
                continue;
            }

            if (_timeProvider.GetUtcNow() - snapshot.CapturedAt > _options.EffectiveMaxQuoteAge
                && _options.RefuseStaleQuotes)
            {
                // Transient for the same reason: the next successful refresh clears this on its own.
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
                // Per-entry and deterministic: the same amount against the same snapshot throws the same
                // way every time, so leaving it queued would only spend the next pass reproducing this one.
                // Terminal.
                _logger.LogError(ex, "pricing payment {Hash} failed", entry.TransactionHash);
                await FailAsync(entry.TransactionHash, $"pricing threw: {ex.Message}", stoppingToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (result is null)
            {
                // The pair currently has no liquidity to trade this amount against. Terminal: nothing
                // retries this on a timer, and an operator — see IFailedValuationAdmin — is what moves it
                // on from here, whether that means pricing it manually once liquidity is known some other
                // way, or writing it off.
                await FailAsync(entry.TransactionHash, "the pair currently has no liquidity", stoppingToken)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                await _quotes.SaveValuationAsync(Complete(entry, result, snapshot), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same failure class the evaluation catch above guards against, one line later: a store
                // that rejects this particular row (a decimal a column cannot hold, say) will reject it
                // again next pass too. Terminal.
                _logger.LogError(ex, "saving the valuation of {Hash} failed", entry.TransactionHash);
                await FailAsync(
                        entry.TransactionHash, $"the store rejected the valuation: {ex.Message}", stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Moves a pending entry to <see cref="ValuationState.Failed"/> for a per-entry, non-transient cause.
    /// A failure here — the store rejecting the failure write itself — is logged and swallowed like any
    /// other store hiccup on this path: the entry simply stays queued and gets another chance next pass.
    /// </summary>
    private async Task FailAsync(string transactionHash, string reason, CancellationToken stoppingToken)
    {
        try
        {
            await _quotes.SaveValuationFailureAsync(transactionHash, reason, _timeProvider.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "recording the failure of {Hash} failed; it stays queued", transactionHash);
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
                    "payment {Hash} reached {State} (quote {Quote}, buyer {Buyer})",
                    entry.TransactionHash,
                    entry.State,
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
            State = ValuationState.Valued,
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
