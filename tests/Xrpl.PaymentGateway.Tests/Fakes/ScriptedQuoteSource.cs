using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>An <see cref="IQuoteSource"/> the test drives by hand.</summary>
public sealed class ScriptedQuoteSource : IQuoteSource
{
    private readonly object _gate = new object();
    private readonly List<string> _captured = new List<string>();

    /// <summary>Per pair key: what the next capture does. Absent means "return a snapshot".</summary>
    public Dictionary<string, Func<IQuoteSnapshot?>> Behaviour { get; } =
        new Dictionary<string, Func<IQuoteSnapshot?>>(StringComparer.Ordinal);

    /// <summary>Pair keys in the order captures were requested.</summary>
    public IReadOnlyList<string> Captured
    {
        get
        {
            lock (_gate)
            {
                return _captured.ToList();
            }
        }
    }

    public int CountFor(string pairKey)
    {
        lock (_gate)
        {
            return _captured.Count(key => string.Equals(key, pairKey, StringComparison.Ordinal));
        }
    }

    public Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _captured.Add(pair.Key);
        }

        if (Behaviour.TryGetValue(pair.Key, out Func<IQuoteSnapshot?>? behaviour))
        {
            return Task.FromResult(behaviour());
        }

        return Task.FromResult<IQuoteSnapshot?>(new StubQuoteSnapshot());
    }
}

/// <summary>A snapshot that prices everything at a fixed rate.</summary>
public sealed class StubQuoteSnapshot : IQuoteSnapshot
{
    public StubQuoteSnapshot(decimal price = 0.01m, uint ledgerIndex = 900, DateTimeOffset? capturedAt = null)
    {
        MarginalPrice = price;
        LedgerIndex = ledgerIndex;
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
    }

    public uint LedgerIndex { get; }

    public DateTimeOffset CapturedAt { get; }

    public decimal? MarginalPrice { get; }

    public ValueTask<QuoteResult?> EvaluateAsync(
        decimal amount,
        QuoteDirection direction,
        CancellationToken cancellationToken) =>
        new ValueTask<QuoteResult?>(new QuoteResult
        {
            Direction = direction,
            InputAmount = amount,
            FilledInput = amount,
            OutputAmount = amount * (MarginalPrice ?? 0m),
            MarginalPrice = MarginalPrice,
            Route = "STUB",
        });
}

/// <summary>A capture that never finishes, for proving the timeout.</summary>
public sealed class HangingQuoteSource : IQuoteSource
{
    public async Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return null;
    }
}
