# Xrpl.PaymentGateway — Design

Date: 2026-08-25
Status: Approved for planning

## Overview

A .NET library that lets any project accept XRPL payments on a single receiving
account and reliably record them, while the host application decides where and
how records are stored (PostgreSQL, a file, anything). The library provides the
storage abstraction, a background monitor built on the `Xrpl` SDK, destination
tag allocation for buyers, and a health/reconciliation service the host can
drive from any scheduler.

## Goals

- Issue payment instructions (r-address + destination tag) per buyer; a
  returning buyer always receives the tag assigned earlier.
- Watch the receiving account in the background and record every incoming
  payment exactly once, keyed by transaction hash.
- Compute received amounts from transaction metadata via the SDK's
  `Xrpl.Utils.BalanceChanges` (not `delivered_amount`). XRP and IOU are
  supported.
- Survive connection loss, node failure, node lag, network-wide consensus
  stalls, and node history gaps without losing or duplicating records.
- Deliver each recorded payment to host code (at-least-once) through a DI
  handler interface.
- Stay storage-agnostic: the host implements one storage interface; the
  library never assumes a database.

## Non-goals

- Outgoing payments, refunds, invoicing, fiat conversion.
- Multiple receiving accounts per service instance (run one instance per
  account instead).
- MPT amounts (may be added later).
- A built-in scheduler (the host calls health/reconciliation from Hangfire,
  Quartz, a timer — whatever it already runs).
- Horizontal scaling of the monitor: exactly one running monitor instance per
  receiving account is supported. A second accidental instance produces no
  duplicate records (idempotent writes) but is not a supported mode.

## Packages

```
Xrpl.PaymentGateway/                        (this repository)
├── src/
│   ├── Xrpl.PaymentGateway.Abstractions/   # models + interfaces, no Xrpl dependency
│   └── Xrpl.PaymentGateway/                # monitor, node pool, catch-up, health
├── tests/
│   └── Xrpl.PaymentGateway.Tests/          # xUnit v3
└── samples/                                # later: file-storage sample host
```

Both packages target `net8.0;net9.0;net10.0` (matching the `Xrpl` package).
`Abstractions` depends only on the BCL so a host that implements storage does
not pull in the SDK and its crypto dependencies. `Xrpl.PaymentGateway`
references `Abstractions`, the `Xrpl` NuGet package, and
`Microsoft.Extensions.*` abstractions (Hosting, Options, Logging, DI).

## Data model

```csharp
public sealed class PaymentRecord
{
    public required string TransactionHash { get; init; }   // unique key
    public required string TransactionType { get; init; }   // "Payment", "CheckCash", ...
    public required string Sender { get; init; }            // tx.Account, never the receiving address
    public uint? DestinationTag { get; init; }              // null when the tx carried no tag
    public required string Currency { get; init; }          // "XRP" or an IOU code (3-char or 40-char hex)
    public string? Issuer { get; init; }                    // null for XRP
    public required decimal Value { get; init; }            // human units; XRP in XRP, not drops
    public required uint LedgerIndex { get; init; }
    public required DateTimeOffset ProcessedAt { get; init; }
}
```

Decisions behind the model:

- **Single amount per record.** An incoming payment from a third party credits
  exactly one asset. The known multi-asset cases on XRPL (two-asset
  `AMMWithdraw`, auto-bridged offer crossings) require the account to act as an
  AMM LP or a market maker, which a dedicated receiving account does not do.
  `AMMWithdraw` is additionally excluded by the sender filter below.
- **Sender filter.** Transactions where `tx.Account` equals the receiving
  address are never recorded — they are the account's own actions, not incoming
  payments.
- **Any crediting transaction counts.** Not only `Payment`: anything validated
  that produced a positive balance change for the receiving address
  (`CheckCash`, `EscrowFinish`, ...) is recorded, hence `TransactionType` in
  the record. Buyer payments arrive as `Payment` with a tag; everything else is
  recorded with `DestinationTag = null` and simply is not linked to a buyer.
- **Defensive anomaly path.** If the processor ever computes more than one
  positive asset delta for a single transaction, it records the largest one,
  logs an error with the full transaction, and increments an anomaly counter
  exposed by the health report. No silent loss, no data-model complexity for a
  phantom case.

