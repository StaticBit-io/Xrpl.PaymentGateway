# Changelog

## 0.1.0 — unreleased

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
