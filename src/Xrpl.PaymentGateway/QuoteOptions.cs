using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>Settings for the quote collector. Separate from the gateway's own options, as the feature is.</summary>
public sealed class QuoteOptions
{
    /// <summary>Pairs to keep a liquidity reading for. At least one.</summary>
    public IReadOnlyList<QuotePair> Pairs { get; set; } = Array.Empty<QuotePair>();

    /// <summary>How often each pair is refreshed.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Floor on the gap between two pair refreshes.
    /// </summary>
    /// <remarks>
    /// Pairs are spread evenly across <see cref="RefreshInterval"/> and never fired closer together than
    /// this. With more pairs than the interval can hold at this spacing, the cycle takes longer than the
    /// interval rather than bunching up — the health report says so when it happens.
    /// </remarks>
    public TimeSpan MinimumPairStagger { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How old a reading may be and still be served. Null means three <see cref="RefreshInterval"/>s.
    /// </summary>
    public TimeSpan? MaxQuoteAge { get; set; }

    /// <summary>
    /// Whether a reading past <see cref="EffectiveMaxQuoteAge"/> is withheld rather than served with its age.
    /// </summary>
    /// <remarks>
    /// On by default. For a payment gateway "I do not know the price" is an answer the host can act on;
    /// a confident number computed from a half-hour-old book is a wrong invoice discovered much later.
    /// </remarks>
    public bool RefuseStaleQuotes { get; set; } = true;

    /// <summary>Whether each payment gets its own capture rather than being priced off the current one.</summary>
    /// <remarks>Off by default: it costs one network round trip per payment.</remarks>
    public bool ValuateWithFreshSnapshot { get; set; }

    /// <summary>How long a single <see cref="IQuoteSource.CaptureAsync"/> may run before it is abandoned.</summary>
    /// <remarks>A source that hangs must not stall the whole cycle behind it.</remarks>
    public TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many queued valuations are processed, and how many delivered, per pass.</summary>
    public int ValuationBatchSize { get; set; } = 50;

    /// <summary>How often the valuation queue is drained.</summary>
    /// <remarks>
    /// Much shorter than <see cref="RefreshInterval"/> on purpose: a payment should be priced within
    /// seconds of arriving, while a book does not need re-reading that often.
    /// </remarks>
    public TimeSpan ValuationPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The age limit actually applied.</summary>
    public TimeSpan EffectiveMaxQuoteAge => MaxQuoteAge ?? RefreshInterval * 3;
}
