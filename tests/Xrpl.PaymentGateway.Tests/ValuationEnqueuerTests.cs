using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ValuationEnqueuerTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static readonly QuotePair Xpm = new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentRecord Payment() => new PaymentRecord
    {
        TransactionHash = "HASH1",
        TransactionType = "Payment",
        Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        DestinationTag = 42,
        Currency = "XPM",
        Issuer = XpmIssuer,
        Value = 1000m,
        LedgerIndex = 901,
        ProcessedAt = DateTimeOffset.UtcNow,
    };

    private static ValuationEnqueuer Build(TimeSpan storeTimeout, IQuoteStore store) =>
        new ValuationEnqueuer(
            Options.Create(new QuoteOptions { Pairs = new[] { Xpm }, StoreTimeout = storeTimeout }),
            store,
            new QuoteRegistry(new[] { Xpm }),
            TimeProvider.System,
            NullLogger.Instance);

    [Fact]
    public async Task AHangingStoreDoesNotBlockTheEnqueueBeyondItsTimeout()
    {
        // EnqueueAsync sits on the payment path, between the payment being stored and its receipt being
        // announced. IQuoteStore is host-implemented, and a store that merely hangs — never throwing,
        // never completing — must not be able to stall that path indefinitely. It never throws either:
        // a timeout is swallowed exactly like any other store failure.
        ValuationEnqueuer enqueuer = Build(TimeSpan.FromMilliseconds(100), new HangingQuoteStore());

        DateTime startedAt = DateTime.UtcNow;
        await enqueuer.EnqueueAsync(Payment(), Ct);
        TimeSpan elapsed = DateTime.UtcNow - startedAt;

        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"expected the hung enqueue to be abandoned near its 100ms timeout, but it took {elapsed}");
    }

    [Fact]
    public async Task CancellationFromTheCallerStillPropagatesThroughAHangingEnqueue()
    {
        // The timeout is implemented with a linked CancellationTokenSource; its own
        // OperationCanceledException must not be mistaken for — and swallow — a real caller-driven
        // cancellation such as host shutdown.
        ValuationEnqueuer enqueuer = Build(TimeSpan.FromSeconds(30), new HangingQuoteStore());
        using CancellationTokenSource callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enqueuer.EnqueueAsync(Payment(), callerCts.Token));
    }
}
