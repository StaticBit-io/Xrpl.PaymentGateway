using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuotePairTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string UsdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    [Fact]
    public void TheSamePairWrittenTwoWaysHasOneKey()
    {
        QuotePair readable = new QuotePair("XPM", XpmIssuer, "USD", UsdIssuer);
        QuotePair hex = new QuotePair(
            "00000000000000000000000058504d0000000000", XpmIssuer, "USD", UsdIssuer);

        Assert.Equal(readable.Key, hex.Key);
    }

    [Fact]
    public void QuotingAgainstXrpNeedsNoIssuer()
    {
        QuotePair pair = new QuotePair("XPM", XpmIssuer, "XRP", null);

        Assert.Contains("XRP", pair.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void APairMatchesAPaymentInEitherWriting()
    {
        QuotePair pair = new QuotePair("XPM", XpmIssuer, "USD", UsdIssuer);

        Assert.True(pair.Matches("00000000000000000000000058504D0000000000", XpmIssuer));
        Assert.True(pair.Matches("XPM", XpmIssuer));
    }

    [Fact]
    public void ADifferentIssuerIsADifferentAsset()
    {
        QuotePair pair = new QuotePair("XPM", XpmIssuer, "USD", UsdIssuer);

        Assert.False(pair.Matches("XPM", UsdIssuer));
        Assert.False(pair.Matches("XPM", null));
    }

    [Fact]
    public void AnIssuedCurrencyWithoutAnIssuerIsRejected()
    {
        // Only XRP has no issuer. A token without one cannot be addressed on the ledger at all.
        Assert.Throws<ArgumentException>(() => new QuotePair("XPM", null, "XRP", null));
    }

    [Fact]
    public void XrpWithAnIssuerIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new QuotePair("XRP", XpmIssuer, "USD", UsdIssuer));
    }

    [Fact]
    public void QuotingAnAssetAgainstItselfIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new QuotePair("XPM", XpmIssuer, "XPM", XpmIssuer));
    }
}
