using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.Models.Subscriptions;
using Xrpl.Models.Transactions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class TransactionProcessorTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static TransactionProcessor CreateProcessor() =>
        new TransactionProcessor(TransactionFixtures.Receiver, new FixedTimeProvider(Now), NullLogger.Instance);

    private static ProcessingResult Process(string fixture) =>
        CreateProcessor().Process(TransactionFixtures.Parse(fixture));

    [Fact]
    public void AnXrpPaymentIsRecordedInXrpNotDrops()
    {
        ProcessingResult result = Process(TransactionFixtures.XrpPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        PaymentRecord record = Assert.IsType<PaymentRecord>(result.Record);
        Assert.Equal("XRP", record.Currency);
        Assert.Null(record.Issuer);
        Assert.Equal(1m, record.Value);
        Assert.Equal(TransactionFixtures.Sender, record.Sender);
        Assert.Equal(42u, record.DestinationTag);
        Assert.Equal(100u, record.LedgerIndex);
        Assert.Equal("Payment", record.TransactionType);
        Assert.Equal(Now, record.ProcessedAt);
    }

    [Fact]
    public void APartialPaymentIsRecordedAtWhatTheLedgerDeliveredNotWhatAmountClaimed()
    {
        ProcessingResult result = Process(TransactionFixtures.PartialXrpPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        Assert.Equal(0.0001m, result.Record!.Value);
    }

    [Fact]
    public void AnIouPaymentCarriesTheCurrencyAndIssuer()
    {
        ProcessingResult result = Process(TransactionFixtures.IouPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        PaymentRecord record = result.Record!;
        Assert.Equal("USD", record.Currency);
        Assert.Equal(TransactionFixtures.Issuer, record.Issuer);
        Assert.Equal(100m, record.Value);
        Assert.Equal(99u, record.DestinationTag);
    }

    [Fact]
    public void AFailedTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.FailedPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
        Assert.Null(result.Record);
    }

    [Fact]
    public void OurOwnOutgoingTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.OutgoingPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void AnUnvalidatedTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.UnvalidatedPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void ATrustSetThatMovesNoBalanceIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.TrustSetOnly);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void OurOwnOfferBeingCrossedIsNotAPaymentAndIsSkipped()
    {
        // An OfferCreate by somebody else moves our balances, but the proceeds are a trade, not a payment.
        ProcessingResult result = Process(TransactionFixtures.ExchangeWithDebit);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
        Assert.Null(result.Record);
    }

    [Fact]
    public void APaymentRipplingThroughUsToSomebodyElseIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.PaymentRipplingThroughUs);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
        Assert.Null(result.Record);
    }

    [Fact]
    public void APaymentAddressedToUsThatAlsoDebitsUsIsRecordedAsAnAnomaly()
    {
        // Once the filter has established this is a Payment addressed to us, the money is a buyer's and
        // dropping it would lose a real payment — so it is recorded, loudly.
        ProcessingResult result = Process(TransactionFixtures.PaymentToUsWithDebit);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        PaymentRecord record = Assert.IsType<PaymentRecord>(result.Record);
        Assert.Equal("USD", record.Currency);
        Assert.Equal(80m, record.Value);
        Assert.Equal(55u, record.DestinationTag);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void TwoCreditedAssetsAreAnAnomalyAndTheLargestIsStillRecorded()
    {
        ProcessingResult result = Process(TransactionFixtures.TwoAssetsCredited);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        PaymentRecord record = Assert.IsType<PaymentRecord>(result.Record);
        Assert.Equal("USD", record.Currency);
        Assert.Equal(100m, record.Value);
    }

    [Fact]
    public void AProcessorForADifferentAddressSeesNothing()
    {
        TransactionProcessor processor = new TransactionProcessor(
            TransactionFixtures.Issuer, new FixedTimeProvider(Now), NullLogger.Instance);

        ProcessingResult result = processor.Process(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void ANullTransactionIsSkippedRatherThanThrowing()
    {
        ProcessingResult result = CreateProcessor().Process(null!);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void AnAmountBeyondWhatDecimalHoldsIsReportedRatherThanThrown()
    {
        // The SDK raises AmountOutOfRangeException here by design: XRPL issued currency reaches ~1e96 and
        // decimal stops near 7.9e28. An unguarded call would be replayed by every catch-up and every
        // restart, wedging the monitor on one transaction forever.
        ProcessingResult result = Process(TransactionFixtures.PoisonousAmounts);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        Assert.Null(result.Record);

        // The offending amount reaches the operator, which only happens through the typed catch.
        Assert.Contains("9e80", result.Reason);
    }

    [Fact]
    public void ASuccessfulPaymentToUsThatCreditsNothingReadableIsAnAnomalyNotASilentSkip()
    {
        ProcessingResult result = Process(TransactionFixtures.PaymentToUsWithNoReadableCredit);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        Assert.Null(result.Record);
        Assert.Contains("not recorded", result.Reason);
    }

    [Fact]
    public void APaymentWhoseBodyCouldNotBeReadIsAnAnomalyNotSomebodyElsesPayment()
    {
        // What the SDK's converter hands back when the transaction body throws a JsonException: the right
        // shape, every field at its default. Skipping it as "not addressed to us" would lose a real payment.
        TransactionStream stream = new TransactionStream
        {
            Validated = true,
            Hash = "EEEE555555555555555555555555555555555555555555555555555555555555",
            LedgerIndex = 113,
            Meta = new Meta { TransactionResult = "tesSUCCESS", AffectedNodes = new List<AffectedNode>() },
            Transaction = new PaymentResponse { Account = TransactionFixtures.Sender },
        };

        ProcessingResult result = CreateProcessor().Process(stream);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        Assert.Null(result.Record);
        Assert.Contains("Destination", result.Reason);
    }
}
