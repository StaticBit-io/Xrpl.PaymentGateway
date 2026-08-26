namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// One incoming payment credited to the receiving account. <see cref="TransactionHash"/> is the unique key.
/// </summary>
public sealed class PaymentRecord
{
    /// <summary>Transaction hash. Unique key of the record.</summary>
    public required string TransactionHash { get; init; }

    /// <summary>XRPL transaction type that produced the credit, e.g. "Payment".</summary>
    public required string TransactionType { get; init; }

    /// <summary>Sending account. Never equal to the receiving address.</summary>
    public required string Sender { get; init; }

    /// <summary>Destination tag carried by the transaction, or null when it carried none.</summary>
    public uint? DestinationTag { get; init; }

    /// <summary>"XRP", or the issued currency code (3 characters or 40 hex characters).</summary>
    public required string Currency { get; init; }

    /// <summary>Issuer of the received token. Null for XRP.</summary>
    public string? Issuer { get; init; }

    /// <summary>Amount in human units: XRP in XRP (not drops), tokens in their own units.</summary>
    public required decimal Value { get; init; }

    /// <summary>Index of the validated ledger the transaction was included in.</summary>
    public required uint LedgerIndex { get; init; }

    /// <summary>When the library recorded the payment.</summary>
    public required DateTimeOffset ProcessedAt { get; init; }
}
