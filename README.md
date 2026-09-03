# Xrpl.PaymentGateway

Accept XRP Ledger payments in any .NET application, and record them wherever you like.

The library watches one receiving account, hands each buyer a stable destination tag, and records every
incoming payment exactly once. Where those records live is your decision: implement one interface and the
gateway writes through it, whether that is PostgreSQL, a file, or something else entirely.

## Install

```bash
dotnet add package Xrpl.PaymentGateway
```

Add `Xrpl.PaymentGateway.Postgres` as well if you want the PostgreSQL store. A project that only implements
storage takes `Xrpl.PaymentGateway.Abstractions` instead, which has no dependency on the XRPL SDK.

## Choosing a store

Where payments are recorded is your decision, and there are three ways to make it:

| Store | Where it lives | Use it when |
|---|---|---|
| `PostgresPaymentStore` | `Xrpl.PaymentGateway.Postgres` | You have a database. Tag allocation and hash uniqueness are enforced by Postgres, so they hold across processes and restarts. Call `EnsureSchemaAsync` on start; it is idempotent and is the only migration there is. |
| `FilePaymentStore` | `Xrpl.PaymentGateway` | You want no database. A single JSON file, rewritten atomically per write. One process only — which is the only supported way to run the monitor anyway. |
| `InMemoryPaymentStore` | `Xrpl.PaymentGateway` | Tests and demos. Everything is lost on restart, so the gateway would re-scan from the current ledger and miss whatever arrived while it was down. |

Writing your own is the fourth way, and the interface is small. Two requirements are not negotiable:
`GetOrAssignTagAsync` must be atomic, and `TryAddPaymentAsync` must enforce uniqueness of the transaction
hash and return false rather than throw on a duplicate. Both are easy to state and easy to get wrong, so
the test suite has a `PaymentStoreContract` class that every shipped store derives from — including the
concurrency and restart cases. Deriving from it is how a new store proves itself.

## Use

```csharp
// Any IPaymentStore will do. This one is from Xrpl.PaymentGateway.Postgres.
PostgresPaymentStore store = new PostgresPaymentStore(connectionString);
await store.EnsureSchemaAsync();

builder.Services.AddSingleton<IPaymentStore>(store);
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

Every setting, with its default and what happens when it is wrong, is in the
[configuration reference](https://github.com/StaticBit-io/Xrpl.PaymentGateway/blob/release/docs/configuration.md).

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
  a range, the cursor freezes rather than skipping it. A stream that skips ledger numbers, or a client whose
  inbound queue overflowed, ends the session rather than advancing the cursor across what it did not see.
- **Amounts as delivered.** Values come from transaction metadata balance changes, not the `Amount` field,
  so a partial payment is recorded at what actually arrived.

## What counts as a payment

Exactly one thing: a validated, successful **`Payment` whose `Destination` is the receiving account**, sent
by somebody else. Nothing else is recorded, even when it moves the account's balances — an offer of yours
being crossed on the DEX is trade proceeds, and a payment rippling through you to a third party is money in
transit. Neither is a buyer paying you, and neither carries a destination tag that means anything to you.

Other ways funds can reach an account — `CheckCash`, `EscrowFinish`, `PaymentChannelClaim` — are outside
this library's scope. They cannot be attributed to a buyer by destination tag, so if you accept them you
need to reconcile them yourself.

**MPT payments are not supported yet.** Amounts are derived from balance changes, and the reader behind
that walks XRP and trust-line entries only. An MPT payment to the account is not recorded — but it is not
lost quietly either: a successful payment addressed to you that credits nothing readable is logged as an
error and raises `AnomalyCount`.

## What it does not do

Deliberately out of scope, so that what is in scope can be relied on:

- **Outgoing payments, refunds, invoicing, fiat conversion.** This receives and records; everything after
  that is the host's.
- **More than one receiving account per instance.** Run an instance per account — the ledger cursor and
  the tag counter belong to one account each.
- **More than one monitor per account.** A second instance produces no duplicate records, because writes
  are keyed by transaction hash, but it is not a supported configuration.
- **MPT amounts.** See above: not recorded, but not lost quietly either.
- **A scheduler.** `CheckAsync` and `ReconcileAsync` are called by whatever the host already runs.

## Quotes and valuation

Optionally, the gateway keeps a liquidity reading for each asset you accept, prices any
size against it without touching the network, and records what each received payment was
worth in an asset of your choosing.

It does not compute prices itself — you supply an `IQuoteSource`, and the gateway owns the
refresh rhythm, the age policy and delivery. Valuation runs behind the payment path and
never on it: the payment is recorded and announced first, and its value arrives as a
second signal through `IPaymentValuedHandler`.

See the [quotes reference](https://github.com/StaticBit-io/Xrpl.PaymentGateway/blob/release/docs/quotes.md).

## What it expects of the receiving account

Use a dedicated account that only receives. Specifically, it must not have `DefaultRipple` enabled and
should not hold DEX offers or AMM positions.

If the account is itself the issuer of a token it accepts, a payment in that token is a redemption, and
the balance change the ledger reports for it names the sender as the issuer rather than the account. A
quote pair configured for that asset never matches it, so the payment is recorded but never valued. This
is a property of the protocol, not a defect here — issue tokens you accept from a separate account, not
the one this library watches.

If a payment addressed to you *also* debits the account, or credits two assets at once, the record is
still written — it is a buyer's money and dropping it would lose a real payment — but it is logged as an
error and increments `AnomalyCount` in the health report. Both shapes are physically impossible for an
account that only receives, so treat any rise in `AnomalyCount` as something to investigate rather than a
statistic: the usual cause is an offer or a rippling trust line the account should not have.

## Health and reconciliation

Call these from whatever scheduler you already run — Hangfire, Quartz, a timer:

```csharp
PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);   // cheap, call often
ReconciliationResult result = await health.ReconcileAsync(cancellationToken);     // slower, call hourly
```

`ReconcileAsync` redelivers anything the handler never accepted and re-verifies a window of ledgers below
the cursor. A non-zero `RecoveredCount` means a payment was missing from the store: investigate, because
the monitor should never let that happen.

**Schedule it more often than its own window.** `ReconcileWindow` defaults to 2000 ledgers, roughly two
hours; running reconciliation less often than that leaves ledgers no sweep ever covers. `RedeliveredCount`
counts only records that actually reached the handler — a handler that keeps failing shows up in `Errors`,
not as progress.

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

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"
```

