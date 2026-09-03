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
    public void TheTwoSpellingsAreEqualAndCollapseInAHashSet()
    {
        // QuotePair promises value equality by being a class with a Key-based Equals/GetHashCode; a host
        // that puts pairs from two spellings of one asset into a HashSet must end up with one entry, not
        // a silent duplicate.
        QuotePair readable = new QuotePair("XPM", XpmIssuer, "USD", UsdIssuer);
        QuotePair hex = new QuotePair(
            "00000000000000000000000058504d0000000000", XpmIssuer, "USD", UsdIssuer);

        Assert.Equal(readable, hex);
        Assert.True(readable == hex);
        Assert.False(readable != hex);
        Assert.Equal(readable.GetHashCode(), hex.GetHashCode());

        HashSet<QuotePair> set = new HashSet<QuotePair> { readable, hex };
        Assert.Single(set);
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
    public void TheAllZeroHexFormOfXrpMatchesAPaymentReportedThatWay()
    {
        // Before CurrencyKey.Canonical recognised the ledger's own all-zero encoding of XRP, a pair
        // configured with the readable "XRP" spelling could never match a payment the balance reader
        // reported in that hex form: the two would canonicalize to different keys.
        QuotePair pair = new QuotePair("XPM", XpmIssuer, "XRP", null);

        Assert.True(pair.Matches("XPM", XpmIssuer));
        Assert.Contains("XRP", pair.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAllZeroHexFormOfXrpWithAnIssuerIsRejected()
    {
        // Before the fix this spelling fell through to the issued-currency branch, so
        // RequireIssuerConsistency did not recognise it as XRP and let an XRP pair carrying an issuer be
        // constructed.
        Assert.Throws<ArgumentException>(
            () => new QuotePair("0000000000000000000000000000000000000000", XpmIssuer, "USD", UsdIssuer));
    }

    [Fact]
    public void QuotingAnAssetAgainstItselfIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new QuotePair("XPM", XpmIssuer, "XPM", XpmIssuer));
    }
}
