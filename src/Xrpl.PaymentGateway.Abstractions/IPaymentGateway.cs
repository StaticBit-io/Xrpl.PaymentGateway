namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Issues payment instructions to buyers.</summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Returns the receiving address and the buyer's destination tag. A returning buyer always receives
    /// the tag assigned earlier.
    /// </summary>
    Task<PaymentInstructions> GetPaymentInstructionsAsync(string buyerId, CancellationToken cancellationToken);
}
