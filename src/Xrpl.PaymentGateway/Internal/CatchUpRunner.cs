using Microsoft.Extensions.Logging;
using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Replays <c>account_tx</c> over a ledger range. A node with a hole in its history answers a range query
/// with a silently partial result, so this checks twice: <c>complete_ledgers</c> before asking, and the
/// range the node echoes back after asking.
/// </summary>
internal sealed class CatchUpRunner
{
    private const int PageSize = 200;

    private readonly ILogger _logger;

    public CatchUpRunner(ILogger logger) => _logger = logger;

    public async Task<CatchUpResult> RunAsync(
        IXrplNodeConnection connection,
        string account,
        uint fromLedger,
        uint toLedger,
        Func<IAccountTransaction, CancellationToken, Task> sink,
        CancellationToken cancellationToken)
    {
        if (fromLedger > toLedger)
        {
            return CatchUpResult.Complete(0);
        }

        NodeStatus status = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!LedgerRangeSet.TryParse(status.CompleteLedgers, out LedgerRangeSet history)
            || !history.Covers(fromLedger, toLedger))
        {
            return CatchUpResult.Incomplete(
                $"node {connection.Node} does not hold ledgers {fromLedger}-{toLedger} (complete_ledgers: {status.CompleteLedgers ?? "none"})");
        }

        object? marker = null;
        int processed = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            AccountTransactionPage page = await connection.GetAccountTransactionsAsync(
                new AccountTransactionQuery
                {
                    Account = account,
                    LedgerIndexMin = fromLedger,
                    LedgerIndexMax = toLedger,
                    Limit = PageSize,
                    Marker = marker,
                },
                cancellationToken).ConfigureAwait(false);

            if (page.LedgerIndexMin > fromLedger || page.LedgerIndexMax < toLedger)
            {
                return CatchUpResult.Incomplete(
                    $"node {connection.Node} searched ledgers {page.LedgerIndexMin}-{page.LedgerIndexMax}, narrower than the requested {fromLedger}-{toLedger}");
            }

            foreach (IAccountTransaction transaction in page.Transactions)
            {
                await sink(transaction, cancellationToken).ConfigureAwait(false);
                processed++;
            }

            marker = page.Marker;
        }
        while (marker is not null);

        _logger.LogInformation(
            "catch-up over ledgers {From}-{To} on {Node} replayed {Count} transactions",
            fromLedger,
            toLedger,
            connection.Node,
            processed);

        return CatchUpResult.Complete(processed);
    }
}
