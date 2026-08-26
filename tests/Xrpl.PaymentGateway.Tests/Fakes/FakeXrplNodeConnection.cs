using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// A scriptable stand-in for a node session. Tests drive the callbacks by hand.
/// This must be internal: its members expose <see cref="NodeStatus"/> and the query types, which are
/// internal, and a public member may not expose a less accessible type (CS0050/CS0053).
/// </summary>
internal sealed class FakeXrplNodeConnection : IXrplNodeConnection
{
    private readonly Queue<AccountTransactionPage> _pages = new Queue<AccountTransactionPage>();

    public FakeXrplNodeConnection(Uri node) => Node = node;

    public Uri Node { get; }

    public long DroppedStreamMessages { get; set; }

    public Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    public Func<ulong, Task>? OnLedgerClosed { get; set; }

    public Func<string, Task>? OnSessionEnded { get; set; }

    public NodeStatus Status { get; set; } = new NodeStatus
    {
        ServerState = "full",
        ValidatedLedgerIndex = 1000,
        CompleteLedgers = "1-1000",
    };

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public string? SubscribedAccount { get; private set; }

    public int StatusCalls { get; private set; }

    public List<AccountTransactionQuery> Queries { get; } = new List<AccountTransactionQuery>();

    public Exception? ConnectFailure { get; set; }

    public void EnqueuePage(AccountTransactionPage page) => _pages.Enqueue(page);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Connected = true;
        return Task.CompletedTask;
    }

    public Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken)
    {
        SubscribedAccount = account;
        return Task.CompletedTask;
    }

    public Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        StatusCalls++;
        return Task.FromResult(Status);
    }

    public Task<AccountTransactionPage> GetAccountTransactionsAsync(AccountTransactionQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (_pages.Count == 0)
        {
            return Task.FromResult(new AccountTransactionPage
            {
                Transactions = Array.Empty<IAccountTransaction>(),
                Marker = null,
                LedgerIndexMin = query.LedgerIndexMin,
                LedgerIndexMax = query.LedgerIndexMax,
            });
        }

        return Task.FromResult(_pages.Dequeue());
    }

    /// <summary>Pushes a transaction the way a live subscription would.</summary>
    public Task PushTransactionAsync(IAccountTransaction transaction) =>
        OnTransaction?.Invoke(transaction) ?? Task.CompletedTask;

    /// <summary>Pushes a ledger close the way the ledger stream would.</summary>
    public Task PushLedgerAsync(ulong ledgerIndex) =>
        OnLedgerClosed?.Invoke(ledgerIndex) ?? Task.CompletedTask;

    /// <summary>Ends the session the way a dropped socket would.</summary>
    public Task EndSessionAsync(string reason) =>
        OnSessionEnded?.Invoke(reason) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Connected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hands out pre-built fakes per node URI, creating one on first request. Reuse across sessions is
/// deliberate: a test that rotates nodes and comes back needs the same instance to still answer.
/// </summary>
internal sealed class FakeXrplNodeConnectionFactory : IXrplNodeConnectionFactory
{
    private readonly Dictionary<Uri, FakeXrplNodeConnection> _connections = new Dictionary<Uri, FakeXrplNodeConnection>();

    public List<Uri> CreatedFor { get; } = new List<Uri>();

    public FakeXrplNodeConnection For(Uri node)
    {
        lock (_connections)
        {
            if (!_connections.TryGetValue(node, out FakeXrplNodeConnection? connection))
            {
                connection = new FakeXrplNodeConnection(node);
                _connections[node] = connection;
            }

            return connection;
        }
    }

    public IXrplNodeConnection Create(Uri node)
    {
        lock (CreatedFor)
        {
            CreatedFor.Add(node);
        }

        return For(node);
    }
}
