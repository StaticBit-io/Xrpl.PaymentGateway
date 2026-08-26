using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class NodePoolTests
{
    private static readonly Uri NodeA = new Uri("ws://a:6006");
    private static readonly Uri NodeB = new Uri("ws://b:6006");
    private static readonly Uri NodeC = new Uri("ws://c:6006");

    [Fact]
    public void NextWalksTheNodesInOrderAndWrapsAround()
    {
        NodePool pool = new NodePool(new[] { NodeA, NodeB, NodeC });

        Assert.Equal(NodeA, pool.Next());
        Assert.Equal(NodeB, pool.Next());
        Assert.Equal(NodeC, pool.Next());
        Assert.Equal(NodeA, pool.Next());
    }

    [Fact]
    public void ASingleNodePoolKeepsReturningTheOneNode()
    {
        NodePool pool = new NodePool(new[] { NodeA });

        Assert.Equal(NodeA, pool.Next());
        Assert.Equal(NodeA, pool.Next());
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void NodesExposesThePoolForFanOutProbes()
    {
        NodePool pool = new NodePool(new[] { NodeA, NodeB, NodeC });

        Assert.Equal(new[] { NodeA, NodeB, NodeC }, pool.Nodes);
    }

    [Fact]
    public void AnEmptyPoolIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new NodePool(Array.Empty<Uri>()));
    }
}
