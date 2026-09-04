using Xrpl.Client;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Prices a pair off the ledger, from the AMM pool that holds its two assets.
/// </summary>
/// <remarks>
/// <para>
/// One pool, the constant-product formula, and the pool's own trading fee — no order book, no routing
/// through a third asset, no splitting a size across venues. A real pricing engine does all of those, and
/// the answers differ: a pair whose book is deeper than its pool is mispriced by this, and a pair with no
/// pool at all reads as having no liquidity when an order book may be standing right there. It is a real
/// source in the sense that matters here — every number comes from a validated ledger, size moves the
/// price, and the reading goes stale like any other — and it is still a demonstration, not a quote engine.
/// </para>
/// <para>
/// The fee is charged on the way in, which is what the ledger does, so it shows up in the marginal price
/// as well as in the fill.
/// </para>
/// </remarks>
public sealed class AmmQuoteSource : IQuoteSource
{
    private readonly NodeConnection _connection;

    public AmmQuoteSource(NodeConnection connection) => _connection = connection;

    public async Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);

        XrplClient client = await _connection.ClientAsync(cancellationToken).ConfigureAwait(false);

        AMMInfoResponse info = await client.AmmInfo(new AMMInfoRequest
        {
            Asset = Asset(pair.Currency, pair.Issuer),
            Asset2 = Asset(pair.QuoteCurrency, pair.QuoteIssuer),

            // Explicitly validated: left at the default the node answers from the in-progress ledger and
            // reports that as ledger_current_index, not the ledger_index this reads.
            LedgerIndex = new LedgerIndex(LedgerIndexType.Validated),
        }).Typed();

        if (info?.Amm is null)
        {
            // No pool for this pair. That is "asked, and there is nothing here" — the answer null is
            // reserved for — rather than "could not ask", which arrives as an exception from the client.
            return null;
        }

        if (info.LedgerIndex is not { } responseLedgerIndex)
        {
            // Having asked for the validated ledger, a response without an index means the response shape
            // changed, not that the pool is empty. Returning null would tell the collector this pair has no
            // liquidity — clearing the last good reading and counting a clean capture — so it throws and is
            // recorded as the failure it is.
            throw new InvalidOperationException(
                $"amm_info for {pair.Key} answered without a ledger_index; the response shape may have changed");
        }

        // Which side of the response is the received asset is read off the amounts rather than assumed from
        // the order they were requested in: an AMM stores its two assets in the ledger's own canonical
        // order, and a pair quoted the other way round would otherwise be priced upside down.
        (Currency received, Currency quote) = OrderSides(info.Amm.Amount, info.Amm.Amount2, pair);

        decimal poolIn = PoolAmount(received);
        decimal poolOut = PoolAmount(quote);
        decimal fee = info.Amm.TradingFee / 100_000m;

        // Stamped from the same response the amounts came from rather than a second call for the current
        // ledger: one can close between the two, and the newer index would then label older pool state.
        return poolIn <= 0m || poolOut <= 0m
            ? null
            : new AmmSnapshot(poolIn, poolOut, fee, (uint)responseLedgerIndex, DateTimeOffset.UtcNow,
                $"AMM {pair.Currency}/{pair.QuoteCurrency}");
    }

    private static Common.IssuedCurrency Asset(string currency, string? issuer) =>
        new Common.IssuedCurrency { Currency = currency, Issuer = issuer };

    /// <summary>Returns the pool's two sides as (received asset, quote asset) for this pair.</summary>
    private static (Currency Received, Currency Quote) OrderSides(Currency amount, Currency amount2, QuotePair pair)
    {
        if (IsSide(amount, pair.Currency, pair.Issuer) && IsSide(amount2, pair.QuoteCurrency, pair.QuoteIssuer))
        {
            return (amount, amount2);
        }

        if (IsSide(amount2, pair.Currency, pair.Issuer) && IsSide(amount, pair.QuoteCurrency, pair.QuoteIssuer))
        {
            return (amount2, amount);
        }

        throw new InvalidOperationException(
            $"amm_info for {pair.Key} returned a pool holding assets this pair does not name");
    }

    /// <summary>Whether one side of the pool is the given asset, comparing codes the way the gateway does.</summary>
    private static bool IsSide(Currency amount, string currency, string? issuer)
    {
        bool sideIsXrp = string.IsNullOrEmpty(amount.Issuer);
        bool assetIsXrp = string.Equals(CurrencyKey.Canonical(currency), "XRP", StringComparison.Ordinal);

        if (sideIsXrp || assetIsXrp)
        {
            return sideIsXrp && assetIsXrp;
        }

        return string.Equals(
                   CurrencyKey.Canonical(amount.CurrencyCode ?? string.Empty),
                   CurrencyKey.Canonical(currency),
                   StringComparison.Ordinal)
               && string.Equals(amount.Issuer, issuer, StringComparison.Ordinal);
    }

    /// <summary>A pool side in whole units. ValueAsNumber reports XRP in drops, so XRP takes the other path.</summary>
    private static decimal PoolAmount(Currency amount) =>
        string.IsNullOrEmpty(amount.Issuer) ? amount.ValueAsXrp ?? 0m : amount.ValueAsNumber;

    private sealed class AmmSnapshot : IQuoteSnapshot
    {
        private readonly decimal _poolIn;
        private readonly decimal _poolOut;
        private readonly decimal _fee;
        private readonly string _route;

        public AmmSnapshot(
            decimal poolIn, decimal poolOut, decimal fee, uint ledgerIndex, DateTimeOffset capturedAt, string route)
        {
            _poolIn = poolIn;
            _poolOut = poolOut;
            _fee = fee;
            _route = route;
            LedgerIndex = ledgerIndex;
            CapturedAt = capturedAt;
        }

        public uint LedgerIndex { get; }

        public DateTimeOffset CapturedAt { get; }

        /// <summary>
        /// The pool's spot price with the fee applied: what an infinitely small trade would get. Every real
        /// size gets less, which is exactly the difference <see cref="QuoteResult.SlippagePercent"/> reports.
        /// </summary>
        public decimal? MarginalPrice => _poolOut / _poolIn * (1m - _fee);

        public ValueTask<QuoteResult?> EvaluateAsync(
            decimal amount,
            QuoteDirection direction,
            CancellationToken cancellationToken)
        {
            QuoteResult result = direction == QuoteDirection.ExactInput
                ? ForExactInput(amount)
                : ForExactOutput(amount);

            return new ValueTask<QuoteResult?>(result);
        }

        /// <summary>Sell this much of the received asset into the pool: what comes back out.</summary>
        private QuoteResult ForExactInput(decimal amount)
        {
            decimal effectiveInput = amount * (1m - _fee);
            decimal output = effectiveInput * _poolOut / (_poolIn + effectiveInput);

            return new QuoteResult
            {
                Direction = QuoteDirection.ExactInput,
                InputAmount = amount,
                FilledInput = amount,
                OutputAmount = output,
                MarginalPrice = MarginalPrice,
                Route = _route,
            };
        }

        /// <summary>Take this much of the quote asset out of the pool: what has to go in for it.</summary>
        /// <remarks>
        /// A constant-product pool can never be emptied — the input needed for its whole other side is
        /// unbounded — so an ask at or above what the pool holds is not a large trade, it is an impossible
        /// one. That is reported as a zero fill rather than an invented number: nothing filled, no
        /// effective price, and <see cref="QuoteResult.IsFullyFilled"/> false.
        /// </remarks>
        private QuoteResult ForExactOutput(decimal amount)
        {
            if (amount >= _poolOut)
            {
                return new QuoteResult
                {
                    Direction = QuoteDirection.ExactOutput,
                    InputAmount = 0m,
                    FilledInput = 0m,
                    OutputAmount = 0m,
                    MarginalPrice = MarginalPrice,
                    Route = _route,
                };
            }

            decimal effectiveInput = _poolIn * amount / (_poolOut - amount);
            decimal input = effectiveInput / (1m - _fee);

            return new QuoteResult
            {
                Direction = QuoteDirection.ExactOutput,
                InputAmount = input,
                FilledInput = input,
                OutputAmount = amount,
                MarginalPrice = MarginalPrice,
                Route = _route,
            };
        }
    }
}