Integration tests need the stand: a standalone node on `ws://localhost:6006` and, for the Postgres
store's contract tests, a database on `localhost:55432`. Both come up together, and each test skips itself
rather than failing when its dependency is missing.

They build a small economy on the ledger — a receiving account, two token issuers, two buyers — and then
pay it in XRP and in an issued currency. That takes about a minute and a half of closed ledgers, most of
it setup. The two issuers exist because a trust line's sides are ordered by comparing account ids, and
which side the receiving account lands on changes the shape of the metadata a payment produces; one issuer
would test whichever case the random addresses happened to give.

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait "Category=Integration"
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

By default the stand publishes the same ports as the XrplCSharp CI stand, so only one of the two can run;
[CONTRIBUTING.md](https://github.com/StaticBit-io/Xrpl.PaymentGateway/blob/release/CONTRIBUTING.md) shows how
to move this one's ports so both can.
If a healthy standalone node is already listening there, the tests use it and you can skip the compose step
entirely; when nothing is listening, they skip themselves rather than fail.

[CONTRIBUTING.md](https://github.com/StaticBit-io/Xrpl.PaymentGateway/blob/release/CONTRIBUTING.md) has the rest: test filters, how to prove a new payment store against the
shared contract, code style, and the release process.

## Sample

`samples/Xrpl.PaymentGateway.SampleApi` is a minimal API with a checkout page in front of it — the whole
surface, end to end, in something you can click. No build step and no package manager: the page is three
static files, so `dotnet run` is the entire toolchain.

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
Xrpl__Address=rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh dotnet run --project samples/Xrpl.PaymentGateway.SampleApi
```

Open the printed URL. Enter a buyer id, take the address and destination tag, and pay. The page polls
until the gateway reports the payment; the strip along the top shows the monitor's own state, so a
reconnect or a catch-up is visible rather than looking like nothing happening. The page's own
"sending from the standalone stand" section prints the calls to pay yourself with no wallet.

The QR code carries an **X-address**, not a bare account: one string holding the address and the
destination tag together. A scanner given a classic address drops the tag, and a payment without the tag
lands on the account attached to nobody. The X-address also encodes which network it is for — the sample
flags it as test, matching the standalone stand, so set `Xrpl:IsTestNetwork` to `false` before pointing it
at mainnet or wallets there will refuse the code. Test X-addresses begin with `T`, mainnet ones with `X`,
which makes a mistake visible at a glance.

`Xrpl:Address` above is the standalone stand's master account, which is convenient for a demo because it
already exists and is funded. For anything else, point it at your own receiving account.

| Endpoint | |
|---|---|
| `POST /api/checkout/{buyerId}` | Address, destination tag, and an X-address carrying both |
| `GET /api/checkout/{buyerId}/qr.svg` | The X-address as a scannable QR code |
| `GET /api/checkout/{buyerId}/price` | What this checkout is priced at, in the quote asset, and how old that reading is. Empty until a quote pair is configured |
| `GET /api/checkout/{buyerId}/payments` | What this buyer has paid. The page polls this |
| `GET /api/checkout/{buyerId}/valuations` | What this buyer's payments turned out to be worth, a second and later signal than the payment itself |
| `GET /api/payments` | Everything the handler has been given |
| `GET /api/valuations` | Every valuation the handler has been given, whichever state it landed in |
| `GET /api/recorded` | Everything the store holds, when the store offers a snapshot |
| `GET /api/health` | The monitor's state; 503 when it is not streaming |
| `POST /api/reconcile` | Redeliver and re-verify on demand |
| `GET /api/quotes/health` | Pair freshness, refresh failures, pending valuations and the age of the oldest of them. 404 when no quote pairs are configured; 503 when configured but not healthy |
| `GET /api/quotes/unresolved` | Payments the automatic pipeline has not resolved, for an operator to act on |
| `POST /api/quotes/unresolved/{transactionHash}/settle` | Price one unresolved payment by hand, at a rate supplied in the body |
| `POST /api/quotes/unresolved/{transactionHash}/write-off` | Close one unresolved payment with no quote amount, recording why |
| `GET /api/demo` | The demo wallet's address and the assets it may be asked to send. 404 when no demo seed is configured |
| `POST /api/checkout/{buyerId}/pay` | Pay this checkout from the demo wallet. 404 when no demo seed is configured |

Set `Xrpl:StorePath` to keep payments in a file instead of memory, and they survive a restart.

### Quotes and valuation in the sample

The demo shop takes payment in three things, all valued in USD: XRP and one issued token (`GEM` below, a
placeholder), each priced into USD through its own pair, and USD itself, accepted directly. Set
`Xrpl:Quotes:QuoteCurrency`/`Xrpl:Quotes:QuoteIssuer` and `Xrpl:Quotes:Pairs` and the page grows a price at
checkout, a valuation that appears on a payment row a few seconds after the payment itself, a quote health
strip beside the monitor strip, and an "Unresolved payments" section for whatever the automatic pipeline
could not price. Leave it empty, the shipped default, and the sample runs exactly as it does without the
feature — `AddXrplPaymentQuotes` is never even called.

```json
"Xrpl": {
  "Quotes": {
    "QuoteCurrency": "USD",
    "QuoteIssuer": "rUsdIssuerAddress",
    "Pairs": [
      { "Currency": "XRP", "Rate": 0.55 },
      { "Currency": "GEM", "Issuer": "rGemIssuerAddress", "Rate": 1.10 }
    ],
    "RefusedCurrencies": []
  }
}
```

`Rate` is quote-asset units per unit of the received asset; every pair shares the one
`QuoteCurrency`/`QuoteIssuer` above them, because the sample values everything a buyer can pay with in the
one asset a real shop would price its catalog in. USD is the third accepted asset and deliberately has no
entry in `Pairs`: `QuotePair` rejects quoting an asset against itself, so there is no USD/USD pair and
cannot be one — a payment already in USD needs no conversion, and the library queues no valuation for it.
The sample handles that itself rather than asking the library to: `SamplePaymentHandler` checks whether a
payment's currency and issuer are the configured `QuoteCurrency`/`QuoteIssuer`, and the page shows such a
payment at its own amount, labelled as needing no conversion, instead of a row waiting forever for a
valuation that was never going to arrive.

The source behind the two priced pairs is `FixedRateQuoteSource`, a deliberate stand-in the sample ships
with — fixed rates read straight from configuration, no network call at all. **Nothing about this prices
anything real**; a real `IQuoteSource` reads liquidity off the ledger, and a real host brings its own
pricing engine, the same way it brings its own `IPaymentStore`. `Xrpl:Quotes:RefusedCurrencies` names
currencies that source throws for instead of pricing, which is what gives the unresolved queue and the
settle/write-off buttons something genuine to act on in a demo that otherwise always prices cleanly.

### Paying from the page

Set `Xrpl:Demo:PayerSeed` to the seed of a funded account on the same network and the "Send the payment"
step grows a row per accepted asset — an amount, prefilled with what the checkout asks for, and a button
that signs the payment and submits it. It is the shell snippet the page already prints, moved onto the
page, so a full demonstration needs one terminal instead of two.

```json
"Xrpl": {
  "Demo": {
    "PayerSeed": "sXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
  }
}
```

Leave it empty, the shipped default, and neither the endpoints nor the buttons exist — the page waits for
money sent from somewhere else, exactly as before.

**A seed in configuration is a private key in a text file.** Use it against a standalone stand or a test
network and nothing else. The endpoint is narrow on purpose — it can only pay the address and destination
tag the gateway itself just issued for the buyer being checked out, in an asset the sample is configured to
accept, and the caller names neither — but that narrowness protects the *destination*, not the seed.

To pay in an issued currency, the demo payer needs a trust line to its issuer and a balance on it, and the
receiving account needs a trust line for the same currency, or the payment is rejected by the ledger before
the gateway ever sees it. On a standalone stand that means creating an issuer, enabling `DefaultRipple` on
it so its token can move between two holders, opening the trust lines, and funding the payer.
