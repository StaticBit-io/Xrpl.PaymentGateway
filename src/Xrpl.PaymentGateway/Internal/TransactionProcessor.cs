using Microsoft.Extensions.Logging;
using Xrpl.Models;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.Utils;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Turns a validated transaction into a <see cref="PaymentRecord"/>, or explains why it is not one.
/// Only a <c>Payment</c> addressed to the receiving account qualifies — everything else that moves the
/// account's balances (an offer of ours being crossed, a payment rippling through us to somebody else)
/// is not money a buyer sent us. The amount comes from metadata balance deltas rather than the Amount
/// field, so partial payments are recorded at what actually arrived.
/// </summary>
internal sealed class TransactionProcessor
{
    private const string Success = "tesSUCCESS";

    private readonly string _receivingAddress;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public TransactionProcessor(string receivingAddress, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receivingAddress);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _receivingAddress = receivingAddress;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Never throws. A transaction that makes this blow up would be replayed by every catch-up and every
    /// restart, so an unguarded exception here would wedge the monitor permanently on one bad transaction
    /// — and the metadata it parses is written by whoever built the payment path, not by us.
    /// </summary>
    public ProcessingResult Process(IAccountTransaction? transaction)
    {
        try
        {
            return Evaluate(transaction);
        }
        catch (Exception ex)
        {
            string reason = $"transaction {transaction?.Hash ?? "(no hash)"} could not be analysed: {ex.Message}";
            _logger.LogError(ex, "payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(null, reason);
        }
    }

    private ProcessingResult Evaluate(IAccountTransaction? transaction)
    {
        if (transaction is null)
        {
            return ProcessingResult.Skip("no transaction");
        }

        if (!transaction.Validated)
        {
            return ProcessingResult.Skip("not validated");
        }

        TransactionResponse? tx = transaction.Transaction;
        if (tx is null)
        {
            return ProcessingResult.Skip("no transaction body");
        }

        Meta? meta = transaction.Meta;
        if (meta is null)
        {
            return ProcessingResult.Skip("no metadata");
        }

        if (!string.Equals(meta.TransactionResult, Success, StringComparison.Ordinal))
        {
            return ProcessingResult.Skip($"transaction result {meta.TransactionResult}");
        }

        if (string.Equals(tx.Account, _receivingAddress, StringComparison.Ordinal))
        {
            return ProcessingResult.Skip("sent by the receiving account itself");
        }

        if (tx is not IPayment payment)
        {
            if (tx.TransactionType == TransactionType.Payment)
            {
                // A Payment that did not deserialize into the Payment shape would be skipped silently and
                // take every payment with it, so say so loudly instead.
                _logger.LogError(
                    "transaction {Hash} is a Payment but deserialized as {Type}; it cannot be read and was skipped",
                    transaction.Hash ?? "(no hash)",
                    tx.GetType().Name);
            }

            return ProcessingResult.Skip($"{tx.TransactionType} is not a Payment");
        }

        if (string.IsNullOrEmpty(payment.Destination))
        {
            // Every on-ledger Payment carries a Destination. An empty one means the SDK's converter hit a
            // JsonException and handed back a blank object, which would otherwise be skipped below as
            // "somebody else's payment" — losing a real one silently.
            string reason = $"payment {transaction.Hash} has no Destination; its body could not be read and it was not recorded";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(null, reason);
        }

        // Only payments addressed to us count. A payment that merely ripples through the account on its way
        // somewhere else also moves our balances, but none of it is ours to keep.
        if (!string.Equals(payment.Destination, _receivingAddress, StringComparison.Ordinal))
        {
            return ProcessingResult.Skip("the receiving account is not the destination");
        }

        string? hash = transaction.Hash;
        if (string.IsNullOrEmpty(hash))
        {
            return ProcessingResult.Skip("no transaction hash");
        }

        if (transaction.LedgerIndex is not { } ledgerIndex || ledgerIndex == 0 || ledgerIndex > uint.MaxValue)
        {
            return ProcessingResult.Skip("no usable ledger index");
        }

        Dictionary<string, List<Currency>> changes = BalanceChanges.GetBalanceChanges(meta);
        if (!changes.TryGetValue(_receivingAddress, out List<Currency>? ours) || ours.Count == 0)
        {
            return NothingCredited(transaction, "the metadata shows no balance change for the receiving account");
        }

        List<(Currency Currency, decimal Value)> credits = new List<(Currency, decimal)>();
        bool debited = false;
        foreach (Currency change in ours)
        {
            decimal delta = ToHumanUnits(change);
            if (delta > 0m)
            {
                credits.Add((change, delta));
            }
            else if (delta < 0m)
            {
                debited = true;
            }
        }

        if (credits.Count == 0)
        {
            return NothingCredited(transaction, "the metadata shows no credit to the receiving account");
        }

        (Currency currency, decimal value) = credits.OrderByDescending(candidate => candidate.Value).First();
        bool isXrp = currency.IsXrp();

        PaymentRecord record = new PaymentRecord
        {
            TransactionHash = hash,
            TransactionType = tx.TransactionType.ToString(),
            Sender = tx.Account,
            DestinationTag = payment.DestinationTag,
            Currency = isXrp ? "XRP" : currency.CurrencyCode,
            Issuer = isXrp ? null : currency.Issuer,
            Value = value,
            LedgerIndex = (uint)ledgerIndex,
            ProcessedAt = _timeProvider.GetUtcNow(),
        };

        // Both of the following are physically odd for an account that only receives, and both point at
        // the same misconfiguration: an offer or a rippling trust line the account should not have. The
        // record is still written — this is a payment addressed to us, and dropping it would lose a real
        // buyer's money — but the anomaly counter makes it something an operator has to look at.
        if (debited)
        {
            string reason = $"payment {hash} is addressed to the receiving account but also debits it; recorded the largest credit ({record.Value} {record.Currency})";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(record, reason);
        }

        if (credits.Count > 1)
        {
            string reason = $"payment {hash} credited {credits.Count} assets; recorded the largest ({record.Value} {record.Currency})";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(record, reason);
        }

        return ProcessingResult.Recorded(record);
    }

    /// <summary>
    /// A successful Payment addressed to us always delivered something, so finding no credit means the
    /// amount was in a form the balance-change reader does not understand — MPT issuances, which it does
    /// not walk at all — or the metadata failed to parse. Either way a buyer's money went unrecorded, and
    /// that must not pass quietly.
    /// </summary>
    private ProcessingResult NothingCredited(IAccountTransaction transaction, string detail)
    {
        string reason = $"payment {transaction.Hash} is addressed to the receiving account and succeeded, but {detail}; it was not recorded";
        _logger.LogError("payment anomaly: {Reason}", reason);
        return ProcessingResult.Anomaly(null, reason);
    }

    /// <summary>XRP deltas arrive in drops on <c>ValueAsNumber</c>; <c>ValueAsXrp</c> is the human amount.</summary>
    private static decimal ToHumanUnits(Currency currency) =>
        currency.IsXrp() ? currency.ValueAsXrp ?? 0m : currency.ValueAsNumber;
}
