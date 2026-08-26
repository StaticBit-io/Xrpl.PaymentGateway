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

    /// <summary>
    /// A payment addressed to us that also debits us — only possible if the account holds an offer or a
    /// rippling trust line it should not. The money is still ours, so it is recorded as an anomaly.
    /// </summary>
    public const string PaymentToUsWithDebit = """
    {
      "type": "transaction",
      "ledger_index": 109,
      "hash": "AAAA111111111111111111111111111111111111111111111111111111111111",
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
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "80" },
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
        "DestinationTag": 55,
        "Amount": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "80" },
        "Fee": "12",
        "Sequence": 21
      }
    }
    """;

    /// <summary>
    /// A payment to somebody else that ripples through the account. Our balances move, but the money is
    /// in transit and none of it is ours.
    /// </summary>
    public const string PaymentRipplingThroughUs = """
    {
      "type": "transaction",
      "ledger_index": 110,
      "hash": "BBBB222222222222222222222222222222222222222222222222222222222222",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "EEEE",
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
        "Destination": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
        "DestinationTag": 900,
        "Amount": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "100" },
        "Fee": "12",
        "Sequence": 22
      }
    }
    """;

    /// <summary>Our own offer being crossed by somebody else. Proceeds of a trade, not a buyer's payment.</summary>
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

    /// <summary>
    /// A payment whose trust-line balances are large enough to overflow decimal arithmetic. XRPL token
    /// values reach ~1e95 while decimal tops out near 7.9e28, and the SDK clamps an unparseable value to
    /// decimal.MaxValue before subtracting the previous balance — so the subtraction throws. Anyone who can
    /// route a payment through their own offers can put values like this in our metadata.
    /// </summary>
    public const string PoisonousAmounts = """
    {
      "type": "transaction",
      "ledger_index": 111,
      "hash": "CCCC333333333333333333333333333333333333333333333333333333333333",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "FFFF",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "9e80" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1e80" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "-9e80" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 77,
        "Amount": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "9e80" },
        "Fee": "12",
        "Sequence": 23
      }
    }
    """;

    /// <summary>
    /// A successful payment to us whose metadata credits nothing our balance reader understands — what an
    /// MPT payment looks like today, since the reader only walks AccountRoot and RippleState.
    /// </summary>
    public const string PaymentToUsWithNoReadableCredit = """
    {
      "type": "transaction",
      "ledger_index": 112,
      "hash": "DDDD444444444444444444444444444444444444444444444444444444444444",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": []
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 88,
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 24
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
