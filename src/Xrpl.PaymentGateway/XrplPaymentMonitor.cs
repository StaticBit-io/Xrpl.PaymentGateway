using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

// Both Xrpl.Models.Methods and System.Threading.Channels define a type named Channel, so the bare name
// is ambiguous (CS0104) on the static CreateBounded call. The generic Channel<T> resolves by arity.
using Channel = System.Threading.Channels.Channel;

namespace Xrpl.PaymentGateway;

/// <summary>
/// Follows the receiving account and records what arrives. One session at a time, one node at a time;
/// every session starts by subscribing, then replaying whatever happened while it was away.
/// </summary>
/// <remarks>
/// Internal because its constructor takes internal types (<c>MonitorSnapshot</c>,
/// <c>IXrplNodeConnectionFactory</c>) and a public constructor may not expose less accessible types
/// (CS0051). Hosts reach it through <see cref="IHostedService"/>; tests see it via InternalsVisibleTo.
/// </remarks>
internal sealed class XrplPaymentMonitor : BackgroundService
{
    private readonly PaymentGatewayOptions _options;
    private readonly IXrplNodeConnectionFactory _connectionFactory;
    private readonly IPaymentStore _store;
    private readonly MonitorSnapshot _snapshot;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<XrplPaymentMonitor> _logger;
    private readonly NodePool _pool;
    private readonly TransactionProcessor _processor;
    private readonly PaymentDispatcher _dispatcher;
    private readonly CatchUpRunner _catchUp;
    private readonly StoreRetryPolicy _storeRetry;

    private uint _persistedCursor;

    /// <summary>
    /// Set when a catch-up could not be proven complete. While it holds a value, the cursor is frozen:
    /// advancing it past an unverified range would turn a visible gap into a permanent, invisible one.
    /// </summary>
    private uint? _unprovenFromLedger;

    /// <summary>The state to restore once the store comes back.</summary>
    private PaymentMonitorState _stateBeforeStoreOutage = PaymentMonitorState.Connecting;

