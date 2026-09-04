# Changelog

## 1.1.0 — 2026-09-03

Quotes and payment valuation, entirely optional: a host that upgrades and changes
nothing else gets no new background service and no new network traffic.

- `IQuoteSource` lets the host supply the pricing engine. The gateway owns the refresh
  rhythm, the age policy and delivery; order-book and AMM arithmetic stays outside, the
  same way storage does.
- A background collector refreshes each configured pair, spreading pairs evenly across
  the interval rather than firing them in a burst.
- A node that cannot be reached keeps the last good reading. Only an answered "there is
  no liquidity here" clears one — an empty book is what a dropped socket returns too.
- Quotes carry the age and ledger of the snapshot they came from, and a reading past its
  age limit is withheld rather than served as though it were current.
- Received payments are valued behind the payment path, never on it: the payment is
  recorded and announced first, and the valuation arrives as a second signal through
  `IPaymentValuedHandler`. Delivery is at least once, retried until the handler accepts it.
- `IQuoteStore` is separate from `IPaymentStore`, so nothing written against 1.0.0 breaks.
  In-memory, file and PostgreSQL implementations ship, all held to one `QuoteStoreContract`.
- A payment that cannot be priced is not retried in silence. `ValuationState` says where each
  one stands: `Pending`, `Valued`, `ValuedManually`, `Failed` or `WrittenOff`. `Failed` is used
  only for the two causes the gateway is certain about — the pair is no longer configured, or
  the host's pricing threw — and carries the reason. Everything else stays `Pending`.
- A failed valuation reaches `IPaymentValuedHandler` like any other, so the host can tell the
  buyer the funds arrived but could not be valued yet.
- `IUnresolvedValuationAdmin` gives an operator the way out: list the payments that have been
  stuck longer than a chosen age, price one at a rate supplied by hand, or write it off. Either
  resolution is announced to the handler through the same path a normal valuation takes, so a
  host books the money with one piece of code. A resolution that lost a race to another operator
  reports that it did not apply rather than claiming success.
- The pending queue is per pair, so a pair whose snapshot is missing delays only its own
  payments and never blocks a healthy pair's.
- `IQuoteHealth` reports pair freshness, failure streaks, the true pending count and the age of
  the oldest waiting payment — not a page-capped approximation of them.
- The sample can pay itself: set `Xrpl:Demo:PayerSeed` and the checkout page grows a send button per
  accepted asset, so a full demonstration needs one terminal rather than two. Test networks only — a seed
  in configuration is a private key in a text file.
- The sample can also price off the ledger rather than off configuration: `Xrpl:Quotes:Source` picks
  between the fixed-rate stand-in and an AMM source that reads the pair's pool from a validated ledger, so
  slippage, a moving ask and a real ledger index are visible in the demo. `samples/seed-demo-stand.py`
  builds the accounts, tokens and pools a standalone stand needs for it.

Nothing in the 1.0.0 surface changed.

## 1.0.0 — 2026-08-27

First release.

- `IPaymentStore` abstraction so the host decides where payments are recorded.
- Only a `Payment` addressed to the receiving account is recorded. Transactions that move the account's
  balances without being a payment to it — an offer of ours being crossed, a payment rippling through to a
  third party — are ignored.
- Sequential destination tag allocation, stable per buyer.
- Background monitor: account subscription, catch-up after every reconnect, node rotation, network-stall
  detection, node history verification, and a cursor that freezes rather than skipping an unproven range.
- Ledger contiguity is enforced: a stream that skips ledger numbers, or a client that dropped frames from
  its bounded inbound queue, ends the session instead of advancing the cursor across unseen ledgers.
- Transaction analysis cannot throw. Metadata is written by whoever built the payment path, and an
  exception on a transaction every catch-up replays would wedge the monitor permanently; such a
  transaction is reported as an anomaly instead. An amount beyond what `decimal` can hold — which the
  SDK reports as `AmountOutOfRangeException` by design, since XRPL issued currency reaches ~1e96 — is
  reported with the amount the node sent, so an operator can tell an absurd token supply from a real
  problem.
- Every way a payment addressed to the account can fail to produce a record — an unreadable body, an
  amount the balance reader does not understand — is logged as an error and counted, never skipped
  quietly.
- Amounts computed from transaction metadata balance changes, so partial payments record what arrived.
  Issued currencies are covered against a real ledger from both sides of a trust line, not only against
  hand-written metadata.
- `IPaymentMonitorHealth` for liveness reporting and reconciliation from any scheduler.
- Three stores to choose from: `PostgresPaymentStore` (in `Xrpl.PaymentGateway.Postgres`), a
  database-free `FilePaymentStore`, and `InMemoryPaymentStore` for tests and demos. All three are held to
  one `PaymentStoreContract` test suite, concurrency and restart included, so the interface is proven
  satisfiable rather than merely described.
- A sample: a minimal API with a checkout page in front of it, in three static files with no build step.
  It switches between the in-memory and file stores by configuration, and offers the checkout as a
  scannable X-address, which carries the destination tag a bare address would lose.

Built against `Xrpl` 11.1.0.
