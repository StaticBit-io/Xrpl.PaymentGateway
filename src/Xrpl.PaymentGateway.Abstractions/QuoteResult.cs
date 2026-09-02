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

    /// <summary>Which side was pinned.</summary>
    public required QuoteDirection Direction { get; init; }

    /// <summary>Amount the caller asked to price.</summary>
    public required decimal InputAmount { get; init; }

    /// <summary>Amount the venues could actually absorb. Below <see cref="InputAmount"/> when they ran dry.</summary>
    public required decimal FilledInput { get; init; }

    /// <summary>Quote-asset amount produced by <see cref="FilledInput"/>.</summary>
    public required decimal OutputAmount { get; init; }

    /// <summary>Best executable price at the snapshot, before any size was pushed through.</summary>
    public decimal? MarginalPrice { get; init; }

    /// <summary>Assets the route went through, for auditing an old valuation.</summary>
    public string? Route { get; init; }

    /// <summary>Whether the node returned as many offers as were asked for, so the real book may run deeper.</summary>
    public bool BookTruncated { get; init; }

    /// <summary>Price actually achieved, or null when nothing filled.</summary>
    public decimal? EffectivePrice =>
        FilledInput > 0m ? OutputAmount / FilledInput : null;

    /// <summary>Whether the whole requested amount could be traded.</summary>
    public bool IsFullyFilled =>
        InputAmount > 0m && InputAmount - FilledInput <= InputAmount * FillTolerance;

    /// <summary>How much worse than <see cref="MarginalPrice"/> the achieved price was, in percent.</summary>
    public decimal? SlippagePercent =>
        MarginalPrice is { } marginal && marginal > 0m && EffectivePrice is { } effective
            ? (marginal - effective) / marginal * 100m
            : null;
}
