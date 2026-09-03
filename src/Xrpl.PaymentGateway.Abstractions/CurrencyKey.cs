using System.Text;

namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Brings a currency code to one form, so that the same asset written two different ways is one key.
/// </summary>
/// <remarks>
/// A standard three-character code and its forty-character hex form are the same currency on the ledger
/// but different strings. Without a canonical form a pair configured as "XPM" would never match a payment
/// the balance reader reported in hex — the quote would silently never be found.
/// </remarks>
public static class CurrencyKey
{
    /// <summary>The canonical form of a currency code: "XRP", or forty uppercase hex characters.</summary>
    /// <exception cref="ArgumentException">The code is neither XRP, three characters, nor forty hex characters.</exception>
    public static string Canonical(string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        string trimmed = currency.Trim();
        if (string.Equals(trimmed, "XRP", StringComparison.OrdinalIgnoreCase))
        {
            return "XRP";
        }

        if (trimmed.Length == 3)
        {
            StringBuilder hex = new StringBuilder(40);
            hex.Append('0', 24);
            foreach (char c in trimmed)
            {
                if (c > 0x7F)
                {
                    throw new ArgumentException($"currency \"{currency}\" must be ASCII", nameof(currency));
                }

                hex.Append(((int)c).ToString("X2"));
            }

            hex.Append('0', 10);
            return hex.ToString();
        }

        if (trimmed.Length == 40 && IsHex(trimmed))
        {
            // The ledger's own encoding of XRP in a 160-bit currency field is all zero bytes — the field
            // simply has no meaning for the native asset. Left uncaught here, that spelling falls through
            // to the issued-currency branch below: a pair configured this way could never match a payment,
            // and RequireIssuerConsistency, not recognising it as XRP, would demand an issuer XRP has none
            // of — letting an XRP pair carrying an issuer be constructed in the first place.
            return IsAllZero(trimmed) ? "XRP" : trimmed.ToUpperInvariant();
        }

        throw new ArgumentException(
            $"currency \"{currency}\" is neither XRP, a three-character code, nor forty hex characters",
            nameof(currency));
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            bool digit = c is >= '0' and <= '9';
            bool lower = c is >= 'a' and <= 'f';
            bool upper = c is >= 'A' and <= 'F';
            if (!digit && !lower && !upper)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZero(string value)
    {
        foreach (char c in value)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }
}
