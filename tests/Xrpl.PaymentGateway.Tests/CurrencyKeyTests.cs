using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class CurrencyKeyTests
{
    [Fact]
    public void XrpIsItsOwnCanonicalForm()
    {
        Assert.Equal("XRP", CurrencyKey.Canonical("XRP"));
        Assert.Equal("XRP", CurrencyKey.Canonical("xrp"));
    }

    [Fact]
    public void AThreeCharacterCodeBecomesItsFortyCharacterHexForm()
    {
        // The ledger stores a standard code in bytes 12..14 of a 160-bit field, everything else zero.
        Assert.Equal(
            "00000000000000000000000058504D0000000000",
            CurrencyKey.Canonical("XPM"));
    }

    [Fact]
    public void TheHexFormAndTheReadableFormCanonicalizeTogether()
    {
        // The whole point: a pair configured in hex must match a payment reported in ASCII.
        Assert.Equal(
            CurrencyKey.Canonical("XPM"),
            CurrencyKey.Canonical("00000000000000000000000058504d0000000000"));
    }

    [Fact]
    public void TheAllZeroFortyCharacterFormCanonicalizesToXrp()
    {
        // This is how the ledger itself encodes XRP in a 160-bit currency field: all zero bytes, because
        // the field has no meaning for the native asset. Left uncaught, it would fall through to the
        // issued-currency branch and never match "XRP".
        Assert.Equal("XRP", CurrencyKey.Canonical("0000000000000000000000000000000000000000"));
    }

    [Fact]
    public void ANonStandardHexCodeIsKeptAsUppercaseHex()
    {
        string hex = "534F4C4F00000000000000000000000000000000";

        Assert.Equal(hex, CurrencyKey.Canonical(hex.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOOLONG")]
    [InlineData("AB")]
    [InlineData("00000000000000000000000058504D00000000")]
    public void AnythingThatIsNotACurrencyCodeIsRejected(string currency)
    {
        Assert.Throws<ArgumentException>(() => CurrencyKey.Canonical(currency));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CurrencyKey.Canonical(null!));
    }
}
