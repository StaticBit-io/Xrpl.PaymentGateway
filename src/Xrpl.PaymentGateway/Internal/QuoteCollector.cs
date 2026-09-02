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

                await RefreshAsync(pair, stoppingToken).ConfigureAwait(false);

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
        StoredQuote? previous = null;

        try
        {
            previous = await _store.GetQuoteAsync(pair.Key, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reading the previous row is a nicety; failing to read it must not skip the refresh.
            _logger.LogWarning(ex, "reading the stored quote for {Pair} failed", pair.Key);
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
            await WriteAsync(Failure(pair, previous, attemptedAt, ex), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(StoredQuote quote, CancellationToken stoppingToken)
    {
        try
        {
            await _store.SaveQuoteAsync(quote, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The in-memory registry is already updated, so quoting keeps working; only the operator's
            // view of it is behind. Retrying here would stall the cycle for every other pair.
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
