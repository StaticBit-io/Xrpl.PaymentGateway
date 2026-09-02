# Quotes and valuation

Optional. The gateway can keep a liquidity reading for each asset you accept, price a
given size against it, and record what each received payment was worth.

## What the gateway does and does not do

It does not compute prices. Order-book and AMM arithmetic is a subject of its own, and a
payment gateway that assumed one engine would be as wrong as one that assumed a database.
You supply an `IQuoteSource`; the gateway owns the refresh rhythm, the age policy, storage
and delivery.

## Wiring it up

```csharp
builder.Services.AddSingleton<IQuoteStore>(new PostgresQuoteStore(connectionString));
builder.Services.AddSingleton<IQuoteSource, MyLiquiditySource>();
builder.Services.AddSingleton<IPaymentValuedHandler, MyLedgerPoster>();

builder.Services.AddXrplPaymentQuotes(options =>
{
    // RLUSD is five characters, which the ledger cannot hold as a plain code, so it is
    // addressed by its forty-character hex form. Three-character codes may be written either way.
    options.Pairs = [new QuotePair("XPM", xpmIssuer, "524C555344000000000000000000000000000000", rlusdIssuer)];
});
```

`AddXrplPaymentGateway` is unaffected: without the call above, nothing changes.

## Settings

| Option | Type | Default | Meaning |
|---|---|---|---|
| `Pairs` | `IReadOnlyList<QuotePair>` | empty | Assets to price, and what to price them in. At least one. A currency written as a three-character code and as forty hex characters is one pair, not two. Anything longer than three characters — `RLUSD`, for instance — exists on the ledger only in its hex form, and must be written that way. |
| `RefreshInterval` | `TimeSpan` | 1 min | How often each pair is refreshed. |
| `MinimumPairStagger` | `TimeSpan` | 10 s | Floor on the gap between two pair refreshes. Pairs are spread evenly across the interval and never fired closer together than this. |
| `MaxQuoteAge` | `TimeSpan?` | 3 × interval | How old a reading may be and still be served. Must not be shorter than the interval. |
| `RefuseStaleQuotes` | `bool` | `true` | Whether a reading past that age is withheld rather than served with its age attached. |
| `ValuateWithFreshSnapshot` | `bool` | `false` | Whether each payment gets its own capture. Costs one network round trip per payment. |
| `CaptureTimeout` | `TimeSpan` | 30 s | How long one capture may run before it is abandoned. A source that hangs must not stall the pairs behind it. |
| `ValuationPollInterval` | `TimeSpan` | 5 s | How often the valuation queue is drained. |
| `ValuationBatchSize` | `int` | 50 | How many queued valuations are priced, and how many delivered, per pass. |

More pairs than the interval can hold at the minimum stagger is allowed: the cycle takes
longer than the interval rather than bunching up, and `CycleFitsInInterval` in the health
report says so.

## Reading a quote

```csharp
QuoteView? view = await reader.QuoteAsync("XPM", issuer, 1000m, QuoteDirection.ExactInput, ct);
```

`ExactInput` answers "this much arrived — what is it worth". `ExactOutput` answers "I need
this much of the quote asset — how much to ask for". Both are computed from the held
snapshot, so neither touches the network.

Every answer carries `CapturedAt`, `Age` and `IsStale`. That is deliberate: a price
computed from a stale book looks exactly as confident as a fresh one.

`QuoteResult` carries three things worth checking before trusting the number:

- `IsFullyFilled` — false means the venues ran dry and the size cannot actually trade.
- `BookTruncated` — the real book may run deeper than what was priced.
- `SlippagePercent` — how much worse the achieved price is than the marginal one.

## Valuation

A received payment in a configured asset is queued, priced, and handed to
`IPaymentValuedHandler`. This is a second, later signal than `IPaymentReceivedHandler`,
never a replacement: waiting for a price before announcing a payment would put liquidity
availability on the path of money arriving.

Delivery is at least once, so the handler must be idempotent. A payment that cannot be
priced yet stays queued and is retried; the money is already recorded either way.

## Health

| Field | Meaning |
|---|---|
| `ConfiguredPairs` / `PairsWithFreshQuote` | How many pairs hold a reading within the age limit. |
| `OldestQuoteAge` | Age of the oldest held reading. |
| `PairsFailing` / `MaxConsecutiveFailures` / `LastError` | Refresh failures. A pair that keeps failing serves its last good reading until it expires. |
| `PendingValuations` / `OldestPendingAge` | Queue depth waiting to be priced. A backlog is normal; a growing age is not. |
| `UndeliveredValuations` / `OldestUndeliveredAge` | Priced but not yet accepted by your handler, and how long the oldest of those has been waiting. A count alone saturates at `ValuationBatchSize` once delivery falls behind, so a handler that has stopped accepting valuations can look like a queue that is draining; the age is what does not lie, and what to alert on. |
| `CycleFitsInInterval` | False means pairs refresh slower than configured. |
| `IsHealthy` | Every pair fresh, nothing failing, store readable. |