    public XrplPaymentMonitor(
        IOptions<PaymentGatewayOptions> options,
        IXrplNodeConnectionFactory connectionFactory,
        IPaymentStore store,
        IPaymentReceivedHandler handler,
        MonitorSnapshot snapshot,
        TimeProvider timeProvider,
        ILogger<XrplPaymentMonitor> logger)
    {
        _options = options.Value;
        _connectionFactory = connectionFactory;
        _store = store;
        _snapshot = snapshot;
        _timeProvider = timeProvider;
        _logger = logger;
        _pool = new NodePool(_options.Nodes);
        _processor = new TransactionProcessor(_options.Address, timeProvider, logger);
        _dispatcher = new PaymentDispatcher(store, handler, logger);
        _catchUp = new CatchUpRunner(logger);
        _storeRetry = new StoreRetryPolicy(
            _options.StoreRetryBaseDelay,
            _options.StoreRetryMaxDelay,
            timeProvider,
            logger,
            available =>
            {
                if (!available)
                {
                    _stateBeforeStoreOutage = _snapshot.Read().State;
                    _snapshot.SetState(PaymentMonitorState.StoreUnavailable);
                }
                else if (_snapshot.Read().State == PaymentMonitorState.StoreUnavailable)
                {
                    // Without this the health report keeps claiming the store is down long after it came back.
                    _snapshot.SetState(_stateBeforeStoreOutage);
                }
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SessionEndedException ex)
            {
                _logger.LogWarning("node session ended: {Reason}", ex.Message);
                attempt = 0;
            }
            catch (Exception ex)
            {
                attempt++;
                _snapshot.SetError(ex.Message);
                _logger.LogError(ex, "payment monitor session failed");
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            _snapshot.SetState(PaymentMonitorState.Reconnecting);

            try
            {
                await Task.Delay(ReconnectDelay(attempt), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _snapshot.SetState(PaymentMonitorState.Stopped);
        _snapshot.SetNode(null);
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        Uri node = _pool.Next();
        _snapshot.SetNode(node);
        _snapshot.SetState(PaymentMonitorState.Connecting);

        using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationToken sessionToken = sessionCts.Token;

        Channel<MonitorEvent> channel = Channel.CreateBounded<MonitorEvent>(
            new BoundedChannelOptions(_options.StreamBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        await using IXrplNodeConnection connection = _connectionFactory.Create(node);

        connection.OnTransaction = transaction =>
        {
            Publish(channel, MonitorEvent.ForTransaction(transaction));
            return Task.CompletedTask;
        };
        connection.OnLedgerClosed = ledgerIndex =>
        {
            Publish(channel, MonitorEvent.ForLedger(ledgerIndex));
            return Task.CompletedTask;
        };
        connection.OnSessionEnded = reason =>
        {
            channel.Writer.TryComplete(new SessionEndedException(reason));
            return Task.CompletedTask;
        };

        try
        {
            await connection.ConnectAsync(sessionToken).ConfigureAwait(false);
            await connection.SubscribeToAccountAsync(_options.Address, sessionToken).ConfigureAwait(false);

            uint validated = await PrimeCursorAsync(connection, sessionToken).ConfigureAwait(false);
            await StreamAsync(connection, channel.Reader, validated, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            connection.OnTransaction = null;
            connection.OnLedgerClosed = null;
            connection.OnSessionEnded = null;
            channel.Writer.TryComplete();
            await sessionCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Publish(Channel<MonitorEvent> channel, MonitorEvent monitorEvent)
    {
        if (!channel.Writer.TryWrite(monitorEvent))
        {
            channel.Writer.TryComplete(new StreamBufferOverflowException(_options.StreamBufferCapacity));
        }
    }

    /// <summary>Loads the cursor, replays anything missed, and returns the node's validated ledger.</summary>
    private async Task<uint> PrimeCursorAsync(IXrplNodeConnection connection, CancellationToken cancellationToken)
    {
        uint? storedCursor = await _storeRetry.ExecuteAsync(
            token => _store.GetLastProcessedLedgerAsync(token), "GetLastProcessedLedger", cancellationToken).ConfigureAwait(false);

        NodeStatus status = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.ValidatedLedgerIndex is not { } validated)
        {
            throw new InvalidOperationException($"node {connection.Node} reports no validated ledger");
        }

        _snapshot.SetValidatedLedger(validated, _timeProvider.GetUtcNow());

        uint cursor = storedCursor ?? _options.StartLedgerIndex ?? validated;
        _persistedCursor = cursor;
        _snapshot.SetCursor(cursor);

        if (storedCursor is null)
        {
            // Written directly rather than through PersistCursorAsync, whose "already at or past this"
            // guard would swallow it. Without this write, a restart before the first ledger close would
            // start from the current validated ledger again and skip whatever arrived in between.
            await _storeRetry.ExecuteAsync(
                token => _store.SetLastProcessedLedgerAsync(cursor, token),
                "SetLastProcessedLedger",
                cancellationToken).ConfigureAwait(false);
        }

        if (cursor >= validated)
        {
            _unprovenFromLedger = null;
            _snapshot.SetState(PaymentMonitorState.Streaming);
            return validated;
        }

        _snapshot.SetState(PaymentMonitorState.CatchingUp);
        CatchUpResult result = await CatchUpAsync(connection, cursor + 1, validated, cancellationToken).ConfigureAwait(false);

        if (result.Completed)
        {
            _unprovenFromLedger = null;
            await PersistCursorAsync(validated, cancellationToken).ConfigureAwait(false);
            _snapshot.SetState(PaymentMonitorState.Streaming);
        }
        else
        {
            // The cursor must stay put. Letting it follow the live stream would move the proven-completeness
            // boundary past ledgers nobody ever searched, and the next restart would never look at them
            // again — a visible gap silently becoming a permanent one.
            _unprovenFromLedger = cursor + 1;
            _logger.LogError(
                "catch-up over ledgers {From}-{To} could not be proven complete on any node: {Reason}. The cursor stays frozen at {Cursor} and new payments are still recorded; add a full-history node to close the gap.",
                cursor + 1,
                validated,
                result.Reason,
                cursor);
            _snapshot.SetState(PaymentMonitorState.HistoryGap);
        }

        return validated;
    }

    /// <summary>Tries the live node first, then any dedicated catch-up nodes, until one proves the range.</summary>
    private async Task<CatchUpResult> CatchUpAsync(
        IXrplNodeConnection primary,
        uint fromLedger,
        uint toLedger,
        CancellationToken cancellationToken)
    {
        CatchUpResult result = await _catchUp
            .RunAsync(primary, _options.Address, fromLedger, toLedger, ProcessTransactionAsync, cancellationToken)
            .ConfigureAwait(false);

        if (result.Completed)
        {
            return result;
        }

        _logger.LogWarning("catch-up on {Node} was not usable: {Reason}", primary.Node, result.Reason);

        foreach (Uri candidate in _options.EffectiveCatchUpNodes)
        {
            if (candidate == primary.Node)
            {
                continue;
            }

            try
            {
                await using IXrplNodeConnection fallback = _connectionFactory.Create(candidate);
                await fallback.ConnectAsync(cancellationToken).ConfigureAwait(false);

                CatchUpResult attempt = await _catchUp
                    .RunAsync(fallback, _options.Address, fromLedger, toLedger, ProcessTransactionAsync, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Completed)
                {
                    return attempt;
                }

                _logger.LogWarning("catch-up on {Node} was not usable: {Reason}", candidate, attempt.Reason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "catch-up attempt against {Node} failed", candidate);
            }
        }

        return result;
    }

    private async Task StreamAsync(
        IXrplNodeConnection connection,
        ChannelReader<MonitorEvent> reader,
        uint validatedAtStart,
        CancellationToken cancellationToken)
    {
        DateTimeOffset lastProgress = _timeProvider.GetUtcNow();
        uint lastSeenValidated = validatedAtStart;
        Task<bool>? wait = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            // The pending wait is carried across iterations: creating a second one while the first is
            // still outstanding would break the channel's SingleReader contract.
            wait ??= reader.WaitToReadAsync(cancellationToken).AsTask();

            TimeSpan idleBudget = _snapshot.Read().State == PaymentMonitorState.NetworkStalled
                ? _options.NetworkStallProbeInterval
                : _options.LedgerStallTimeout;

            Task completed = await Task.WhenAny(
                wait,
                Task.Delay(idleBudget, _timeProvider, cancellationToken)).ConfigureAwait(false);

            if (completed != wait)
            {
                if (_timeProvider.GetUtcNow() - lastProgress < idleBudget)
                {
                    continue;
                }

                bool networkStalled = await IsNetworkStalledAsync(connection, lastSeenValidated, cancellationToken).ConfigureAwait(false);
                if (!networkStalled)
                {
                    _logger.LogWarning(
                        "node {Node} produced no ledger for {Timeout}; rotating to the next node",
                        connection.Node,
                        idleBudget);
                    return;
                }

                _logger.LogWarning(
                    "no ledger for {Timeout} and every reachable node is synced at ledger {Ledger}; treating this as a network-wide stall",
                    idleBudget,
                    lastSeenValidated);
                _snapshot.SetState(PaymentMonitorState.NetworkStalled);
                lastProgress = _timeProvider.GetUtcNow();
                continue;
            }

            bool hasData = await wait.ConfigureAwait(false);
            wait = null;

            if (!hasData)
            {
                return;
            }

            while (reader.TryRead(out MonitorEvent monitorEvent))
            {
                if (monitorEvent.Kind == MonitorEventKind.Transaction)
                {
                    await ProcessTransactionAsync(monitorEvent.Transaction!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (monitorEvent.LedgerIndex is 0 or > uint.MaxValue)
                {
                    continue;
                }

                uint closed = (uint)monitorEvent.LedgerIndex;
                lastSeenValidated = closed;
                lastProgress = _timeProvider.GetUtcNow();
                _snapshot.SetValidatedLedger(closed, lastProgress);

                if (_unprovenFromLedger is not null)
                {
                    // An unproven range is still open behind us. Keep recording live payments, but neither
                    // advance the cursor nor let the state read as healthy.
                    continue;
                }

                _snapshot.SetState(PaymentMonitorState.Streaming);
                await PersistCursorAsync(closed - 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A stall is the network's fault only when the nodes themselves are healthy and stuck at the same place.
    /// Anything else — an unsynced node, an unreachable peer — is a local problem and means rotate.
    /// </summary>
    private async Task<bool> IsNetworkStalledAsync(
        IXrplNodeConnection connection,
        uint lastSeenValidated,
        CancellationToken cancellationToken)
    {
        NodeStatus current;

        try
        {
            current = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "server_info failed on {Node} while classifying a stall", connection.Node);
            return false;
        }

        if (!current.IsSynced || current.ValidatedLedgerIndex > lastSeenValidated)
        {
            return false;
        }

        // Every other node in the pool gets asked, not just the next one. With three or more nodes, two
        // stuck peers would otherwise outvote a third that is still advancing, and the monitor would sit
        // on a lagging node calling it a network outage.
        foreach (Uri candidate in _pool.Nodes)
        {
            if (candidate == connection.Node)
            {
                continue;
            }

            try
            {
                await using IXrplNodeConnection probe = _connectionFactory.Create(candidate);
                await probe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                NodeStatus other = await probe.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);

                if (other.IsSynced && other.ValidatedLedgerIndex > current.ValidatedLedgerIndex)
                {
                    _logger.LogInformation(
                        "node {Other} is ahead at ledger {Ahead} while {Node} sits at {Behind}; this is a node problem, not a network one",
                        candidate,
                        other.ValidatedLedgerIndex,
                        connection.Node,
                        current.ValidatedLedgerIndex);
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "probing {Node} for a second opinion failed", candidate);
            }
        }

        return true;
    }

    private async Task ProcessTransactionAsync(IAccountTransaction transaction, CancellationToken cancellationToken)
    {
        ProcessingResult result = _processor.Process(transaction);

        if (result.Kind == ProcessingResultKind.Anomaly)
        {
            _snapshot.IncrementAnomaly();
        }

        if (result.Record is not { } record)
        {
            return;
        }

        bool isNew = await _storeRetry
            .ExecuteAsync(token => _dispatcher.RecordAsync(record, token), "TryAddPayment", cancellationToken)
            .ConfigureAwait(false);

        if (isNew)
        {
            await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistCursorAsync(uint cursor, CancellationToken cancellationToken)
    {
        if (cursor <= _persistedCursor)
        {
            return;
        }

        await _storeRetry.ExecuteAsync(
            token => _store.SetLastProcessedLedgerAsync(cursor, token), "SetLastProcessedLedger", cancellationToken).ConfigureAwait(false);

        _persistedCursor = cursor;
        _snapshot.SetCursor(cursor);
    }

    private TimeSpan ReconnectDelay(int attempt)
    {
        if (attempt <= 0)
        {
            return _options.ReconnectBaseDelay;
        }

        double exponent = Math.Min(attempt - 1, 16);
        double milliseconds = Math.Min(
            _options.ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, exponent),
            _options.ReconnectMaxDelay.TotalMilliseconds);
        double jittered = milliseconds * (0.75 + (Random.Shared.NextDouble() * 0.5));

        return TimeSpan.FromMilliseconds(Math.Min(jittered, _options.ReconnectMaxDelay.TotalMilliseconds));
    }
}
