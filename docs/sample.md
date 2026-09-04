# Sample checkout

`samples/Xrpl.PaymentGateway.SampleApi` is a minimal API with a checkout page in front of it — the whole
surface of the library, end to end, in something you can click. It is a demonstration, not a shop.

For the library this exercises, start from the [README](../README.md); for what each setting does, the
[configuration reference](configuration.md).

No build step and no package manager: the page is three static files, so `dotnet run` is the entire
toolchain. Everything optional is off until you configure it, so the sample starts as the smallest thing
that accepts a payment and grows from there.

## Before you start

- The .NET SDK, version 10 or later.
- Docker, to run the standalone `rippled` the sample points at. Any test network works instead; only the
  addresses and the node URL change.
- Python 3, for the stand-seeding script in [Reproducing the whole demo](#reproducing-the-whole-demo-on-a-standalone-stand).
  Nothing else here needs it.

## Run it

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
Xrpl__Address=rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh dotnet run --project samples/Xrpl.PaymentGateway.SampleApi
```

Open the printed URL. Enter a buyer id, take the address and destination tag, and pay. The page polls until
the gateway reports the payment; the strip along the top shows the monitor's own state, so a reconnect or a
catch-up is visible rather than looking like nothing happening. The page's own "sending from the standalone
stand" section prints the calls to pay yourself with no wallet.

`Xrpl:Address` above is the standalone stand's master account, which is convenient for a demo because it
already exists and is funded. For anything else, point it at your own receiving account.

Set `Xrpl:StorePath` to keep payments in a file instead of memory, and they survive a restart.

## What the QR code carries

The QR code carries an **X-address**, not a bare account: one string holding the address and the
destination tag together. A scanner given a classic address drops the tag, and a payment without the tag
lands on the account attached to nobody.

The X-address also encodes which network it is for. The sample flags it as test, matching the standalone
stand, so set `Xrpl:IsTestNetwork` to `false` before pointing it at mainnet or wallets there will refuse
the code. Test X-addresses begin with `T`, mainnet ones with `X`, which makes a mistake visible at a
glance.

## Quotes and valuation

The demo shop values everything in one asset — USD below — and takes payment in that asset directly plus
any number of others, each priced into it through its own pair. Set `Xrpl:Quotes:QuoteCurrency` and
`Xrpl:Quotes:QuoteIssuer` alongside `Xrpl:Quotes:Pairs` and the page grows a price at checkout, a valuation
that appears on a payment row a few seconds after the payment itself, a quote health strip beside the
monitor strip, and an "Unresolved payments" section for whatever the automatic pipeline could not price.

Leave it empty, the shipped default, and the sample runs exactly as it does without the feature —
`AddXrplPaymentQuotes` is never even called.

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

`Rate` is quote-asset units per unit of the received asset. Every pair shares the one
`QuoteCurrency`/`QuoteIssuer` above them, because the sample values everything a buyer can pay with in the
one asset a real shop would price its catalog in.

The quote asset itself is accepted directly and deliberately has no entry in `Pairs`: `QuotePair`
rejects quoting an asset against itself, so there is no USD/USD pair and cannot be one — a payment already
in USD needs no conversion, and the library queues no valuation for it. The sample handles that itself
rather than asking the library to. `SamplePaymentHandler` checks whether a payment's currency and issuer
are the configured `QuoteCurrency`/`QuoteIssuer`, and the page shows such a payment at its own amount,
labelled as needing no conversion, instead of a row waiting forever for a valuation that was never going to
arrive.

### Currency codes longer than three characters

A currency code longer than three characters has no short form on the ledger. RLUSD is
`524C555344000000000000000000000000000000`, and that hex is what goes in `Currency` — `CurrencyKey` rejects
a five-character code outright — and what the node reports back on every payment in it.

The page decodes such a code to its name for display, so the asset still reads as `RLUSD` on the price
line, on its pay button and on the payment row, while everything on the wire stays the code the ledger
actually uses.

### Where the prices come from

`Xrpl:Quotes:Source` picks the source, and the sample ships two answers to it.

The default, with the key absent or set to anything else, is `FixedRateQuoteSource`: rates read straight
from configuration, no network call at all. It shows the shape of the integration with nothing else
running — no stand, no pools, no liquidity — and it prices nothing real. Size does not move its answer, and
its `LedgerIndex` counts captures rather than naming a ledger. `Xrpl:Quotes:RefusedCurrencies` names
currencies it throws for instead of pricing, which gives the unresolved queue and the settle and write-off
buttons something to act on in a demo that otherwise always prices cleanly.

`"Source": "amm"` switches to `AmmQuoteSource`, which reads the pair's AMM pool off a validated ledger and
prices a size through the constant-product formula with the pool's own trading fee. Every number then comes
from the ledger: the ask moves when the pool does, the ledger index is a real one, and the price for the
size differs from the price per unit — the checkout line grows a "below spot" figure, which is slippage,
and a valuation's `EffectivePrice` sits below its `MarginalPrice` for the same reason. An asset with no
pool reads as having no liquidity, so it goes to the operator's queue like any other asset nothing can
price it against.

`AmmQuoteSource` is still a demonstration, not a quote engine: one pool, no order book, no routing through
a third asset, no splitting a size across venues. A pair whose book is deeper than its pool is mispriced by
it, and a pair with no pool reads as dry with an order book standing right there. A real host brings its
own pricing, the same way it brings its own `IPaymentStore` — that is what `IQuoteSource` being an
interface is for.

The library's own half of this is in the [quotes reference](quotes.md).

## Paying from the page

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
it so its token can move between two holders, opening the trust lines, and funding the payer. The script
below does all of it.

## Reproducing the whole demo on a standalone stand

The quote demo needs a token economy the ledger does not come with — an issuer whose tokens can move
between two holders, a receiving account with a trust line for each token it accepts, and a payer holding a
balance in each. `samples/seed-demo-stand.py` builds it and writes the configuration that goes with it:

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
python3 samples/seed-demo-stand.py --write samples/Xrpl.PaymentGateway.SampleApi/appsettings.Development.json
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/Xrpl.PaymentGateway.SampleApi
```

It also creates an AMM pool for each priced pair and writes `"Source": "amm"`, so the demo prices off the
ledger rather than off fixed rates. That gives the page all five assets at once: XRP, `GEM` and `RLUSD` —
the last under the long hex code — each priced into USD through its own pool, USD accepted directly, and
`JNK`, which is issued with no pool at all, so nothing can price it and it lands in the operator's queue.
The demo payer's buttons can send any of them.

The pools are sized so their spot prices match the fixed rates the same configuration carries, which makes
the two sources comparable: switch `Source` back and the asks move only by the pool's fee and the size's
own slippage. Drop the line entirely and the stand's pools stop being read at all.

Every account comes from a fixed passphrase, so a second run against a live stand does nothing and a run
against a recreated stand reproduces the same addresses. `--rpc` and `--node` point the script at a stand
on other ports; `XRPLPG_RPC_URL` and `XRPLPG_NODE_URL` do the same from the environment. Without `--write`
it prints the configuration instead.

`appsettings.Development.json` is git-ignored, because what the script writes into it includes the demo
payer's seed. Its shape is committed beside it as `appsettings.Development.example.json`, with the
addresses replaced by placeholders. And it is read only when the environment is `Development` — hence the
variable above, since `dotnet run` in a fresh clone has no `launchSettings.json` to set it.

## Endpoints

| Endpoint | |
|---|---|
| `POST /api/checkout/{buyerId}` | Address, destination tag, and an X-address carrying both |
| `GET /api/checkout/{buyerId}/qr.svg` | The X-address as a scannable QR code |
| `GET /api/checkout/{buyerId}/price` | What this checkout is priced at, in the quote asset, and how old that reading is. Empty until a quote pair is configured |
| `GET /api/checkout/{buyerId}/payments` | What this buyer has paid. The page polls this |
| `GET /api/checkout/{buyerId}/valuations` | What this buyer's payments turned out to be worth, a second and later signal than the payment itself |
| `POST /api/checkout/{buyerId}/pay` | Pay this checkout from the demo wallet. 404 when no demo seed is configured |
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
