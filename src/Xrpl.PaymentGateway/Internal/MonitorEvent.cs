using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

internal enum MonitorEventKind
{
    Transaction,
    Ledger,
}

/// <summary>
/// One item from the socket. Transactions and ledger closes share a queue so that the cursor can only
/// advance past a ledger after everything that arrived before its close has been processed.
/// </summary>
internal readonly struct MonitorEvent
{
    private MonitorEvent(MonitorEventKind kind, IAccountTransaction? transaction, ulong ledgerIndex)
    {
        Kind = kind;
        Transaction = transaction;
        LedgerIndex = ledgerIndex;
    }

    public MonitorEventKind Kind { get; }

    public IAccountTransaction? Transaction { get; }

    public ulong LedgerIndex { get; }

    public static MonitorEvent ForTransaction(IAccountTransaction transaction) =>
        new MonitorEvent(MonitorEventKind.Transaction, transaction, 0);

    public static MonitorEvent ForLedger(ulong ledgerIndex) =>
        new MonitorEvent(MonitorEventKind.Ledger, null, ledgerIndex);
}

/// <summary>Thrown into the event channel when the node session ends, unwinding the session loop.</summary>
internal sealed class SessionEndedException : Exception
{
    public SessionEndedException(string reason) : base(reason)
    {
    }
}

/// <summary>Thrown into the event channel when the buffer overflows and the session must be dropped.</summary>
internal sealed class StreamBufferOverflowException : Exception
{
    public StreamBufferOverflowException(int capacity)
        : base($"the stream buffer of {capacity} events overflowed; dropping the session so catch-up can recollect from the ledger")
    {
    }
}
