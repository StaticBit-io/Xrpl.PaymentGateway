using Xrpl.Client;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// The sample's own connection to a node, shared by the parts of it that need to talk to one.
/// </summary>
/// <remarks>
/// Deliberately not the gateway's connection. The gateway owns its node, its reconnection policy and its
/// cursor, and reaching into that to borrow a socket would couple a demonstration to internals no host is
/// meant to touch. Opening a second connection is what any host would do.
/// </remarks>
public sealed class NodeConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly string _node;

    private XrplClient? _client;
    private bool _disposed;

    public NodeConnection(string node) => _node = node;

    /// <summary>A connected client, reconnecting if the last one dropped.</summary>
    public async Task<XrplClient> ClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is { } live && live.IsConnected())
        {
            return live;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Checked again inside the gate: several callers can arrive at a dropped connection at once,
            // and only the first of them should be the one to replace it.
            if (_client is { } current && current.IsConnected())
            {
                return current;
            }

            _client?.Dispose();
            _client = null;

            XrplClient client = new XrplClient(_node);
            await client.Connect(cancellationToken).ConfigureAwait(false);
            _client = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _gate.Dispose();
    }
}
