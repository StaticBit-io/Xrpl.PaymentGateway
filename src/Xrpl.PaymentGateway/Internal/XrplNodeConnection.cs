using Microsoft.Extensions.Logging;
using Xrpl.Client;
using Xrpl.Models;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// One <see cref="XrplClient"/> session. The SDK's own reconnect loop is kept to a single attempt: node
/// rotation is the monitor's job, and two independent reconnect policies would fight each other.
/// </summary>
internal sealed class XrplNodeConnection : IXrplNodeConnection
{
    private readonly XrplClient _client;
    private readonly ILogger _logger;
    private string? _account;
    private int _disposed;

    public XrplNodeConnection(Uri node, ILogger logger)
    {
        Node = node;
        _logger = logger;
        _client = new XrplClient(
            node.ToString(),
            new XrplClient.ClientOptions
            {
                MaxReconnectAttempts = 1,
                StopAfterMaxAttempts = true,
                RequestPolicy = RequestFailurePolicy.ImmediateFail,
            });

        _client.OnTransaction += HandleTransactionAsync;
        _client.OnLedgerClosed += HandleLedgerClosedAsync;
        _client.OnSessionEnded += HandleSessionEndedAsync;
        _client.OnConnected += ResubscribeAsync;
    }

    public Uri Node { get; }

    public Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    public Func<ulong, Task>? OnLedgerClosed { get; set; }

    public Func<string, Task>? OnSessionEnded { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => _client.Connect(cancellationToken);

    public async Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken)
    {
        _account = account;

        // Subscribing by account delivers only this account's transactions. The transactions stream would
        // deliver every transaction on the network for the same information.
        await _client.Subscribe(
            new SubscribeRequest
            {
                Accounts = new List<string> { account },
                Streams = new List<StreamType> { StreamType.Ledger },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        XrplResponse<ServerInfo> response = await _client
            .ServerInfo(new ServerInfoRequest(), cancellationToken)
            .ConfigureAwait(false);

        Info info = response.Result.Info;

        return new NodeStatus
        {
            ServerState = info.ServerState.ToString().ToLowerInvariant(),
            ValidatedLedgerIndex = info.ValidatedLedger is { Sequence: > 0 } ledger ? (uint)ledger.Sequence : null,
            CompleteLedgers = info.CompleteLedgers,
        };
    }

    public async Task<AccountTransactionPage> GetAccountTransactionsAsync(
        AccountTransactionQuery query,
        CancellationToken cancellationToken)
    {
        AccountTransactionsRequest request = new AccountTransactionsRequest(query.Account)
        {
            LedgerIndexMin = (int)query.LedgerIndexMin,
            LedgerIndexMax = (int)query.LedgerIndexMax,
            Limit = query.Limit,
            Marker = query.Marker,
            Forward = true,
        };

        XrplResponse<AccountTransactions> response = await _client
            .AccountTransactions(request, cancellationToken)
            .ConfigureAwait(false);

        AccountTransactions result = response.Result;

        return new AccountTransactionPage
        {
            Transactions = result.Transactions?.Cast<IAccountTransaction>().ToList() ?? new List<IAccountTransaction>(),
            Marker = result.Marker,
            LedgerIndexMin = result.LedgerIndexMin,
            LedgerIndexMax = result.LedgerIndexMax,
        };
    }

    private async Task ResubscribeAsync()
    {
        if (_account is not { } account)
        {
            return;
        }

        try
        {
            await SubscribeToAccountAsync(account, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("re-subscribed to {Account} on {Node} after the socket came back", account, Node);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "re-subscribing to {Account} on {Node} failed", account, Node);
        }
    }

    private Task HandleTransactionAsync(TransactionStream stream) =>
        OnTransaction?.Invoke(stream) ?? Task.CompletedTask;

    private Task HandleLedgerClosedAsync(LedgerStream stream) =>
        OnLedgerClosed?.Invoke(stream.LedgerIndex) ?? Task.CompletedTask;

    private Task HandleSessionEndedAsync(SessionEndReason reason, string description) =>
        OnSessionEnded?.Invoke($"{reason}: {description}") ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _client.OnTransaction -= HandleTransactionAsync;
        _client.OnLedgerClosed -= HandleLedgerClosedAsync;
        _client.OnSessionEnded -= HandleSessionEndedAsync;
        _client.OnConnected -= ResubscribeAsync;

        try
        {
            await _client.DisconnectAndWaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "disconnecting from {Node} was not clean", Node);
        }

        _client.Dispose();
    }
}

internal sealed class XrplNodeConnectionFactory : IXrplNodeConnectionFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public XrplNodeConnectionFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public IXrplNodeConnection Create(Uri node) =>
        new XrplNodeConnection(node, _loggerFactory.CreateLogger($"Xrpl.PaymentGateway.Node[{node}]"));
}
