namespace Xrpl.PaymentGateway.Internal;

/// <summary>One page request against <c>account_tx</c>.</summary>
internal sealed class AccountTransactionQuery
{
    public required string Account { get; init; }

    public required uint LedgerIndexMin { get; init; }

    public required uint LedgerIndexMax { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>Opaque continuation token from the previous page, or null for the first page.</summary>
    public object? Marker { get; init; }
}
