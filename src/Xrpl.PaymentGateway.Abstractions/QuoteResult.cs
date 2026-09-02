namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>What a given trade size costs, priced against one liquidity snapshot.</summary>
public sealed class QuoteResult
{
    /// <summary>
    /// Relative shortfall below which the input counts as fully consumed.
    /// </summary>
    /// <remarks>
    /// Summing a hundred book levels rounds at decimal's 28th significant digit, so an order that ate
    /// every last unit still reports a residue. The ledger carries fifteen significant digits for an
    /// issued amount and six decimals for XRP, so nothing this small exists as an amount anyone could
    /// send. Comparing against exact zero makes a filled order announce itself as partial.
    /// </remarks>
    private const decimal FillTolerance = 1e-15m;

    /// <summary>
    /// Which side was pinned. See <see cref="QuoteDirection"/> for what that means for the fields below —
    /// they are always in pay-to-get terms, regardless of direction.
    /// </summary>
    public required QuoteDirection Direction { get; init; }

    /// <summary>
    /// Amount of the received asset the trade requires. Under <see cref="QuoteDirection.ExactInput"/> this
    /// is the amount the caller asked to price; under <see cref="QuoteDirection.ExactOutput"/> it is the
    /// amount the implementation computed as necessary to yield <see cref="OutputAmount"/>.
    /// </summary>
    public required decimal InputAmount { get; init; }

    /// <summary>
    /// How much of <see cref="InputAmount"/> the venues could actually absorb. Below
    /// <see cref="InputAmount"/> when they ran dry — true in either direction, since
    /// <see cref="InputAmount"/> is the requirement whichever side the caller pinned.
    /// </summary>
    public required decimal FilledInput { get; init; }

    /// <summary>
    /// Quote-asset amount the trade produces. Under <see cref="QuoteDirection.ExactOutput"/> this is the
    /// amount the caller asked for; under <see cref="QuoteDirection.ExactInput"/> it is what
    /// <see cref="FilledInput"/> turned out to be worth.
    /// </summary>
    public required decimal OutputAmount { get; init; }

    /// <summary>Best executable price at the snapshot, before any size was pushed through.</summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>Assets the route went through, for auditing an old valuation.</summary>
    public string? Route { get; init; }

    /// <summary>Whether the node returned as many offers as were asked for, so the real book may run deeper.</summary>
    public bool BookTruncated { get; init; }

    /// <summary>
    /// Price actually achieved, or null when nothing filled. Derived from what filled, so this is the same
    /// computation in either direction.
    /// </summary>
    public decimal? EffectivePrice =>
        FilledInput > 0m ? OutputAmount / FilledInput : null;

    /// <summary>
    /// Whether the whole required input could actually be traded. The same meaning under both directions:
    /// under <see cref="QuoteDirection.ExactInput"/> the caller's own amount is the requirement; under
    /// <see cref="QuoteDirection.ExactOutput"/> the requirement is the <see cref="InputAmount"/> the
    /// implementation computed to reach the caller's requested <see cref="OutputAmount"/>.
    /// </summary>
    public bool IsFullyFilled =>
        InputAmount > 0m && InputAmount - FilledInput <= InputAmount * FillTolerance;

    /// <summary>
    /// How much worse than <see cref="MarginalPrice"/> the achieved price was, in percent. Derived from
    /// what filled, so this is the same computation in either direction.
    /// </summary>
    public decimal? SlippagePercent =>
        MarginalPrice is { } marginal && marginal > 0m && EffectivePrice is { } effective
            ? (marginal - effective) / marginal * 100m
            : null;
}
