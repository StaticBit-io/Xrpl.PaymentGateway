using System.Text.Json;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;

namespace Xrpl.PaymentGateway.Tests.Fixtures;

/// <summary>Canned rippled stream frames. Addresses are real-shaped so nothing chokes on address parsing.</summary>
public static class TransactionFixtures
{
    public const string Receiver = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
    public const string Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF";
    public const string Issuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";

    /// <summary>Deserializes a frame the way the SDK's own stream pipeline does.</summary>
    public static IAccountTransaction Parse(string json) =>
        JsonSerializer.Deserialize<TransactionStream>(json, XrplJsonOptions.Default)
        ?? throw new InvalidOperationException("fixture did not deserialize");

    /// <summary>1 XRP delivered to the receiver, destination tag 42.</summary>
    public const string XrpPayment = """
    {
      "type": "transaction",
      "engine_result": "tesSUCCESS",
      "ledger_index": 100,
      "hash": "1111111111111111111111111111111111111111111111111111111111111111",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "delivered_amount": "1000000",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "21000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "BBBB",
              "FinalFields": { "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF", "Balance": "29000000" },
              "PreviousFields": { "Balance": "30000012" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 42,
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 7
      }
    }
    """;

    /// <summary>A partial payment: the Amount field says 1 XRP, the ledger delivered 100 drops.</summary>
    public const string PartialXrpPayment = """
    {
      "type": "transaction",
      "ledger_index": 101,
      "hash": "2222222222222222222222222222222222222222222222222222222222222222",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "delivered_amount": "100",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "20000100" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 7,
        "Amount": "1000000",
        "Flags": 131072,
        "Fee": "12",
        "Sequence": 8
      }
    }
    """;

    /// <summary>100 USD delivered to the receiver, who is the low account on the trust line.</summary>
    public const string IouPayment = """
    {
      "type": "transaction",
      "ledger_index": 102,
      "hash": "3333333333333333333333333333333333333333333333333333333333333333",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "100" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 99,
        "Amount": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "100" },
        "Fee": "12",
        "Sequence": 3
      }
    }
    """;

    /// <summary>A failed transaction. Nothing moved, but the frame still arrives.</summary>
    public const string FailedPayment = """
    {
      "type": "transaction",
      "ledger_index": 103,
      "hash": "4444444444444444444444444444444444444444444444444444444444444444",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tecPATH_DRY",
        "AffectedNodes": []
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 9
      }
    }
    """;

    /// <summary>The receiving account sending funds out. Its own action, never an incoming payment.</summary>
    public const string OutgoingPayment = """
    {
      "type": "transaction",
      "ledger_index": 104,
      "hash": "5555555555555555555555555555555555555555555555555555555555555555",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "19000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "TransactionType": "Payment",
        "Destination": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 4
      }
    }
    """;

    /// <summary>Not yet validated. Provisional results must never be recorded.</summary>
    public const string UnvalidatedPayment = """
    {
      "type": "transaction",
      "ledger_current_index": 105,
      "hash": "6666666666666666666666666666666666666666666666666666666666666666",
      "validated": false,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "21000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 10
      }
    }
    """;

    /// <summary>The receiver gives up XRP and gains USD — an exchange, not a receipt.</summary>
    public const string ExchangeWithDebit = """
    {
      "type": "transaction",
      "ledger_index": 106,
      "hash": "7777777777777777777777777777777777777777777777777777777777777777",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "19000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "50" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "OfferCreate",
        "Fee": "12",
        "Sequence": 11
      }
    }
    """;

    /// <summary>Two assets credited at once. Physically odd for a receiving account, so it is an anomaly.</summary>
    public const string TwoAssetsCredited = """
    {
      "type": "transaction",
      "ledger_index": 107,
      "hash": "8888888888888888888888888888888888888888888888888888888888888888",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "20500000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "100" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "500000",
        "Fee": "12",
        "Sequence": 12
      }
    }
    """;

    /// <summary>Someone opens a trust line towards us. No balance moved.</summary>
    public const string TrustSetOnly = """
    {
      "type": "transaction",
      "ledger_index": 108,
      "hash": "9999999999999999999999999999999999999999999999999999999999999999",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "CreatedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "DDDD",
              "NewFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "0" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "100" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
        "TransactionType": "TrustSet",
        "Fee": "12",
        "Sequence": 5
      }
    }
    """;
}
