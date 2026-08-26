using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>What the monitor knows about itself, readable by the health service from another thread.</summary>
internal sealed class MonitorSnapshot
{
    private readonly object _gate = new object();
    private PaymentMonitorState _state = PaymentMonitorState.Stopped;
    private string? _node;
    private string? _lastError;
    private uint? _lastValidatedLedger;
    private uint? _cursor;
    private DateTimeOffset? _lastLedgerAt;
    private long _anomalyCount;

    public void SetState(PaymentMonitorState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    public void SetNode(Uri? node)
    {
        lock (_gate)
        {
            _node = node?.ToString();
        }
    }

    public void SetCursor(uint cursor)
    {
        lock (_gate)
        {
            _cursor = cursor;
        }
    }

    public void SetValidatedLedger(uint ledgerIndex, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_lastValidatedLedger is null || ledgerIndex > _lastValidatedLedger)
            {
                _lastValidatedLedger = ledgerIndex;
            }

            _lastLedgerAt = at;
        }
    }

    public void SetError(string error)
    {
        lock (_gate)
        {
            _lastError = error;
        }
    }

    public void IncrementAnomaly() => Interlocked.Increment(ref _anomalyCount);

    public MonitorSnapshotData Read()
    {
        lock (_gate)
        {
            return new MonitorSnapshotData
            {
                State = _state,
                Node = _node,
                LastError = _lastError,
                LastValidatedLedger = _lastValidatedLedger,
                Cursor = _cursor,
                LastLedgerAt = _lastLedgerAt,
                AnomalyCount = Interlocked.Read(ref _anomalyCount),
            };
        }
    }
}

/// <summary>An immutable copy of <see cref="MonitorSnapshot"/>.</summary>
internal sealed class MonitorSnapshotData
{
    public required PaymentMonitorState State { get; init; }

    public string? Node { get; init; }

    public string? LastError { get; init; }

    public uint? LastValidatedLedger { get; init; }

    public uint? Cursor { get; init; }

    public DateTimeOffset? LastLedgerAt { get; init; }

    public required long AnomalyCount { get; init; }
}
