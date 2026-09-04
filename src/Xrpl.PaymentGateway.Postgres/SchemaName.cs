using System.Text.RegularExpressions;

namespace Xrpl.PaymentGateway.Postgres;

/// <summary>
/// Guards the one value that reaches SQL as an identifier rather than a parameter.
/// </summary>
/// <remarks>
/// A schema name cannot be parameterised, so it is quoted into every statement. Anything but a plain
/// identifier could break out of that quoting, so nothing else is accepted.
/// </remarks>
internal static class SchemaName
{
    private static readonly Regex Pattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled);

    public static void Validate(string schema, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema, parameterName);

        if (!Pattern.IsMatch(schema))
        {
            throw new ArgumentException(
                $"schema \"{schema}\" must be a plain SQL identifier: a letter or underscore followed by letters, digits or underscores.",
                parameterName);
        }
    }
}
