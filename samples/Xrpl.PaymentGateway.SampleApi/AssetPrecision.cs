using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// What "an amount that exists" means for one XRPL asset, and the two different ways the sample needs to
/// arrive at one.
/// </summary>
/// <remarks>
/// XRP's smallest unit is the drop, one millionth — a fixed six decimal places, always. An issued currency
/// carries no such fixed scale: the ledger stores it as up to fifteen significant digits of mantissa with
/// a separate exponent, so the constraint is on digit count, not decimal places, and it is unrelated to
/// XRP's rule. <see cref="Xrpl.PaymentGateway.Abstractions.QuoteResult"/> and
/// <see cref="Xrpl.PaymentGateway.Abstractions.PaymentValuation"/> carry plain <c>decimal</c> — the gateway
/// deliberately leaves rounding to the host, because only the host knows whether a given number is an ask
/// or a report. This type is that decision, made once, for this sample.
/// </remarks>
internal static class AssetPrecision
{
    private const int XrpDecimalPlaces = 6;
    private const int IssuedSignificantDigits = 15;

    /// <summary>
    /// What a checkout should ask a buyer to send. Rounds up: a buyer sends exactly the number displayed,
    /// so rounding down would make that number stop covering the invoice it was computed from, and the
    /// shop would be short by a fraction on every order that pays the exact figure shown. Rounding up
    /// instead costs the shop a dust-sized amount at worst — the safe side to be wrong on for a request.
    /// </summary>
    public static decimal RoundUpForAsk(decimal amount, string currency) =>
        Round(amount, currency, roundUp: true);

    /// <summary>
    /// What a payment turned out to be worth, or any other number the sample reports rather than requests.
    /// Rounds to the nearest expressible unit, not up — a report is a measurement of something that already
    /// happened, and biasing every measurement upward would misstate history instead of merely
    /// approximating it.
    /// </summary>
    public static decimal RoundNearestForReport(decimal amount, string currency) =>
        Round(amount, currency, roundUp: false);

    /// <summary>
    /// What the demo payer can actually put on the ledger. Shares the report rule rather than the ask one:
    /// the amount came from somebody who meant it, so the nearest expressible amount is the faithful
    /// reading of it, where rounding up would quietly send more than was asked for.
    /// </summary>
    public static decimal RoundNearestForSending(decimal amount, string currency) =>
        Round(amount, currency, roundUp: false);

    private static decimal Round(decimal amount, string currency, bool roundUp)
    {
        // CurrencyKey.Canonical is the same normalization the gateway itself uses to recognise XRP,
        // hex-encoded or not — reusing it here means this never drifts from what the library considers XRP.
        bool isXrp = string.Equals(CurrencyKey.Canonical(currency), "XRP", StringComparison.Ordinal);
        int decimalPlaces = isXrp ? XrpDecimalPlaces : DecimalPlacesForSignificantDigits(amount, IssuedSignificantDigits);

        return roundUp ? CeilToDecimalPlaces(amount, decimalPlaces) : Math.Round(amount, decimalPlaces, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How many decimal places keep <paramref name="amount"/> at <paramref name="significantDigits"/>
    /// significant digits — the ledger's own budget for an issued-currency amount, wherever its decimal
    /// point happens to fall.
    /// </summary>
    private static int DecimalPlacesForSignificantDigits(decimal amount, int significantDigits)
    {
        if (amount == 0m)
        {
            return 0;
        }

        int exponent = FloorLog10(Math.Abs(amount));
        int decimalPlaces = significantDigits - 1 - exponent;

        // Clamped rather than left to go negative or past what Math.Round accepts: a demo amount never
        // gets large enough to need truncating whole digits, so 0 is the sensible floor, and 28 is
        // Math.Round's own ceiling for decimal.
        return Math.Clamp(decimalPlaces, 0, 28);
    }

    /// <summary>
    /// floor(log10(absAmount)), computed by repeated multiplication/division in decimal rather than a
    /// double conversion — the latter can misjudge an exact power of ten (99.999999999996 instead of 100)
    /// and knock the digit budget off by one right where it matters most.
    /// </summary>
    private static int FloorLog10(decimal absAmount)
    {
        int exponent = 0;

        if (absAmount >= 1m)
        {
            decimal threshold = 10m;
            while (absAmount >= threshold)
            {
                threshold *= 10m;
                exponent++;
            }
        }
        else
        {
            decimal threshold = 1m;
            while (absAmount < threshold)
            {
                threshold /= 10m;
                exponent--;
            }
        }

        return exponent;
    }

    private static decimal CeilToDecimalPlaces(decimal amount, int decimalPlaces)
    {
        decimal factor = Pow10(decimalPlaces);
        return Math.Ceiling(amount * factor) / factor;
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
