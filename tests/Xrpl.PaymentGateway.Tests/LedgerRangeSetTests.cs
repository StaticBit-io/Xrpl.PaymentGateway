using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class LedgerRangeSetTests
{
    [Fact]
    public void ASingleRangeCoversLedgersInsideIt()
    {
        Assert.True(LedgerRangeSet.TryParse("32570-99383752", out LedgerRangeSet set));

        Assert.True(set.Covers(40000, 50000));
        Assert.True(set.Covers(32570, 99383752));
    }

    [Fact]
    public void ASingleRangeDoesNotCoverLedgersBelowOrAboveIt()
    {
        Assert.True(LedgerRangeSet.TryParse("100-200", out LedgerRangeSet set));

        Assert.False(set.Covers(99, 150));
        Assert.False(set.Covers(150, 201));
    }

    [Fact]
    public void AGappedListIsParsedAndTheGapIsNotCovered()
    {
        Assert.True(LedgerRangeSet.TryParse("24900901-24900984,24901116-24901158", out LedgerRangeSet set));

        Assert.True(set.Covers(24900902, 24900950));
        Assert.True(set.Covers(24901116, 24901158));
        Assert.False(set.Covers(24900950, 24901120));
    }

    [Fact]
    public void ASingleLedgerEntryIsARangeOfOne()
    {
        Assert.True(LedgerRangeSet.TryParse("500", out LedgerRangeSet set));

        Assert.True(set.Covers(500, 500));
        Assert.False(set.Covers(500, 501));
    }

    [Fact]
    public void TheEmptyMarkerParsesToASetThatCoversNothing()
    {
        Assert.True(LedgerRangeSet.TryParse("empty", out LedgerRangeSet set));

        Assert.False(set.Covers(1, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-range")]
    [InlineData("200-100")]
    [InlineData("100-")]
    public void UnparseableInputFailsClosed(string? input)
    {
        Assert.False(LedgerRangeSet.TryParse(input, out LedgerRangeSet set));
        Assert.False(set.Covers(1, 1));
    }

    [Fact]
    public void AnEmptyRequestedSpanIsTriviallyCovered()
    {
        Assert.True(LedgerRangeSet.TryParse("100-200", out LedgerRangeSet set));

        Assert.True(set.Covers(300, 299));
    }
}
