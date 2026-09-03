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
| `StoreTimeout` | `TimeSpan` | 5 s | How long a single call to `IQuoteStore` may run before it is abandoned, on every path that must not hang: the collector's own read and write of a pair's stored quote, the write that queues a payment for valuation (which runs after the payment's receipt has been announced, not before), and `IQuoteHealth.CheckAsync`'s own reads. `IQuoteStore` is host-implemented, and a store that merely hangs must not be able to stall the collector's cycle, the valuation queue, or a health check reporting on it. |

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

### The pending queue is per pair

`ValuationWorker` prices each configured pair independently. For every pair it first decides,
without asking the store anything, whether it can price at all this pass — a snapshot must
exist and, when `RefuseStaleQuotes` is set, be fresh — and only then asks
`IQuoteStore.GetPendingValuationsAsync` for that one pair's queued entries. A pair with a
missing or stale snapshot is simply skipped for the pass: its own payments wait a little
longer, and every other pair's payments are priced exactly as if the broken one did not
exist. Nothing shares a queue across pairs, so nothing on one pair can bury payments on
another behind it.

### The five states

`PaymentValuation.State` is a `ValuationState`: `Pending`, `Valued`, `ValuedManually`,
`Failed`, `WrittenOff`. A payment with no row at all is `null`; a row that exists always has
one of these states — there is no "none".

- **`Pending`** — queued, not yet priced. Every reason an entry sits here is transient and
  shared by every entry against the same pair: no snapshot has been captured for it yet, the
  held one is past `MaxQuoteAge`, the snapshot answered that it currently has no liquidity to
  price this amount against, or the store rejected the write that would have moved the entry
  on. None of these is retried on a timer or counted against the entry; it simply prices
  itself once conditions allow — a later snapshot, a later capture, a later store write.
- **`Valued`** — priced automatically from a snapshot. The common case.
- **`Failed`** — terminal. Reached only for a per-entry, non-transient cause: the pair was
  removed from configuration, or pricing it threw. Both are deterministic — another attempt
  cannot change either outcome — which is exactly what does not fix itself on a retry, so the
  entry leaves the pending queue for good and waits on an operator instead — see
  [Failed valuations and the operator path](#failed-valuations-and-the-operator-path) below.
  `PaymentValuation.FailureReason` says which cause it was, with the exception message where
  the cause was one.
- **`ValuedManually`** — an operator priced it, through `IFailedValuationAdmin`, at a rate
  they supplied. Distinguishable from `Valued` by the state itself: no need for a separate
  flag to answer "why is this row this number".
- **`WrittenOff`** — terminal. An operator looked at a `Failed` entry and decided it will
  never be priced or credited — dust, a spam token, a mistaken transfer.

There is deliberately no retry counter, no backoff schedule, and no second queue behind any
of this. A cause that reaches `Failed` is, by definition, one another automatic attempt
cannot fix; the fix is an operator's decision, not a timer.

Delivery is at least once, so `IPaymentValuedHandler` must be idempotent, and it hears about
all four non-`Pending` states, not only a successfully priced one: a `Failed` or `WrittenOff`
valuation arrives too, carrying `FailureReason` and no `QuoteAmount`. This is what lets a host
tell the buyer something useful instead of nothing — see the next section for what.

Marking an entry delivered is conditional on it still being in the state that was actually
handed to the handler. If an operator resolves an entry — pricing it manually or writing it
off — while a slow `IPaymentValuedHandler` call for its stale `Failed` content is still in
flight, the mark is refused and the row stays undelivered; the very next poll hands the
resolved content to the handler instead. Without this, the resolution could be marked
delivered on the handler call's behalf without the handler ever having seen it.

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

## Failed valuations and the operator path

This library has no UI and never will. `IFailedValuationAdmin` is the operator path a host
draws a screen on top of — list what needs attention, and act on one entry at a time:

```csharp
FailedValuationPage page = await admin.ListFailedAsync(limit: 50, offset: 0, ct);

// An operator found a real price some other way.
await admin.ValueManuallyAsync(transactionHash, rate: 0.0123m, ct);

// Or decided it will never be credited.
await admin.WriteOffAsync(transactionHash, "dust", ct);
```

`ListFailedAsync` pages `Failed` entries only, oldest-failed first, with a `TotalCount` for
pagination — a `WrittenOff` entry is settled and does not keep showing up here.

`ValueManuallyAsync` prices the entry's recorded amount at the supplied rate, moves it to
`ValuedManually`, and clears `FailedAt`/`FailureReason` — resolved now, not failed and valued
at once. Rejects a hash that is not currently `Failed`, and rejects a rate that is zero or
negative.

`WriteOffAsync` moves the entry to `WrittenOff` with no quote amount, recording the reason and
the time. It keeps `FailedAt`/`FailureReason` from the original failure for the record,
alongside the operator's own reason. Rejects a hash that is not currently `Failed` — writing
off something that priced normally is a mistake, not a workflow.

Neither operation delivers anything itself. Both leave the resolved row undelivered — even
when the `Failed` row it replaces had already been delivered, since a manual price or a
write-off is a new fact the host has not heard yet — exactly as `ValuationWorker` leaves a
freshly computed automatic valuation the moment it is saved, so the same delivery pass that
already retries a stuck automatic delivery is what hands this one to `IPaymentValuedHandler`
too, within one `ValuationPollInterval` — one delivery mechanism, not a second one built for
the operator path.

Both operations also refuse to replace an entry that has already moved on some other way —
two operators racing, one pricing and one writing off, cannot have one silently overwrite the
other; whichever call lands first wins, and the other is a no-op.

What to tell the buyer, from `PaymentValuation.State` on the delivered valuation:

- **`Failed`** — funds arrived but could not be valued yet; an administrator has been made
  aware and will follow up.
- **`WrittenOff`** — the case has been reviewed and closed; `FailureReason` carries why,
  `WriteOffReason` carries the operator's own note. This is what stops a buyer being told to
  contact an administrator about something that is already resolved.

## Health

| Field | Meaning |
|---|---|
| `ConfiguredPairs` / `PairsWithFreshQuote` | How many pairs hold a reading within the age limit. |
| `OldestQuoteAge` | Age of the oldest held reading. |
| `PairsFailing` / `MaxConsecutiveFailures` / `LastError` | Refresh failures. A pair that keeps failing serves its last good reading until it expires. |
| `PendingValuations` / `OldestPendingAge` | Queue depth waiting to be priced, and the age of the oldest entry — the true totals across every pair, from one store call (`IQuoteStore.GetPendingValuationBreakdownAsync`), not a page capped at `ValuationBatchSize`. A backlog is normal; a growing age is not. |
| `UndeliveredValuations` / `OldestUndeliveredAge` | Past `Pending` — `Valued`, `ValuedManually`, `Failed` or `WrittenOff` — but not yet accepted by your handler, and how long the oldest of those has been waiting. A count alone saturates at `ValuationBatchSize` once delivery falls behind, so a handler that has stopped accepting valuations can look like a queue that is draining; the age is what does not lie, and what to alert on. |
| `FailedValuations` | Entries in `Failed` right now — waiting on an operator through `IFailedValuationAdmin`. A written-off entry is settled and does not count here: this is the queue an operator still has open work in, not a running total of everything that ever failed. A backlog here is expected work, not a symptom of the pipeline being broken, but it should not be ignored. |
| `CycleFitsInInterval` | False means pairs refresh slower than configured. This checks spacing only — the pauses between pairs — and knows nothing about how long a capture itself takes, so it can read true while the real cycle runs far longer than `RefreshInterval`. |
| `StoreReadable` | Whether the store answered the last time the health report itself tried to read it. |
| `PairsFailingToPersist` / `StoreWritable` | How many pairs' most recent attempt to persist a quote failed to reach the store, and that count being zero, spelled as a bool. Per pair, like every other count in this report: a store rejecting writes for two pairs out of three must not be erased by the third pair's next successful write, which a single process-wide flag would do. A store whose writes hang or throw while its reads keep answering would otherwise be invisible entirely: captures keep updating the in-memory snapshot every cycle, so every other field here would report healthy for as long as the process stays up, however long persistence has actually been broken. |
| `IsHealthy` | Every pair fresh, nothing failing, store readable and writable. A pair with genuinely no liquidity also reads unhealthy: the collector correctly clears its snapshot on an empty capture, so that pair never counts as fresh. Defensible, but worth knowing before treating a false reading as an incident. |
