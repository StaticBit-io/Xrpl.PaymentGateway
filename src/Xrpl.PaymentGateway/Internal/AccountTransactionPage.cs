using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>One page of <c>account_tx</c> results, including the range the node actually searched.</summary>
internal sealed class AccountTransactionPage
{
    public required IReadOnlyList<IAccountTransaction> Transactions { get; init; }

    /// <summary>Continuation token, or null when this was the last page.</summary>
    public object? Marker { get; init; }

    /// <summary>Echoed lower bound. Higher than requested means the node searched less than we asked.</summary>
    public required uint LedgerIndexMin { get; init; }

    /// <summary>Echoed upper bound. Lower than requested means the node searched less than we asked.</summary>
    public required uint LedgerIndexMax { get; init; }
}
