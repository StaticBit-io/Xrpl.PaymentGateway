# Xrpl.PaymentGateway

Accept XRP Ledger payments in any .NET application, and record them wherever you like.

The library watches one receiving account, hands each buyer a stable destination tag, and records every
incoming payment exactly once. Where those records live is your decision: implement one interface and the
gateway writes through it, whether that is PostgreSQL, a file, or something else entirely.

## Install

```
dotnet add package Xrpl.PaymentGateway
```

Implement storage against `Xrpl.PaymentGateway.Abstractions`, which has no dependency on the XRPL SDK.

## Use

```csharp
builder.Services.AddSingleton<IPaymentStore, MyPostgresPaymentStore>();
builder.Services.AddSingleton<IPaymentReceivedHandler, MyOrderActivator>();

builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = "rYourReceivingAddress";
    options.Nodes =
    [
        new Uri("wss://xrplcluster.com"),
        new Uri("wss://s1.ripple.com"),
    ];
});
```

Issue instructions when a buyer reaches checkout:

```csharp
PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
```

React when the money arrives:

```csharp
public sealed class MyOrderActivator : IPaymentReceivedHandler
{
    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken ct)
    {
        // Called at least once per payment. Make this idempotent.
        return activations.ActivateAsync(buyerId, payment.Value, ct);
    }
}
```

## What it guarantees

- **Exactly once in the store.** Records are keyed by transaction hash, and the store rejects duplicates.
- **At least once to your handler.** Delivery is retried by reconciliation until it succeeds, so handlers
  must be idempotent.
- **No gaps across disconnects.** A persisted ledger cursor marks the boundary below which completeness is
  proven. Every reconnect subscribes first, then replays `account_tx` from the cursor. If no node can prove
  a range, the cursor freezes rather than skipping it.
- **Amounts as delivered.** Values come from transaction metadata balance changes, not the `Amount` field,
  so a partial payment is recorded at what actually arrived.

## What it expects of the receiving account

Use a dedicated account that only receives. Specifically, it must not have `DefaultRipple` enabled and
should not hold DEX offers or AMM positions. A transaction that both credits and debits the account is
treated as an exchange rather than a payment: it is refused, logged as an error, and counted in the health
report rather than credited to a buyer.

## Health and reconciliation

Call these from whatever scheduler you already run — Hangfire, Quartz, a timer:

```csharp
PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);   // cheap, call often
ReconciliationResult result = await health.ReconcileAsync(cancellationToken);     // slower, call hourly
```

`ReconcileAsync` redelivers anything the handler never accepted and re-verifies a window of ledgers below
the cursor. A non-zero `RecoveredCount` means a payment was missing from the store: investigate, because
the monitor should never let that happen.

## Operational notes

- Run **one** monitor instance per receiving account. A second one produces no duplicate records, but it is
  not a supported configuration.
- Destination tags are allocated by your store, not by the library. `PaymentGatewayOptions.FirstDestinationTag`
  is the value to hand your store; the library validates it but cannot apply it on the store's behalf.
- Catch-up refuses nodes whose `complete_ledgers` does not cover the range it needs. If the health report
  says `HistoryGap`, add a full-history node to `CatchUpNodes`.
- A network-wide consensus stall is reported as `NetworkStalled` rather than treated as a node failure. No
  payments are lost: nothing is being validated while the network is stopped.

## Development

The test project is an executable, not a `dotnet test` target: xunit.v3 runs on Microsoft Testing Platform,
and the .NET 10 SDK's `dotnet test` still routes through VSTest, which refuses to run it.

```
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"
```

Integration tests need a standalone node on `ws://localhost:6006`:

```
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait "Category=Integration"
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

The stand publishes the same ports as the XrplCSharp CI stand, so only one of the two can run at a time.
If a healthy standalone node is already listening there, the tests use it and you can skip the compose step
entirely; when nothing is listening, they skip themselves rather than fail.

## Sample

`samples/Xrpl.PaymentGateway.SampleApi` is a minimal ASP.NET Core host wiring the whole surface together:
`POST /checkout/{buyerId}` for instructions, `GET /payments` for what the handler received, `GET /recorded`
for what the store holds, `GET /health`, and `POST /reconcile`. Point `Xrpl:Address` at a receiving account
and run it.
