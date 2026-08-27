# Configuration reference

Every setting `PaymentGatewayOptions` accepts, and everything the health report tells you. For what the
gateway does and how to wire it up, start from the [README](../README.md).

## Required settings

The gateway refuses to start without these. Validation runs when the options are first read, so a mistake
surfaces at startup rather than at the first payment.

| Option | Type | Meaning |
|---|---|---|
| `Address` | `string` | The receiving account, as a classic r-address. Must pass `XrplCodec.IsValidClassicAddress`; an X-address is rejected, because the monitor compares it against the `Destination` field of incoming payments, which is always classic. |
| `Nodes` | `IReadOnlyList<Uri>` | At least one node, each using the `ws` or `wss` scheme. Tried round-robin: every reconnect moves to the next one. |

## Optional settings

### Where to start and which nodes to use

| Option | Type | Default | Meaning |
|---|---|---|---|
| `CatchUpNodes` | `IReadOnlyList<Uri>?` | falls back to `Nodes` | Full-history nodes used only for catch-up and reconciliation, so ordinary streaming can run against light nodes. Same scheme rule as `Nodes`. |
| `StartLedgerIndex` | `uint?` | current validated ledger | Where to begin when the store has no cursor. The ledger you name is treated as already processed, so catch-up starts at the one after it. A value above the network's own tip is clamped down to it and logged. |
| `FirstDestinationTag` | `uint` | `1` | The tag for the first buyer. Must be greater than zero: many wallets read a tag of `0` as "no tag". The library validates this value but cannot apply it — [tag allocation belongs to your store](#tags-are-the-stores-job). |

### Reacting to a node that stops answering

| Option | Type | Default | Meaning |
|---|---|---|---|
| `LedgerStallTimeout` | `TimeSpan` | 20 s | Time without a ledger close before the node is suspected. A healthy network closes one every 3–5 seconds. |
| `NetworkStallProbeInterval` | `TimeSpan` | 30 s | How often to re-check once the stall has been blamed on the network rather than the node. |
| `ProductiveSessionThreshold` | `TimeSpan` | 30 s | How long a session must last to count as productive and reset the reconnect backoff. Without it, a node that accepts the socket and drops it immediately would be retried forever at the base delay. |
| `ReconnectBaseDelay` | `TimeSpan` | 1 s | Wait after the first failed session. Doubles per consecutive failure, spread by ±25% so a fleet that lost the same node does not return in lockstep. |
| `ReconnectMaxDelay` | `TimeSpan` | 30 s | Ceiling on that doubling. Must not be less than `ReconnectBaseDelay`. |

All five must be positive.

### Surviving a store that is down

| Option | Type | Default | Meaning |
|---|---|---|---|
| `StoreRetryBaseDelay` | `TimeSpan` | 1 s | Wait before retrying a failed store call. Doubles and jitters like the reconnect delay. |
| `StoreRetryMaxDelay` | `TimeSpan` | 30 s | Ceiling on that. Must not be less than `StoreRetryBaseDelay`. |

Store retries have no attempt limit on purpose. The store is the source of truth, and giving up would lose
a payment; instead the monitor pauses, the ledger cursor stops advancing, and the health report says
`StoreUnavailable` until the store answers again.

### Buffering and reconciliation

| Option | Type | Default | Meaning |
|---|---|---|---|
| `StreamBufferCapacity` | `int` | `1000` | Events held between the socket and processing. Overflowing it drops the session deliberately: catch-up recollects everything from the ledger, so the buffer is never the system of record. Must be positive. |
| `ReconcileWindow` | `uint` | `2000` | Ledgers below the cursor that `ReconcileAsync` re-verifies — roughly two hours. Must be greater than zero. Run reconciliation more often than this window, or ledgers exist that no sweep ever covers. |
| `HealthUnhandledSampleSize` | `int` | `100` | Cap on how many undelivered payments a health check counts and a reconciliation run redelivers per pass. Must be positive. |
| `MaxAcceptableLedgerLag` | `uint` | `10` | Ledgers the cursor may trail the network by while the health report still calls itself healthy. |

## Tags are the store's job

`FirstDestinationTag` is validated by the library and used by nothing inside it. Allocation belongs to
`IPaymentStore.GetOrAssignTagAsync`, so the value has to reach whichever store you registered:

```csharp
uint firstTag = 1;

builder.Services.AddSingleton<IPaymentStore>(_ => new FilePaymentStore("payments.json", firstTag));
builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = "rYourReceivingAddress";
    options.Nodes = [new Uri("wss://xrplcluster.com")];
    options.FirstDestinationTag = firstTag;
});
```

Setting it on the options alone changes nothing.

## Reading the health report

`IPaymentMonitorHealth.CheckAsync` is cheap enough to call every few seconds. It returns:

| Field | Type | Meaning |
|---|---|---|
| `State` | `PaymentMonitorState` | What the monitor is doing. See the table below. |
| `IsHealthy` | `bool` | True only when streaming, within `MaxAcceptableLedgerLag`, with nothing undelivered, and with the store readable. A failed store read never reports healthy. |
| `CurrentNode` | `string?` | The node in use, or null when not connected. |
| `LastValidatedLedger` | `uint?` | Highest validated ledger the monitor has seen. |
| `Cursor` | `uint?` | The persisted boundary below which record completeness is proven. |
| `LedgerLag` | `uint` | Ledgers between the cursor and the last validated ledger. |
| `UnhandledPaymentCount` | `int` | Payments recorded but not yet accepted by your handler, capped at `HealthUnhandledSampleSize`. |
| `AnomalyCount` | `long` | Transactions that credited the account in a shape a receiving account should not see. Any rise is worth investigating. |
| `LastError` | `string?` | The last error the monitor recorded. |
| `LastLedgerAt` | `DateTimeOffset?` | When the monitor last saw a ledger close. |

### Monitor states

| State | Meaning | What to do |
|---|---|---|
| `Stopped` | Not started, or shut down. | Nothing, unless it is unexpected. |
| `Connecting` | Opening a connection. | Wait. |
| `CatchingUp` | Replaying ledgers between the cursor and the network's tip. | Wait. Payments sent now arrive late, not never. |
| `Streaming` | Connected and following the ledger. | The healthy state. |
| `Reconnecting` | Backing off before the next attempt. | Wait; the backoff is capped. |
| `NetworkStalled` | Every reachable node is synced but no ledgers are being validated. | Nothing local is broken. Nothing is being validated, so nothing can be missed. |
| `StoreUnavailable` | Store calls are failing and being retried. | Fix the store. The cursor is frozen meanwhile, so nothing is lost. |
| `HistoryGap` | A ledger range could not be verified by any node, so the cursor is frozen. | Add a full-history node to `CatchUpNodes`. |

## Reading the reconciliation result

`ReconcileAsync` is slower and meant for a schedule, not a request path.

| Field | Type | Meaning |
|---|---|---|
| `RedeliveredCount` | `int` | Records that were undelivered and reached your handler on this run. Counts successes only; a handler that keeps failing shows up in `Errors`. |
| `RecoveredCount` | `int` | Payments found on the ledger that were missing from the store. **Any value above zero is a defect** — investigate. |
| `Errors` | `IReadOnlyList<string>` | What went wrong. Non-empty means the run did not finish everything it set out to do. |
| `Skipped` | `bool` | True when another reconciliation was already running and this call did nothing. |
