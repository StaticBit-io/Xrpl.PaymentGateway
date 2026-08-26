using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// One session against one node. Callbacks are single-consumer properties rather than events: the monitor
/// is the only consumer, and a multicast event would hide which subscriber's task is being awaited.
/// </summary>
internal interface IXrplNodeConnection : IAsyncDisposable
{
    Uri Node { get; }

    /// <summary>
    /// How many stream frames the client discarded because its inbound queue overflowed. The SDK's queue
    /// is bounded and drops the oldest frame, so a rise here means transactions were lost without an error.
    /// </summary>
    long DroppedStreamMessages { get; }

    /// <summary>Raised for every transaction affecting the subscribed account.</summary>
    Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    /// <summary>Raised with the index of each newly closed ledger.</summary>
    Func<ulong, Task>? OnLedgerClosed { get; set; }

    /// <summary>Raised once when the session is over and the subscriptions are gone.</summary>
    Func<string, Task>? OnSessionEnded { get; set; }

    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Subscribes to the account plus the ledger stream.</summary>
    Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken);

    Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken);

    Task<AccountTransactionPage> GetAccountTransactionsAsync(AccountTransactionQuery query, CancellationToken cancellationToken);
}

/// <summary>Creates a fresh session per node. The monitor disposes each one it opens.</summary>
internal interface IXrplNodeConnectionFactory
{
    IXrplNodeConnection Create(Uri node);
}
