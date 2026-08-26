using Microsoft.Extensions.Logging;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Retries store operations until they succeed or the token is cancelled. There is no attempt limit on
/// purpose: the store is the source of truth, and giving up means losing a payment. Callers pause while
/// this runs, which is what freezes the ledger cursor during a store outage.
/// </summary>
internal sealed class StoreRetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<bool>? _onAvailabilityChanged;

    public StoreRetryPolicy(
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        TimeProvider timeProvider,
        ILogger logger,
        Action<bool>? onAvailabilityChanged = null)
    {
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _timeProvider = timeProvider;
        _logger = logger;
        _onAvailabilityChanged = onAvailabilityChanged;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        bool reportedUnavailable = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                T result = await operation(cancellationToken).ConfigureAwait(false);
                if (reportedUnavailable)
                {
                    _logger.LogInformation("store recovered on {Operation} after {Attempts} failed attempts", operationName, attempt);
                    _onAvailabilityChanged?.Invoke(true);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (!reportedUnavailable)
                {
                    reportedUnavailable = true;
                    _onAvailabilityChanged?.Invoke(false);
                }

                _logger.LogError(ex, "store operation {Operation} failed on attempt {Attempt}; retrying", operationName, attempt);
                await Task.Delay(NextDelay(attempt), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            operationName,
            cancellationToken);

    private TimeSpan NextDelay(int attempt)
    {
        double exponent = Math.Min(attempt - 1, 16);
        double milliseconds = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        double capped = Math.Min(milliseconds, _maxDelay.TotalMilliseconds);
        double jittered = capped * (0.75 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, _maxDelay.TotalMilliseconds));
    }
}
