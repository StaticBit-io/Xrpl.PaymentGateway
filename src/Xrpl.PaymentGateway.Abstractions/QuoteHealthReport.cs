namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>What the quote collector and the valuation queue are doing.</summary>
public sealed class QuoteHealthReport
{
    /// <summary>Pairs configured.</summary>
    public required int ConfiguredPairs { get; init; }

    /// <summary>Pairs holding a reading within the age limit.</summary>
    public required int PairsWithFreshQuote { get; init; }

    /// <summary>Age of the oldest held reading, or null when nothing has been captured yet.</summary>
    public TimeSpan? OldestQuoteAge { get; init; }

    /// <summary>Pairs whose last refresh failed.</summary>
    public required int PairsFailing { get; init; }

    /// <summary>Worst consecutive-failure streak across pairs.</summary>
    public required int MaxConsecutiveFailures { get; init; }

    /// <summary>Message from the most recent failure, or null when nothing is failing.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Payments queued for valuation and not yet priced, across every pair — the true count, not a page
    /// capped at the batch size.
    /// </summary>
    public required int PendingValuations { get; init; }

    /// <summary>Age of the oldest queued payment across every pair, or null when the queue is empty.</summary>
    public TimeSpan? OldestPendingAge { get; init; }

    /// <summary>Valuations computed but not yet accepted by the host handler, capped at the batch size.</summary>
    public required int UndeliveredValuations { get; init; }

    /// <summary>
    /// Age of the oldest undelivered valuation, or null when nothing is waiting on delivery.
    /// </summary>
    /// <remarks>
    /// <see cref="UndeliveredValuations"/> saturates at the batch size, so a host whose
    /// <c>IPaymentValuedHandler</c> is permanently broken can look like a queue that is draining even
    /// though nothing has left it in hours. The age does not saturate, which is what makes it the field
    /// to alert on.
    /// </remarks>
    public TimeSpan? OldestUndeliveredAge { get; init; }

    /// <summary>
    /// Entries in <c>ValuationState.Failed</c> right now — the automatic pipeline gave up on them and they
    /// wait on an operator through <c>IUnresolvedValuationAdmin</c>.
    /// </summary>
    /// <remarks>
    /// A written-off entry is settled and does not count here: this is the queue an operator still has open
    /// work in, not a running total of everything that ever failed.
    /// </remarks>
    public required int FailedValuations { get; init; }

    /// <summary>
    /// Whether a full refresh cycle fits inside the configured interval.
    /// </summary>
    /// <remarks>False means pairs refresh slower than configured; add fewer pairs or a longer interval.</remarks>
    public required bool CycleFitsInInterval { get; init; }

    /// <summary>Whether the store could be read at all.</summary>
    public required bool StoreReadable { get; init; }

    /// <summary>
    /// How many pairs' most recent attempt to persist a quote failed to reach the store, right now.
    /// </summary>
    /// <remarks>
    /// Reads and writes can fail independently: a store whose writes hang or throw while its reads keep
    /// answering would otherwise be invisible here, because <see cref="PairsWithFreshQuote"/> and
    /// <see cref="PairsFailing"/> come from the collector's in-memory snapshot and its own cached last
    /// write, both of which keep looking current every cycle regardless of whether the write beneath them
    /// ever lands. Per pair, like every other count in this report — a store rejecting writes for two pairs
    /// out of three must not be erased by the third pair's next successful write.
    /// </remarks>
    public required int PairsFailingToPersist { get; init; }

    /// <summary><see cref="PairsFailingToPersist"/> being zero, spelled as a bool.</summary>
    public bool StoreWritable => PairsFailingToPersist == 0;

    /// <summary>
    /// True only when every pair holds a fresh reading, nothing is failing, and the store both answered
    /// and accepted every pair's last write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A queue with work in it is not unhealthy: valuations are expected to lag by design. A queue that
    /// stops draining shows up as a growing <see cref="OldestPendingAge"/>, which is the number to alert on.
    /// A backlog of <see cref="FailedValuations"/> is not itself unhealthy either — it is expected work for
    /// an operator, not a symptom of the pipeline being broken — but it should not be ignored.
    /// </para>
    /// <para>
    /// A pair with genuinely no liquidity also reads as unhealthy: the collector correctly clears its
    /// cached snapshot on an empty capture, so that pair never counts toward
    /// <see cref="PairsWithFreshQuote"/> and <see cref="ConfiguredPairs"/> stays higher than it. This is
    /// defensible — there is really nothing to price — but an operator reading a false <c>IsHealthy</c>
    /// without this context will read it as a failure to investigate rather than a market fact.
    /// </para>
    /// </remarks>
    public bool IsHealthy =>
        StoreReadable && StoreWritable && PairsFailing == 0 && ConfiguredPairs > 0
        && PairsWithFreshQuote == ConfiguredPairs;
}
