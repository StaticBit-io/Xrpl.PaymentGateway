using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Keeps every configured pair's liquidity snapshot current.
/// </summary>
/// <remarks>
/// Separate from the payment monitor on purpose, and separately failable: the monitor answers for the
/// completeness of payment records and must not wait a millisecond on whether an order book replied.
/// </remarks>
internal sealed class QuoteCollector : BackgroundService
{
    private readonly QuoteOptions _options;
    private readonly IQuoteSource _source;
    private readonly IQuoteStore _store;
    private readonly QuoteRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuoteCollector> _logger;

    /// <summary>
    /// The last <see cref="StoredQuote"/> this collector itself wrote for a pair, kept so a store read
    /// failure has a fallback to build an honest failure row from instead of losing the previous row's
    /// price and failure count entirely. See <see cref="RefreshAsync"/>.
    /// </summary>
    private readonly Dictionary<string, StoredQuote> _lastWritten = new Dictionary<string, StoredQuote>(StringComparer.Ordinal);

    public QuoteCollector(
        IOptions<QuoteOptions> options,
        IQuoteSource source,
        IQuoteStore store,
        QuoteRegistry registry,
        TimeProvider timeProvider,
        ILogger<QuoteCollector> logger)
    {
        _options = options.Value;
        _source = source;
        _store = store;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The pause is taken after every pair, and PairDelay is already interval/N when the floor does
        // not bind. So a cycle equals the interval when it fits and stretches honestly when it does not,
        // and two cycles can never overlap.
        TimeSpan delay = QuoteSchedule.PairDelay(
            _registry.Pairs.Count, _options.RefreshInterval, _options.MinimumPairStagger);

        if (!QuoteSchedule.CycleFitsInInterval(
                _registry.Pairs.Count, _options.RefreshInterval, _options.MinimumPairStagger))
        {
            _logger.LogWarning(
                "{Pairs} pairs at {Delay} apart take longer than the {Interval} refresh interval; "
                + "quotes will refresh slower than configured",
                _registry.Pairs.Count,
                delay,
                _options.RefreshInterval);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (QuotePair pair in _registry.Pairs)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await RefreshAsync(pair, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // RefreshAsync rethrows only when shutdown, not the capture timeout, caused the
                    // cancellation. Left uncaught here that exception would escape ExecuteAsync itself
                    // and leave the BackgroundService's execute task in an unexpected state instead of
                    // completing normally, the way every other exit from this loop does.
                    return;
                }

                try
                {
                    await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshAsync(QuotePair pair, CancellationToken stoppingToken)
    {
        DateTimeOffset attemptedAt = _timeProvider.GetUtcNow();
        StoredQuote? previous;
        bool previousKnown;

        try
        {
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
            using CancellationTokenSource readTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            previous = await _store.GetQuoteAsync(pair.Key, readTimeout.Token).ConfigureAwait(false);
            previousKnown = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reading the previous row is a nicety; failing to read it must not skip the refresh. The
            // collector's own last write — see _lastWritten — stands in for it: a correct failure row,
            // carrying the right price and failure count, can be produced either way. On a cold start,
            // before this collector has ever written the pair, the cache has nothing either, and previous
            // state is genuinely unknown — not "empty", just unseen. Writing a null-filled failure row in
            // that case would overwrite a row that may still be sitting in the store, simply unread this
            // cycle, so RefreshAsync below skips the write entirely rather than guessing.
            if (_lastWritten.TryGetValue(pair.Key, out StoredQuote? cached))
            {
                previous = cached;
                previousKnown = true;
            }
            else
            {
                previous = null;
                previousKnown = false;
            }

            _logger.LogWarning(ex, "reading the stored quote for {Pair} failed; using the last written value", pair.Key);
        }

        try
        {
            // The constructor overload that takes a TimeProvider is what lets an injected clock govern
            // the timeout in tests instead of the wall clock, while still honouring stoppingToken.
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(_options.CaptureTimeout, _timeProvider);
            using CancellationTokenSource capture =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            IQuoteSnapshot? snapshot = await _source.CaptureAsync(pair, capture.Token).ConfigureAwait(false);

            _registry.SetSnapshot(pair.Key, snapshot);
            await WriteAsync(Success(pair, snapshot, attemptedAt), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately NOT clearing the cached snapshot: a node we could not reach tells us nothing
            // about whether the pair still has liquidity, and overwriting a working quote with nothing
            // would turn a network blip into a checkout outage.
            _logger.LogError(ex, "refreshing the quote for {Pair} failed; the last good reading is kept", pair.Key);

            if (!previousKnown)
            {
                // The store read failed and this collector has never written the pair itself: we have no
                // way to know whether the store currently holds a good row or nothing at all. Writing a
                // null-filled failure row here would blindly overwrite it if it does. Leave the store
                // alone; the next successful read or write will put us back on solid ground.
                _logger.LogWarning(
                    "skipping the failure write for {Pair}; the previously stored value is unknown", pair.Key);
                return;
            }

            await WriteAsync(Failure(pair, previous, attemptedAt, ex), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(StoredQuote quote, CancellationToken stoppingToken)
    {
        try
        {
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
            using CancellationTokenSource writeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            await _store.SaveQuoteAsync(quote, writeTimeout.Token).ConfigureAwait(false);

            // Recorded only once the write actually succeeded: a failure row that never reached the store
            // must not become the fallback the next read failure builds on top of.
            _lastWritten[quote.PairKey] = quote;
            _registry.SetLastWriteSucceeded(quote.PairKey, succeeded: true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The in-memory registry is already updated, so quoting keeps working; only the operator's
            // view of it is behind. Retrying here would stall the cycle for every other pair. Covers both
            // an outright store failure and the StoreTimeout above — a store that merely hangs must not
            // stall the pairs behind it either. Recorded on the registry too, per pair: otherwise a store
            // whose writes hang while its reads still answer would read as healthy forever, since the
            // freshness fields come from the in-memory snapshot and update every cycle regardless of
            // whether the write beneath them ever lands — and a single process-wide flag would let one
            // pair's next successful write erase another pair's still-failing one.
            _registry.SetLastWriteSucceeded(quote.PairKey, succeeded: false);
            _logger.LogError(ex, "persisting the quote for {Pair} failed", quote.PairKey);
        }
    }

    private static StoredQuote Success(QuotePair pair, IQuoteSnapshot? snapshot, DateTimeOffset attemptedAt) =>
        new StoredQuote
        {
            PairKey = pair.Key,
            Currency = pair.Currency,
            Issuer = pair.Issuer,
            QuoteCurrency = pair.QuoteCurrency,
            QuoteIssuer = pair.QuoteIssuer,
            MarginalPrice = snapshot?.MarginalPrice,
            LedgerIndex = snapshot?.LedgerIndex,
            CapturedAt = snapshot?.CapturedAt ?? attemptedAt,
            LastAttemptAt = attemptedAt,
            ConsecutiveFailures = 0,
            LastError = null,
        };

    private static StoredQuote Failure(
        QuotePair pair,
        StoredQuote? previous,
        DateTimeOffset attemptedAt,
        Exception error) =>
        new StoredQuote
        {
            PairKey = pair.Key,
            Currency = pair.Currency,
            Issuer = pair.Issuer,
            QuoteCurrency = pair.QuoteCurrency,
            QuoteIssuer = pair.QuoteIssuer,
            MarginalPrice = previous?.MarginalPrice,
            LedgerIndex = previous?.LedgerIndex,
            CapturedAt = previous?.CapturedAt,
            LastAttemptAt = attemptedAt,
            ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1,
            LastError = error.Message,
        };
}
