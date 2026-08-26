namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Where a buyer must send funds, and under which destination tag.</summary>
public sealed class PaymentInstructions
{
    /// <summary>The receiving r-address.</summary>
    public required string Address { get; init; }

    /// <summary>The tag assigned to this buyer. Stable across calls.</summary>
    public required uint DestinationTag { get; init; }
}
