using Xrpl.Client;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// A deliberately minimal <see cref="IQuoteSource"/> over one AMM pool, for proving the wiring.
/// </summary>
/// <remarks>
/// Not a pricing engine and not shipped. It reads amm_info and applies the constant-product formula:
/// no order book, no routing, no multi-hop. Real pricing is the host's, which is the whole point of
/// <see cref="IQuoteSource"/> being an interface.
/// </remarks>
public sealed class AmmQuoteSource : IQuoteSource
{
    private readonly XrplClient _client;

    public AmmQuoteSource(XrplClient client) => _client = client;

    public async Task<IQuoteSnapshot?> CaptureAsync(QuotePair pair, CancellationToken cancellationToken)
    {
        AMMInfoResponse info = await _client.AmmInfo(new AMMInfoRequest
        {
            Asset = Asset(pair.Currency, pair.Issuer),
            Asset2 = Asset(pair.QuoteCurrency, pair.QuoteIssuer),
        }).Typed();

        if (info?.Amm is null)
        {
            // No pool for this pair. That is "nothing there", not "could not ask" — the client throws if
            // it could not ask, and the distinction is what keeps a dead socket from emptying a quote.
            return null;
        }

        decimal poolIn = PoolAmount(info.Amm.Amount);
        decimal poolOut = PoolAmount(info.Amm.Amount2);
        decimal fee = info.Amm.TradingFee / 100_000m;

        uint ledgerIndex = await StandaloneFixture.CurrentValidatedLedgerAsync(_client);

        return poolIn <= 0m || poolOut <= 0m
            ? null
            : new AmmSnapshot(poolIn, poolOut, fee, ledgerIndex, DateTimeOffset.UtcNow);
    }

    private static Common.IssuedCurrency Asset(string currency, string? issuer) =>
        new Common.IssuedCurrency { Currency = currency, Issuer = issuer };

    /// <summary>Pool side in human units. ValueAsNumber reports XRP in drops, so XRP takes the other path.</summary>
    private static decimal PoolAmount(Currency amount) =>
        string.IsNullOrEmpty(amount.Issuer) ? amount.ValueAsXrp ?? 0m : amount.ValueAsNumber;

    private sealed class AmmSnapshot : IQuoteSnapshot
    {
        private readonly decimal _poolIn;
        private readonly decimal _poolOut;
        private readonly decimal _fee;

        public AmmSnapshot(decimal poolIn, decimal poolOut, decimal fee, uint ledgerIndex, DateTimeOffset capturedAt)
        {
            _poolIn = poolIn;
            _poolOut = poolOut;
            _fee = fee;
            LedgerIndex = ledgerIndex;
            CapturedAt = capturedAt;
        }

        public uint LedgerIndex { get; }

        public DateTimeOffset CapturedAt { get; }

        /// <summary>Spot price of the pool, fee included.</summary>
        public decimal? MarginalPrice => _poolOut / _poolIn * (1m - _fee);

        public ValueTask<QuoteResult?> EvaluateAsync(
            decimal amount,
            QuoteDirection direction,
            CancellationToken cancellationToken)
        {
            if (direction != QuoteDirection.ExactInput)
            {
                // The wiring test only needs valuation. A real source answers both.
                return new ValueTask<QuoteResult?>((QuoteResult?)null);
            }

            decimal effectiveIn = amount * (1m - _fee);
            decimal output = effectiveIn * _poolOut / (_poolIn + effectiveIn);

            return new ValueTask<QuoteResult?>(new QuoteResult
            {
                Direction = direction,
                InputAmount = amount,
                FilledInput = amount,
                OutputAmount = output,
                MarginalPrice = MarginalPrice,
                Route = "AMM",
            });
        }
    }
}
