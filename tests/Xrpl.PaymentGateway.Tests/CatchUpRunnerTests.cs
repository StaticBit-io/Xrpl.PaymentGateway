using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class CatchUpRunnerTests
{
    private static readonly Uri Node = new Uri("ws://node:6006");

    private static AccountTransactionPage Page(uint min, uint max, object? marker, params IAccountTransaction[] transactions) =>
        new AccountTransactionPage
        {
            Transactions = transactions,
            Marker = marker,
            LedgerIndexMin = min,
            LedgerIndexMax = max,
        };

    [Fact]
    public async Task AnEmptyRangeCompletesWithoutTouchingTheNode()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 200, 100, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ProcessedCount);
        Assert.Equal(0, connection.StatusCalls);
    }

    [Fact]
    public async Task EveryTransactionInEveryPageReachesTheSink()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        connection.EnqueuePage(Page(100, 200, "marker-1", TransactionFixtures.Parse(TransactionFixtures.XrpPayment)));
        connection.EnqueuePage(Page(100, 200, null, TransactionFixtures.Parse(TransactionFixtures.IouPayment)));
        List<IAccountTransaction> seen = new List<IAccountTransaction>();
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (tx, _) => { seen.Add(tx); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.Equal(2, result.ProcessedCount);
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, connection.Queries.Count);
        Assert.Null(connection.Queries[0].Marker);
        Assert.Equal("marker-1", connection.Queries[1].Marker);
    }

    [Fact]
    public async Task ANodeWhoseHistoryDoesNotCoverTheRangeIsRefusedBeforeAnyQuery()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "300-500" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Contains("300-500", result.Reason);
        Assert.Empty(connection.Queries);
    }

    [Fact]
    public async Task ANodeThatSearchedANarrowerRangeThanAskedIsRefused()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        connection.EnqueuePage(Page(150, 200, null));
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Contains("150", result.Reason);
    }

    [Fact]
    public async Task AnUnparseableCompleteLedgersStringIsRefused()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "empty" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
    }

    [Fact]
    public async Task TheQueryCarriesTheRequestedBoundsAndTheAccount()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        await runner.RunAsync(connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        AccountTransactionQuery query = Assert.Single(connection.Queries);
        Assert.Equal("rAccount", query.Account);
        Assert.Equal(100u, query.LedgerIndexMin);
        Assert.Equal(200u, query.LedgerIndexMax);
    }
}
