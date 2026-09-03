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
| `Pairs` | `IReadOnlyList<QuotePair>` | empty | Assets to price, and what to price them in. At least one. A currency written as a three-character code and as forty hex characters is one pair, not two. Anything longer than three characters — `RLUSD`, for instance — exists on the ledger only in its hex form, and must be written that way. Each received asset may appear in only one pair: a payment's asset is matched by currency and issuer alone, so two pairs naming the same asset against different quote currencies would validate and both refresh, yet only the first is ever reachable. |
| `RefreshInterval` | `TimeSpan` | 1 min | How often each pair is refreshed. |
| `MinimumPairStagger` | `TimeSpan` | 10 s | Floor on the gap between two pair refreshes. Pairs are spread evenly across the interval and never fired closer together than this. |
| `MaxQuoteAge` | `TimeSpan?` | 3 × interval | How old a reading may be and still be served. Must not be shorter than the interval. |
| `RefuseStaleQuotes` | `bool` | `true` | Whether a reading past that age is withheld rather than served with its age attached. |
| `ValuateWithFreshSnapshot` | `bool` | `false` | Whether each payment gets its own capture. Costs one network round trip per payment, not per pair: a batch of pending payments that all price against the same pair still costs that many round trips per poll. |
| `CaptureTimeout` | `TimeSpan` | 30 s | How long one capture may run before it is abandoned. A source that hangs must not stall the pairs behind it. |
| `ValuationPollInterval` | `TimeSpan` | 5 s | How often the valuation queue is drained. |
| `ValuationBatchSize` | `int` | 50 | How many queued valuations are priced, and how many delivered, per pass. |
| `StoreTimeout` | `TimeSpan` | 5 s | How long a single call to `IQuoteStore` may run before it is abandoned, on every path that must not hang: the collector's own read and write of a pair's stored quote, and the write that queues a payment for valuation (which runs after the payment's receipt has been announced, not before). `IQuoteStore` is host-implemented, and a store that merely hangs must not be able to stall the collector's cycle or the valuation queue indefinitely. |

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

`QuoteResult`'s amount fields are always in pay-to-get terms, whichever direction was
asked: `InputAmount` is the amount of the received asset the trade requires, `FilledInput`
is how much of it the venues could actually absorb, and `OutputAmount` is the quote-asset
amount that produces. Direction only says which side the caller pinned: under `ExactInput`
the caller pinned `InputAmount`; under `ExactOutput` the caller pinned the quote-asset
amount they need, and the implementation computed the `InputAmount` that yields it.
`IsFullyFilled` means the same thing either way — the required input could actually be
traded — and `EffectivePrice` / `SlippagePercent` are derived from what filled, so they do
not change meaning with direction.

Every answer carries `CapturedAt`, `Age` and `IsStale`. That is deliberate: a price
computed from a stale book looks exactly as confident as a fresh one.

When an amount was asked for, `view.Result` carries three things worth checking before
trusting the number — and it is null when the snapshot answered "no liquidity", the same
condition that leaves `view.MarginalPrice` null:

- `view.Result.IsFullyFilled` — false means the venues ran dry and the size cannot actually trade. Under
  `ExactOutput`, a partial fill means `OutputAmount` is what `FilledInput` produces, not the caller's ask.
- `view.Result.BookTruncated` — the real book may run deeper than what was priced.
- `view.Result.SlippagePercent` — how much worse the achieved price is than the marginal one.

## Valuation

A received payment in a configured asset is queued, priced, and handed to
`IPaymentValuedHandler`. This is a second, later signal than `IPaymentReceivedHandler`,
never a replacement: waiting for a price before announcing a payment would put liquidity
availability on the path of money arriving.

The valuation is queued only after `IPaymentReceivedHandler` has been given its chance —
whether or not the handler actually accepted the payment, since a failing handler must not
cost the valuation too — and only for a payment that was not already recorded. A replayed
payment, during catch-up or a duplicate live notification, reaches neither the handler nor
the queue a second time, which is what keeps a long catch-up window cheap: most of what it
replays is payments the store has already seen.

Queuing is never done on the path that records the payment itself: `RecordAsync` runs one
payment at a time during catch-up, ahead of the live subscription's bounded buffer, and an
optional feature has no business throttling it. That keeps a replayed payment free, but not a
newly recorded one — the queue write for a genuinely new payment is still awaited by the same
per-transaction call catch-up replays through, so an `IQuoteStore` whose write hangs still
costs up to `StoreTimeout` there, once per newly recorded payment, before the next
transaction in the page can be looked at.

Delivery is at least once, so the handler must be idempotent. A payment that cannot be
priced yet stays queued and is retried, in an order that favours fairness over strict
enqueue time: an entry that keeps failing — its pair removed from configuration, an
evaluation that throws deterministically, a save the store rejects — is retried once per
sweep of the queue rather than blocking everything queued behind it. The money is already
recorded either way.

If queueing a payment for valuation fails outright — the store threw, or the write ran past
`StoreTimeout` — nothing in this library retries it on its own. The payment is lost to
valuation unless the host calls `IPaymentMonitorHealth.ReconcileAsync`, and even then only
if the payment falls inside the reconciler's `ReconcileWindow`; an outage that outlasts that
window loses the valuation for good, though the payment itself was never at risk. Hosts that
want this to self-heal reliably should schedule reconciliation to run.

Reconciliation's sweep offers every payment it re-reads in its window to the quote store as
well, whether or not that payment was already recorded — the store rejects the duplicates
itself. This is where the recovery above actually happens: the sweep is the mechanism, not a
side effect of it. That means a reconcile run costs one extra store round trip per payment in
the window, on top of whatever the payment monitor itself already spends re-reading it.

## Health

| Field | Meaning |
|---|---|
| `ConfiguredPairs` / `PairsWithFreshQuote` | How many pairs hold a reading within the age limit. |
| `OldestQuoteAge` | Age of the oldest held reading. |
| `PairsFailing` / `MaxConsecutiveFailures` / `LastError` | Refresh failures. A pair that keeps failing serves its last good reading until it expires. |
| `PendingValuations` / `OldestPendingAge` | Queue depth waiting to be priced. A backlog is normal; a growing age is not. |
| `UndeliveredValuations` / `OldestUndeliveredAge` | Priced but not yet accepted by your handler, and how long the oldest of those has been waiting. A count alone saturates at `ValuationBatchSize` once delivery falls behind, so a handler that has stopped accepting valuations can look like a queue that is draining; the age is what does not lie, and what to alert on. |
| `CycleFitsInInterval` | False means pairs refresh slower than configured. This checks spacing only — the pauses between pairs — and knows nothing about how long a capture itself takes, so it can read true while the real cycle runs far longer than `RefreshInterval`. |
| `LastCycleDuration` | How long the collector's last full refresh cycle actually took, or null before the first one completes. Measured, not predicted, unlike `CycleFitsInInterval` — compare this against `RefreshInterval` for the truth. |
| `StoreReadable` | Whether the store answered the last time the health report itself tried to read it. |
| `StoreWritable` | Whether the collector's most recent attempt to persist a quote actually reached the store. A store whose writes hang or throw while its reads keep answering would otherwise be invisible: captures keep updating the in-memory snapshot every cycle, so every other field here would report healthy for as long as the process stays up, however long persistence has actually been broken. |
| `IsHealthy` | Every pair fresh, nothing failing, store readable and writable. A pair with genuinely no liquidity also reads unhealthy: the collector correctly clears its snapshot on an empty capture, so that pair never counts as fresh. Defensible, but worth knowing before treating a false reading as an incident. |
