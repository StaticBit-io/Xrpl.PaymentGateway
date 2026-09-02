using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>
/// Reads the collector's state for a scheduler or a health endpoint.
/// </summary>
/// <remarks>
/// Internal because its constructor takes <see cref="QuoteRegistry"/>, which is internal, and a public
/// constructor may not expose a less accessible type. Hosts reach it through <see cref="IQuoteHealth"/>.
/// </remarks>
internal sealed class QuoteHealth : IQuoteHealth
{
    private readonly QuoteOptions _options;
    private readonly IQuoteStore _store;
    private readonly QuoteRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuoteHealth> _logger;

    public QuoteHealth(
        IOptions<QuoteOptions> options,
        IQuoteStore store,
        QuoteRegistry registry,
        TimeProvider timeProvider,
        ILogger<QuoteHealth> logger)
    {
        _options = options.Value;
        _store = store;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<QuoteHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        int fresh = 0;
        TimeSpan? oldestQuoteAge = null;
        foreach (QuotePair pair in _registry.Pairs)
        {
            IQuoteSnapshot? snapshot = _registry.GetSnapshot(pair.Key);
            if (snapshot is null)
            {
                continue;
            }

            TimeSpan age = now - snapshot.CapturedAt;
            if (age <= _options.EffectiveMaxQuoteAge)
            {
                fresh++;
            }

            if (oldestQuoteAge is null || age > oldestQuoteAge)
            {
                oldestQuoteAge = age;
            }
        }

        int failing = 0;
        int worstStreak = 0;
        string? lastError = null;
        int pending = 0;
        TimeSpan? oldestPendingAge = null;
        int undelivered = 0;
        bool storeReadable = true;

        try
        {
            foreach (StoredQuote quote in await _store.GetQuotesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (quote.ConsecutiveFailures <= 0)
                {
                    continue;
                }

                failing++;
                if (quote.ConsecutiveFailures > worstStreak)
                {
                    worstStreak = quote.ConsecutiveFailures;
                    lastError = quote.LastError;
                }
            }

            IReadOnlyList<PaymentValuation> queued = await _store
                .GetPendingValuationsAsync(_options.ValuationBatchSize, cancellationToken)
                .ConfigureAwait(false);
            pending = queued.Count;
            if (queued.Count > 0)
            {
                oldestPendingAge = now - queued[0].EnqueuedAt;
            }

            undelivered = (await _store
                .GetUndeliveredValuationsAsync(_options.ValuationBatchSize, cancellationToken)
                .ConfigureAwait(false)).Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A health check that could not read the store must not answer "healthy" on counts that
            // defaulted to zero.
            storeReadable = false;
            lastError = ex.Message;
            _logger.LogError(ex, "reading the quote store for the health report failed");
        }

        return new QuoteHealthReport
        {
            ConfiguredPairs = _registry.Pairs.Count,
            PairsWithFreshQuote = fresh,
            OldestQuoteAge = oldestQuoteAge,
            PairsFailing = failing,
            MaxConsecutiveFailures = worstStreak,
            LastError = lastError,
            PendingValuations = pending,
            OldestPendingAge = oldestPendingAge,
            UndeliveredValuations = undelivered,
            CycleFitsInInterval = QuoteSchedule.CycleFitsInInterval(
                _registry.Pairs.Count, _options.RefreshInterval, _options.MinimumPairStagger),
            StoreReadable = storeReadable,
        };
    }
}