## Storage abstraction

```csharp
public interface IPaymentStore
{
    // --- buyers and tags ---
    /// Atomically return the buyer's existing tag or assign the next counter value.
    Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken ct);
    Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken ct);

    // --- payments ---
    /// false — a record with this hash already exists (idempotency).
    Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken ct);
    Task MarkHandledAsync(string transactionHash, CancellationToken ct);
    Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken ct);

    // --- ledger cursor ---
    Task<uint?> GetLastProcessedLedgerAsync(CancellationToken ct);
    Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken ct);
}
```

Contract requirements for implementers (documented on the interface):

- `GetOrAssignTagAsync` must be atomic: two concurrent calls for the same new
  buyer must return the same tag; the counter must never hand out one tag to
  two buyers. Tags are sequential starting from `FirstDestinationTag`
  (default 1; 0 is never issued because many wallets treat it as "no tag").
- `TryAddPaymentAsync` must enforce hash uniqueness and return `false` on a
  duplicate instead of throwing.
- No cross-method transactionality is required. The library writes the payment
  first and advances the cursor afterwards; a crash between the two causes an
  idempotent replay, not loss.

Payments are inserted as unhandled; `MarkHandledAsync` flips the flag after the
host handler succeeds. `GetUnhandledPaymentsAsync` feeds redelivery during
reconciliation.

An `InMemoryPaymentStore` reference implementation ships in the main package:
it documents the expected semantics, backs the unit tests, and serves demos.

## Host-facing API

```csharp
public interface IPaymentGateway
{
    /// r-address + tag for invoicing a buyer (an existing buyer gets the previously assigned tag).
    Task<PaymentInstructions> GetPaymentInstructionsAsync(string buyerId, CancellationToken ct);
}

public interface IPaymentReceivedHandler
{
    /// buyerId is non-null when DestinationTag resolved to a known buyer.
    Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken ct);
}

public sealed class PaymentInstructions
{
    public required string Address { get; init; }        // the receiving r-address
    public required uint DestinationTag { get; init; }   // the buyer's tag
}
```

Handler semantics: called after the record is persisted, at-least-once —
implementations must be idempotent. A handler exception never blocks recording;
the record stays unhandled and is redelivered by reconciliation.

### Options and registration

```csharp
public sealed class PaymentGatewayOptions
{
    public required string Address { get; set; }
    public required IReadOnlyList<Uri> Nodes { get; set; }     // allowed WS node pool
    public IReadOnlyList<Uri>? CatchUpNodes { get; set; }      // optional full-history nodes for catch-up
    public uint? StartLedgerIndex { get; set; }                // used when the store has no cursor
    public uint FirstDestinationTag { get; set; } = 1;
    public TimeSpan LedgerStallTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public int StreamBufferCapacity { get; set; } = 1000;
    public uint ReconcileWindow { get; set; } = 2000;          // ledgers below the cursor to re-verify
}
```

`services.AddXrplPaymentGateway(opts => ...)` registers the monitor
(`IHostedService`), `IPaymentGateway`, and `IPaymentMonitorHealth`. The host
registers its own `IPaymentStore` and `IPaymentReceivedHandler`.

## Monitor design

Components of `Xrpl.PaymentGateway`:

| Component | Role |
|---|---|
| `XrplPaymentMonitor : BackgroundService` | Owns the lifecycle: connect → subscribe → catch-up → stream; sole orchestrator |
| `NodePool` | Allowed endpoints from options; hands out the next node, tracks backoff |
| `TransactionProcessor` | Transaction + metadata → `PaymentRecord?` via `BalanceChanges`; the same code for stream and catch-up |
| `PaymentDispatcher` | `TryAddPaymentAsync` → `FindBuyerByTagAsync` → handler → `MarkHandledAsync` |
| `CatchUpRunner` | `account_tx` from the cursor to the current validated ledger, marker pagination, range-completeness verification |

The node connection is hidden behind an internal adapter interface over
`XrplClient` so unit tests substitute a fake without a real WebSocket.

### Data flow (cold start and every reconnect — same path)

