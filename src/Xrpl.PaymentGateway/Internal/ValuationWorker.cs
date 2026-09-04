using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Prices queued payments and delivers the results.
/// </summary>
/// <remarks>
/// Runs behind the payment path, never on it. The pending queue is per pair: for each configured pair this
/// worker reads that pair's queued entries first and captures or looks up a snapshot for it only when there
/// is something queued to price, so a pair with nothing outstanding costs nothing here and a pair with no
/// usable snapshot costs only its own payments a delay, not the pairs behind it. A payment that cannot be
/// priced for a transient, pair-wide reason — no snapshot yet, a stale one, the snapshot answering "no
/// liquidity right now", or the store rejecting the write that would move it on — simply stays queued: the
/// money is already recorded and its receipt already announced, so waiting costs nothing but a later
/// number. A payment that cannot be priced for a per-entry, non-transient reason — its pair is gone, or
/// pricing it threw — is moved to <see cref="ValuationState.Failed"/> instead of retried forever: see
/// <see cref="ValuePendingAsync"/> and <see cref="IUnresolvedValuationAdmin"/>, the operator path such an
/// entry then waits on alongside one still stuck <see cref="ValuationState.Pending"/>.
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
        foreach (QuotePair pair in _registry.Pairs)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ValuePendingForPairAsync(pair, stoppingToken).ConfigureAwait(false);
        }

        // GetPendingValuationsAsync only ever asks about a currently configured pair, so an entry whose
        // pair was removed from configuration after it was queued would otherwise never be looked at
        // again — not priced, not failed, just stuck. The breakdown is the one place a pair key with
        // pending work surfaces regardless of configuration, which is what makes that entry reachable.
        IReadOnlyList<PendingValuationsByPair> breakdown = await _quotes
            .GetPendingValuationBreakdownAsync(stoppingToken)
            .ConfigureAwait(false);

        foreach (PendingValuationsByPair bucket in breakdown)
        {
            stoppingToken.ThrowIfCancellationRequested();

            bool stillConfigured = _registry.Pairs
                .Any(p => string.Equals(p.Key, bucket.PairKey, StringComparison.Ordinal));
            if (stillConfigured)
            {
                continue;
            }

            await FailOrphanedPairAsync(bucket.PairKey, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prices one configured pair's pending entries. Reads the pending queue first: with nothing queued
    /// there is nothing to price, and neither a cached snapshot nor a fresh capture is worth spending on
    /// it — a pair that sits idle between payments must not cost a capture every poll regardless.
    /// </summary>
    private async Task ValuePendingForPairAsync(QuotePair pair, CancellationToken stoppingToken)
    {
        IReadOnlyList<PaymentValuation> pending = await _quotes
            .GetPendingValuationsAsync(pair.Key, _options.ValuationBatchSize, stoppingToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return;
        }

        if (_options.ValuateWithFreshSnapshot)
        {
            // The documented cost of this option is one capture per payment, not one per pair per pass:
            // pricing the whole batch off a single capture would also have every entry in it record the
            // same snapshot ledger and time, which is not "each payment gets its own capture".
            foreach (PaymentValuation entry in pending)
            {
                stoppingToken.ThrowIfCancellationRequested();

                IQuoteSnapshot? fresh = await CaptureFreshAsync(pair, stoppingToken).ConfigureAwait(false);
                if (fresh is null)
                {
                    // Transient: this attempt captured nothing. The entry stays queued and is tried
                    // again — with its own fresh capture — next pass.
                    continue;
                }

                await PriceAsync(entry, fresh, stoppingToken).ConfigureAwait(false);
            }

            return;
        }

        IQuoteSnapshot? cached = _registry.GetSnapshot(pair.Key);
        if (cached is null)
        {
            // Transient and shared by every entry against this pair: nothing has been captured for it
            // yet, or the last capture failed. Every entry simply stays queued and prices itself once a
            // snapshot exists — nothing to record here.
            return;
        }

        if (_timeProvider.GetUtcNow() - cached.CapturedAt > _options.EffectiveMaxQuoteAge
            && _options.RefuseStaleQuotes)
        {
            // Transient for the same reason: the next successful refresh clears this on its own.
            return;
        }

        foreach (PaymentValuation entry in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await PriceAsync(entry, cached, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Prices one entry against an already-current snapshot for its pair.</summary>
    private async Task PriceAsync(PaymentValuation entry, IQuoteSnapshot snapshot, CancellationToken stoppingToken)
    {
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
            return;
        }

        if (result is null)
        {
            // The snapshot cannot trade this amount right now — the next capture may price it fine.
            // Transient and shared by nothing else about this entry, so it simply stays queued rather than
            // being parked in the operator queue over what may be a passing condition.
            return;
        }

        bool applied;
        try
        {
            applied = await _quotes.SaveValuationAsync(Complete(entry, result, snapshot), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A store write failure is the archetypal transient error — a timeout, a dropped connection, a
            // deadlock — not evidence this row can never be saved. The entry stays Pending and is priced
            // again next pass; nothing here parks a correctly priced payment in the operator queue over a
            // momentary store blip.
            _logger.LogError(ex, "saving the valuation of {Hash} failed; it stays pending and is retried", entry.TransactionHash);
            return;
        }

        if (!applied)
        {
            // The row moved on from Pending between being read here and this write reaching the store —
            // an operator resolved it (writing it off, say) while this pass was pricing it. The freshly
            // computed price is simply discarded; the operator's resolution is the fact that stands.
            _logger.LogInformation(
                "payment {Hash} was resolved by an operator before its automatic valuation could be saved; "
                + "the operator's resolution stands",
                entry.TransactionHash);
        }
    }

    /// <summary>
    /// Fails every pending entry against a pair key the registry no longer configures — the one per-entry,
    /// non-transient cause this worker can reach without ever asking the store about an unconfigured pair
    /// on the pricing path itself.
    /// </summary>
    private async Task FailOrphanedPairAsync(string pairKey, CancellationToken stoppingToken)
    {
        IReadOnlyList<PaymentValuation> orphaned = await _quotes
            .GetPendingValuationsAsync(pairKey, _options.ValuationBatchSize, stoppingToken)
            .ConfigureAwait(false);

        foreach (PaymentValuation entry in orphaned)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Per-entry, and nothing will re-add a removed pair behind this worker's back — terminal,
            // not transient.
            await FailAsync(
                    entry.TransactionHash,
                    $"pair \"{pairKey}\" is no longer configured",
                    stoppingToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Captures one fresh snapshot for <paramref name="pair"/>, bounded by <see cref="QuoteOptions.CaptureTimeout"/>.
    /// Called once per pending entry when <see cref="QuoteOptions.ValuateWithFreshSnapshot"/> is set, never
    /// once for a whole pending batch — see <see cref="ValuePendingForPairAsync"/>.
    /// </summary>
    private async Task<IQuoteSnapshot?> CaptureFreshAsync(QuotePair pair, CancellationToken stoppingToken)
    {
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

                // Conditional on the row still being in the state actually handed to the handler above: if
                // an operator resolved this entry (pricing it manually or writing it off) while the handler
                // call was in flight, the mark must not land on that newer row — see
                // IQuoteStore.MarkValuationDeliveredAsync. Left undelivered here, the resolved content is
                // what the very next pass hands to the handler instead.
                bool applied = await _quotes
                    .MarkValuationDeliveredAsync(entry.TransactionHash, entry.State, stoppingToken)
                    .ConfigureAwait(false);

                if (applied)
                {
                    _logger.LogInformation(
                        "payment {Hash} reached {State} (quote {Quote}, buyer {Buyer})",
                        entry.TransactionHash,
                        entry.State,
                        entry.QuoteAmount,
                        buyerId ?? "unknown");
                }
                else
                {
                    // The mark did not apply — the handler was handed content that has since been
                    // superseded. Not a failure: the resolved row is what the very next pass delivers.
                    _logger.LogInformation(
                        "payment {Hash} moved on from {State} before the delivered mark could apply; "
                        + "the newer content will be redelivered next pass",
                        entry.TransactionHash,
                        entry.State);
                }
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
