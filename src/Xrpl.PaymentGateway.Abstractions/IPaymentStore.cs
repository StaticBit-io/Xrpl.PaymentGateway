namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Persistence the host provides. Postgres, a file, anything — the library never assumes a database.
/// </summary>
/// <remarks>
/// Two hard requirements on implementations:
/// <list type="bullet">
/// <item><description><see cref="GetOrAssignTagAsync"/> must be atomic. Two concurrent calls for the same new
/// buyer must return the same tag, and one tag must never reach two buyers.</description></item>
/// <item><description><see cref="TryAddPaymentAsync"/> must enforce uniqueness of
/// <see cref="PaymentRecord.TransactionHash"/> and return false on a duplicate rather than throwing.</description></item>
/// </list>
/// No transactionality across methods is required: the library writes the payment first and advances the
/// cursor afterwards, so a crash between the two causes an idempotent replay, never a loss.
/// </remarks>
public interface IPaymentStore
{
    /// <summary>Returns the buyer's existing tag, or atomically assigns the next one.</summary>
    Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken);

    /// <summary>Resolves a destination tag back to a buyer, or null when the tag was never issued.</summary>
    Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken);

    /// <summary>Inserts the record as unhandled. Returns false when the hash is already stored.</summary>
    Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken);

    /// <summary>Marks a stored payment as delivered to the host handler.</summary>
    Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> payments not yet marked handled, oldest first.</summary>
    Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The ledger boundary below which record completeness is proven, or null on a fresh store.</summary>
    Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken);

    /// <summary>Persists the completeness boundary.</summary>
    Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken);
}
