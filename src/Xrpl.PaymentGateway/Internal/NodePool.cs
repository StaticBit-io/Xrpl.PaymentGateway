namespace Xrpl.PaymentGateway.Internal;

/// <summary>Round-robin over the allowed nodes. Not thread-safe; only the monitor loop touches it.</summary>
internal sealed class NodePool
{
    private readonly IReadOnlyList<Uri> _nodes;
    private int _index = -1;

    public NodePool(IReadOnlyList<Uri> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            throw new ArgumentException("at least one node is required", nameof(nodes));
        }

        _nodes = nodes;
    }

    public int Count => _nodes.Count;

    public IReadOnlyList<Uri> Nodes => _nodes;

    /// <summary>Advances to the next node and returns it.</summary>
    public Uri Next()
    {
        _index = (_index + 1) % _nodes.Count;
        return _nodes[_index];
    }
}
