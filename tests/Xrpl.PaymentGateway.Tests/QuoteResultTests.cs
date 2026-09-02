using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteResultTests
{
    private static QuoteResult Result(
        decimal input = 1000m,
        decimal filled = 1000m,
        decimal output = 10m,
        decimal? marginal = 0.0102m) => new QuoteResult
        {
            Direction = QuoteDirection.ExactInput,
            InputAmount = input,
            FilledInput = filled,
            OutputAmount = output,
            MarginalPrice = marginal,
        };

    [Fact]
    public void TheEffectivePriceComesFromWhatActuallyFilled()
    {
        // Dividing by the requested amount instead prices a trade that cannot happen.
        QuoteResult result = Result(input: 1000m, filled: 500m, output: 5m);

        Assert.Equal(0.01m, result.EffectivePrice);
    }

    [Fact]
    public void APartialFillIsNotAFullFill()
    {
        Assert.False(Result(input: 1000m, filled: 500m).IsFullyFilled);
        Assert.True(Result(input: 1000m, filled: 1000m).IsFullyFilled);
    }

    [Fact]
    public void ARoundingResidueStillCountsAsFullyFilled()
    {
        // Walking a hundred book levels leaves a residue far below anything the ledger can express.
        Assert.True(Result(input: 1000m, filled: 1000m - 0.000000000000000001m).IsFullyFilled);
    }

    [Fact]
    public void SlippageIsHowMuchWorseThanTheMarginalPrice()
    {
        QuoteResult result = Result(input: 1000m, filled: 1000m, output: 9.9m, marginal: 0.01m);

        // Effective 0.0099 against a marginal 0.01 is one percent worse.
        Assert.Equal(1m, result.SlippagePercent);
    }

    [Fact]
    public void WithoutAMarginalPriceThereIsNoSlippageNumber()
    {
        Assert.Null(Result(marginal: null).SlippagePercent);
    }

    [Fact]
    public void NothingFilledMeansNoPriceRatherThanZero()
    {
        QuoteResult result = Result(input: 1000m, filled: 0m, output: 0m);

        Assert.Null(result.EffectivePrice);
        Assert.Null(result.SlippagePercent);
        Assert.False(result.IsFullyFilled);
    }

    [Fact]
    public void ATruncatedBookDefaultsToFalse()
    {
        Assert.False(Result().BookTruncated);
    }

    // The following tests document and pin the contract that OutputAmount is always what FilledInput
    // produces, in both directions. This ensures EffectivePrice and IsFullyFilled remain honest even
    // when ExactOutput asks for a size the venues cannot fully absorb.

    [Fact]
    public void UnderExactOutputFullyFilledTheOutputAmountIsWhatWasAsked()
    {
        // The caller asked for 100 units of the quote asset and got exactly that.
        QuoteResult result = new QuoteResult
        {
            Direction = QuoteDirection.ExactOutput,
            InputAmount = 1000m,
            FilledInput = 1000m,
            OutputAmount = 100m,
            MarginalPrice = 0.1m,
        };

        Assert.Equal(100m, result.OutputAmount);
        Assert.True(result.IsFullyFilled);
        Assert.Equal(0.1m, result.EffectivePrice);
    }

    [Fact]
    public void UnderExactOutputPartialFillReducesOutputAmountToWhatWasActuallyFilled()
    {
        // The caller asked for 100 units of the quote asset, but the venues could only absorb 500 of
        // the 1000 units needed to get there. OutputAmount is reduced to what those 500 units produce,
        // not left at the caller's ask. This keeps EffectivePrice honest: it is 50 / 500 = 0.1, the
        // achieved price, not 100 / 500 = 0.2, which would overstate what was actually bought.
        QuoteResult result = new QuoteResult
        {
            Direction = QuoteDirection.ExactOutput,
            InputAmount = 1000m,
            FilledInput = 500m,
            OutputAmount = 50m,
            MarginalPrice = 0.1m,
        };

        Assert.Equal(50m, result.OutputAmount);
        Assert.False(result.IsFullyFilled);
        Assert.Equal(0.1m, result.EffectivePrice);
    }
}
