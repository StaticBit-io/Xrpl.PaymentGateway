using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentDispatcherTests
{
    private static PaymentRecord Record(string hash = "HASH-1", uint? tag = 1) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = tag,
        Currency = "XRP",
        Value = 5m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ANewRecordIsStoredAndReported()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);

        bool isNew = await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.True(isNew);
    }

    [Fact]
    public async Task ADuplicateRecordIsReportedAsNotNew()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);
        await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        bool isNew = await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.False(isNew);
    }

    [Fact]
    public async Task AStoreFailureOnRecordPropagatesSoTheCallerCanRetry()
    {
        FlakyPaymentStore store = new FlakyPaymentStore(new InMemoryPaymentStore(), failures: 1);
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryResolvesTheBuyerFromTheDestinationTagAndMarksTheRecordHandled()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        uint tag = await store.GetOrAssignTagAsync("buyer-9", TestContext.Current.CancellationToken);
        RecordingHandler handler = new RecordingHandler();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: tag);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Equal("buyer-9", Assert.Single(handler.Deliveries).BuyerId);
        Assert.Empty(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APaymentWithAnUnknownTagIsStillDeliveredWithoutABuyer()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: 555);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Deliveries).BuyerId);
    }

    [Fact]
    public async Task AHandlerExceptionLeavesTheRecordUnhandledAndDoesNotEscape()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler { Throws = true };
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: null);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Single(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }
}
