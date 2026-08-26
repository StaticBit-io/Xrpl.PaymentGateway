using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.Utils;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Turns a validated transaction into a <see cref="PaymentRecord"/>, or explains why it is not one.
/// The amount comes from metadata balance deltas rather than the Amount field, so partial payments are
/// recorded at what actually arrived.
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

    public ProcessingResult Process(IAccountTransaction? transaction)
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
            return ProcessingResult.Skip("no balance change for the receiving account");
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

        if (debited)
        {
            string reason = $"transaction {hash} both credits and debits the receiving account; it is an exchange or a rippling path, not an incoming payment, and was not recorded";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(null, reason);
        }

        if (credits.Count == 0)
        {
            return ProcessingResult.Skip("no positive balance change");
        }

        (Currency currency, decimal value) = credits.OrderByDescending(candidate => candidate.Value).First();
        bool isXrp = currency.IsXrp();

        PaymentRecord record = new PaymentRecord
        {
            TransactionHash = hash,
            TransactionType = tx.TransactionType.ToString(),
            Sender = tx.Account,
            DestinationTag = ReadDestinationTag(tx),
            Currency = isXrp ? "XRP" : currency.CurrencyCode,
            Issuer = isXrp ? null : currency.Issuer,
            Value = value,
            LedgerIndex = (uint)ledgerIndex,
            ProcessedAt = _timeProvider.GetUtcNow(),
        };

        if (credits.Count > 1)
        {
            string reason = $"transaction {hash} credited {credits.Count} assets; recorded the largest ({record.Value} {record.Currency})";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(record, reason);
        }

        return ProcessingResult.Recorded(record);
    }

    /// <summary>XRP deltas arrive in drops on <c>ValueAsNumber</c>; <c>ValueAsXrp</c> is the human amount.</summary>
    private static decimal ToHumanUnits(Currency currency) =>
        currency.IsXrp() ? currency.ValueAsXrp ?? 0m : currency.ValueAsNumber;

    /// <summary>
    /// DestinationTag lives on Payment, not on the base transaction. The extension-data fallback keeps
    /// tags readable on transaction types the SDK maps to the generic response.
    /// </summary>
    private static uint? ReadDestinationTag(TransactionResponse tx)
    {
        if (tx is IPayment payment && payment.DestinationTag is { } tag)
        {
            return tag;
        }

        if (tx.UnknownFields is { } unknown
            && unknown.TryGetValue("DestinationTag", out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetUInt32(out uint raw))
        {
            return raw;
        }

        return null;
    }
}