```
1. cursor = GetLastProcessedLedgerAsync()
   └─ null → StartLedgerIndex from options, else current validated (history is not scanned)
2. Connect to a node from the pool
3. subscribe: accounts=[Address] + streams=[ledger]
4. V = current validated ledger index
5. Catch-up: account_tx [cursor+1 … V] ──→ TransactionProcessor → PaymentDispatcher
   (stream events accumulate in a bounded Channel meanwhile)
6. Drain the channel, then normal streaming
```

Subscribing before catching up closes the gap between the two paths; overlap
(a transaction seen by both) is absorbed by the idempotent
`TryAddPaymentAsync` — the second path gets `false` and does not invoke the
handler.

### Per-transaction processing (single consumer, sequential)

1. Skip if not `validated`, if `meta.TransactionResult != tesSUCCESS`, or if
   `tx.Account` is the receiving address.
2. `BalanceChanges(meta)` → deltas for the receiving address → keep positive.
3. None → skip (e.g. someone's `TrustSet` towards us). More than one → anomaly
   path (largest amount + error log + counter).
4. `TryAddPaymentAsync`; on `true`: resolve buyer by tag, invoke the handler in
   try/catch, on success `MarkHandledAsync`.

### Cursor invariant

The cursor is not "the last ledger seen" — it is **the boundary below which
record completeness is proven** (by an uninterrupted stream, or by a verified
catch-up). It advances only when that statement is true.

Advancement rule: on `ledgerClosed(N)` from the ledger stream the cursor moves
to `N−1`, and only after every transaction up to that ledger has been drained
from the channel. The one-ledger margin covers the fact that rippled does not
hard-guarantee "all transactions of a ledger before its ledgerClosed" message
ordering; the cost on restart is a couple of idempotent replays.

**Contiguity is part of the proof.** "Uninterrupted stream" means the ledger
numbers arrive without gaps. Two things break that in practice: a node catching
up applies ledgers in bulk and announces only the latest, and the SDK's inbound
stream queue is bounded with a drop-oldest policy, so frames — transactions
included — can be discarded silently under load. Hence two guards:

- `ledgerClosed(N)` with `N > lastSeen + 1` does not advance the cursor. The
  session ends instead, and the next one replays the span through a verified
  catch-up. `N ≤ lastSeen` is ignored: it is a queued frame that proves nothing
  new, and treating it as the stream running backwards would cause a reconnect
  loop. The baseline for `lastSeen` at session start is `max(validated, cursor)`,
  because a node can sit behind a cursor another node already proved.
- A rise in the client's dropped-frame counter ends the session for the same
  reason.

`StartLedgerIndex` above the current validated ledger is clamped down to it: a
cursor parked in the future would discard every later write as "already past
this" and report zero lag while proving nothing.

## Failure handling

- **WS drop / subscribe failure / node desync** — reconnect with exponential
  backoff and jitter; each retry takes the next node in the pool. After
  reconnect, the same path as a cold start (steps 1–6).
- **Node stall** — no `ledgerClosed` for `LedgerStallTimeout` (default 20 s
  against the normal 3–5 s close time) → force-switch to the next node. This
  also covers "node alive but lagging".
- **Network-wide consensus stall** (has happened: Feb 2021, Nov 2024,
  ~1 h in Feb 2025). Only validated data is consumed, so a halt pauses the
  service without loss — nothing is being validated while the network is
  stalled. To avoid treating it as node failure: when the stall timer fires,
  query `server_info` on the current and next node; if nodes are in
  `server_state: full` but the validated ledger is not advancing anywhere and a
  full pool cycle stalls on every node → enter **NetworkStalled**: keep one
  connection, stop rotating, poll `server_info` every 30 s, log a warning,
  expose the state in the health report. When the network resumes — normal
  streaming or an ordinary reconnect with catch-up from the unchanged cursor.
- **Node history gaps.** The validated ledger chain is continuous by
  construction; ledgers "disappear" only from a node's local history
  (`online_delete`, desync). This hits catch-up: `account_tx` over
  `[cursor+1 … V]` on a gappy node silently searches only what the node has.
  Defense in `CatchUpRunner`:
  1. Before catch-up, check `server_info.complete_ledgers` covers the range
     contiguously; otherwise skip this node for catch-up and try the next.
  2. After `account_tx`, verify the response's echoed `ledger_index_min/max`;
     a narrower-than-requested range means an incomplete search — node
     rejected.
  3. If no pool node covers the range, the cursor does not advance (data must
     not be invented), streaming continues for new payments, and the health
     report raises `HistoryGap` with the gap boundaries — the operator's cue to
     add a full-history node.
  4. Optional `CatchUpNodes`: dedicated full-history endpoints used only for
     catch-up, so regular streaming can use light nodes.
- **Store outage** — retries with backoff, unbounded attempts: a payment must
  not be lost. Channel processing pauses, the cursor does not move; if the
  bounded channel (default 1000) fills up, the connection is dropped
  deliberately — after the store recovers, catch-up recollects everything from
  the ledger. The store is the source of truth; the in-memory buffer is not.
- **Host handler exception** — logged; the record is already persisted and
  stays unhandled; reconciliation redelivers. The handler cannot block
  recording.

## Health and reconciliation

```csharp
public interface IPaymentMonitorHealth
{
    /// Fast read-only snapshot — for a scheduler job, /health endpoint, metrics.
    Task<PaymentMonitorHealthReport> CheckAsync(CancellationToken ct);

    /// Verification and repair. Long-running; guarded against concurrent runs.
    Task<ReconciliationResult> ReconcileAsync(CancellationToken ct);
}
```

`PaymentMonitorHealthReport`: monitor state (`Streaming` / `CatchingUp` /
`Reconnecting` / `NetworkStalled` / `StoreUnavailable` / `HistoryGap`), current
node, last seen validated ledger, cursor and its lag behind the network,
unhandled record count, anomaly counter, last error. Built from the monitor's
in-memory snapshot plus two cheap store queries — safe to call every few
seconds.

`ReconciliationResult`: number of redelivered records, number of recovered
(previously missing) records, and the errors encountered.

`ReconcileAsync` performs two safety-net jobs (monitor correctness does not
depend on them):

1. **Redeliver unhandled records**: `GetUnhandledPaymentsAsync` → handler →
   `MarkHandledAsync`. This closes the at-least-once loop for handler failures.
2. **Completeness sweep below the cursor**: `account_tx` over
   `[cursor − ReconcileWindow … cursor]` (default 2000 ledgers, ~2 h), with the
   same `complete_ledgers` verification as catch-up, through the same
   `TransactionProcessor` → `TryAddPaymentAsync`. Normally every insert returns
   `false`. Every `true` is a payment that was missing from the store: it is
   recorded late, logged as an error, and reported — a signal of a bug or an
   incident, not a normal path.

The host schedules both calls from anything — Hangfire, Quartz,
`PeriodicTimer`; the package has no scheduler dependency.

## Testing

- xUnit v3 with the built-in `Assert` (no FluentAssertions).
- Unit tests run against the fake connection adapter — no real WebSocket:
  - `TransactionProcessor` on canned JSON fixtures: XRP payment, IOU payment,
    partial payment (`tfPartialPayment`: the `Amount` field lies, metadata
    deltas give the actually delivered value — locked in by a test),
    `tec` transaction (skip), own transaction (sender filter), multi-asset
    anomaly (largest amount + error log).
  - `PaymentDispatcher`: success → `MarkHandledAsync`; handler failure leaves
    the record unhandled.
  - `CatchUpRunner`: `complete_ledgers` parsing (including gapped
    `a-b,c-d` forms), node rejection by echoed range, marker pagination.
  - Monitor state machine: drop → reconnect with node rotation; stall timer →
    rotation; full stalled cycle → `NetworkStalled`; store outage → pause
    without cursor movement.
  - Tag allocation: new/existing buyer, concurrent allocation against
    `InMemoryPaymentStore`.
- Integration tests (second phase): the rippled standalone docker stand from
  XrplCSharp `.ci-config` (started under a distinct compose project name),
  scenarios "tagged payment reaches the handler" and "connection drop →
  catch-up recovers".

## Repository and CI

- Repository `StaticBit-io/Xrpl.PaymentGateway`, private for now, to be opened
  later; branches `dev`/`release` mirroring XrplCSharp conventions; all
  artifacts in English.
- GitHub Actions: build + unit tests on PRs; both packages published to NuGet
  on push to `release` (publishing workflow becomes relevant once the
  repository goes public).
