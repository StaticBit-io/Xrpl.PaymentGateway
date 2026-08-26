using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void PaymentRecordCarriesTheFieldsTheSpecRequires()
    {
        PaymentRecord record = new PaymentRecord
        {
            TransactionHash = "F00D",
            TransactionType = "Payment",
            Sender = "rSender",
            DestinationTag = 42,
            Currency = "XRP",
            Issuer = null,
            Value = 1.5m,
            LedgerIndex = 100,
            ProcessedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal("F00D", record.TransactionHash);
        Assert.Equal(42u, record.DestinationTag);
        Assert.Equal(1.5m, record.Value);
    }
}
