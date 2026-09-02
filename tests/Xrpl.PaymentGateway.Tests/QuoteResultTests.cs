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
}
