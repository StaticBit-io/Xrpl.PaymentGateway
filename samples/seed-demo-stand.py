#!/usr/bin/env python3
"""Builds the token economy the sample's quote demo needs on a standalone rippled stand.

The demo values everything in USD and takes payment in five things: XRP, two issued tokens under
short currency codes, one issued under the long hex form, and USD itself. None of that exists on a
fresh stand, so this creates it — an issuer, a receiving account for the gateway, and a wallet the
sample can pay itself from — and prints the configuration that goes with it.

Run it against the stand from .ci-config/docker-compose.ci.yml:

    python3 samples/seed-demo-stand.py --write samples/Xrpl.PaymentGateway.SampleApi/appsettings.Development.json

Every account comes from a fixed passphrase, so running it twice against the same stand is a no-op
and running it again after recreating the stand reproduces the same addresses — the configuration it
printed the first time stays valid.

It talks to the admin JSON-RPC port, which is what `submit` with a `secret` requires; that port is
localhost-only in the shipped Compose file. It signs with keys it derives here and never asks for
one, so nothing it touches belongs on a network with value on it.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

# The stand's master account, funded at genesis and the same on every standalone rippled.
MASTER_ACCOUNT = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh"
MASTER_SECRET = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb"

# What the demo shop accepts, and what one unit of it is worth in the quote asset. XRP is priced but
# not issued, so it has no trust line and no balance to hand out.
QUOTE_CURRENCY = "USD"
XRP_RATE = "0.5"

# `RLUSD` is five characters and has no short form on the ledger, so it exists only as the long hex
# code — which is what has to be configured and what the node reports back on every payment in it.
RLUSD_HEX = "524C555344000000000000000000000000000000"

ISSUED = [
    # (currency code, rate in quote-asset units, refused by the demo's fixed-rate source)
    (QUOTE_CURRENCY, None, False),
    ("GEM", "2.5", False),
    (RLUSD_HEX, "1.25", False),
    ("JNK", "1", True),
]

# The pools the prices are read from, as (received asset, its side of the pool, the quote asset's side).
# Each ratio is the rate above, so a demo priced off the ledger lands near one priced off the fixed rates —
# near, not on, because a pool charges a fee and a real size moves the price. `JNK` has no pool on purpose:
# an asset with nothing to price it against is what the operator's queue exists for.
POOLS = [
    ("XRP", "100000", "50000"),
    ("GEM", "20000", "50000"),
    (RLUSD_HEX, "40000", "50000"),
]

# Half a percent, so slippage and the fee are both visible in a quote rather than lost in rounding.
TRADING_FEE = 500

TRUST_LIMIT = "1000000000"
ISSUED_BALANCE = "100000"
ACCOUNT_FUNDING_DROPS = "1000000000"

# The liquidity provider funds both sides of every pool, so it needs XRP by the pool-full rather than by
# the reserve: one pool's XRP side plus the reserves and the creation fees for all of them.
LIQUIDITY_FUNDING_DROPS = "200000000000"

ACCOUNTS = {
    "issuer": "xrplpg-demo-issuer",
    "gateway": "xrplpg-demo-gateway",
    "payer": "xrplpg-demo-payer",
    "liquidity": "xrplpg-demo-liquidity",
}

ASF_DEFAULT_RIPPLE = 8


class RpcError(RuntimeError):
    """A JSON-RPC call the node answered with an error rather than a result."""


def rpc(url, method, params=None):
    body = json.dumps({"method": method, "params": [params or {}]}).encode()
    request = urllib.request.Request(url, body, {"Content-Type": "application/json"})

    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            result = json.loads(response.read())["result"]
    except urllib.error.URLError as problem:
        raise SystemExit(f"cannot reach {url}: {problem.reason}. Is the stand running?")

    # A JSON-RPC error arrives as HTTP 200 with "status": "error" in the body, so urlopen raises for
    # none of them and it has to be read out explicitly.
    if result.get("status") == "error":
        raise RpcError(f"{method}: {result.get('error_message') or result.get('error')}")

    return result


def propose(url, passphrase):
    result = rpc(url, "wallet_propose", {"passphrase": passphrase, "key_type": "secp256k1"})
    return result["account_id"], result["master_seed"]


def sequence(url, account):
    return rpc(url, "account_info", {"account": account, "ledger_index": "validated"})["account_data"]["Sequence"]


def exists(url, account):
    try:
        return "account_data" in rpc(url, "account_info", {"account": account, "ledger_index": "validated"})
    except RpcError:
        return False


def default_ripple(url, account):
    info = rpc(url, "account_info", {"account": account, "ledger_index": "validated"})
    return bool(info.get("account_flags", {}).get("defaultRipple"))


def submit(url, secret, transaction, seq, fee="12"):
    transaction = dict(transaction, Fee=fee, Sequence=seq)
    result = rpc(url, "submit", {"secret": secret, "tx_json": transaction})
    status = result.get("engine_result", "?")

    if status not in ("tesSUCCESS", "terQUEUED"):
        raise SystemExit(
            f"{transaction['TransactionType']} from {transaction['Account']} was rejected: {status}")


def wait_for(condition, what, seconds=90):
    """Waits on a validated ledger. The stand closes one every four seconds through its acceptor."""
    deadline = time.monotonic() + seconds

    while time.monotonic() < deadline:
        if condition():
            return
        time.sleep(2)

    raise SystemExit(f"timed out after {seconds}s waiting for {what}")


def trust_lines(url, account):
    lines = rpc(url, "account_lines", {"account": account, "ledger_index": "validated"})["lines"]
    return {line["currency"]: line["balance"] for line in lines}


def asset(currency, issuer):
    """One side of a pool as amm_info names it."""
    return {"currency": currency} if currency == "XRP" else {"currency": currency, "issuer": issuer}


def pool_exists(url, currency, issuer):
    try:
        return "amm" in rpc(url, "amm_info", {
            "asset": asset(currency, issuer),
            "asset2": asset(QUOTE_CURRENCY, issuer),
            "ledger_index": "validated",
        })
    except RpcError:
        # The node answers a missing pool with an error, which is the ordinary case here rather than a
        # problem: it is what "this pool has not been created yet" looks like.
        return False


def amm_create_fee(url):
    """AMMCreate costs one owner reserve, not the ordinary fee, and the stand decides what that is."""
    info = rpc(url, "server_info")["info"]
    increment = info.get("validated_ledger", {}).get("reserve_inc_xrp", 2)
    return str(int(float(increment) * 1_000_000))


def liquidity_needs():
    """How much of each token the pools consume, summed across them."""
    needs = {QUOTE_CURRENCY: 0.0}
    for currency, own_side, quote_side in POOLS:
        needs[QUOTE_CURRENCY] += float(quote_side)
        if currency != "XRP":
            needs[currency] = needs.get(currency, 0.0) + float(own_side)

    return needs


def seed(url):
    accounts = {role: propose(url, passphrase) for role, passphrase in ACCOUNTS.items()}
    issuer, issuer_seed = accounts["issuer"]
    gateway, gateway_seed = accounts["gateway"]
    payer, payer_seed = accounts["payer"]
    liquidity, liquidity_seed = accounts["liquidity"]
    currencies = [currency for currency, _, _ in ISSUED]
    pooled = [currency for currency, _, _ in POOLS if currency != "XRP"] + [QUOTE_CURRENCY]

    print(f"issuer    {issuer}\ngateway   {gateway}\npayer     {payer}\nliquidity {liquidity}",
          file=sys.stderr)

    # 1. Fund the accounts from the stand's master account. Skipped for one that already exists, which
    #    is what makes a second run against a live stand cost nothing.
    funding = {account: LIQUIDITY_FUNDING_DROPS if account == liquidity else ACCOUNT_FUNDING_DROPS
               for account, _ in accounts.values()}
    unfunded = [account for account in funding if not exists(url, account)]
    if unfunded:
        seq = sequence(url, MASTER_ACCOUNT)
        for offset, account in enumerate(unfunded):
            submit(url, MASTER_SECRET, {
                "TransactionType": "Payment",
                "Account": MASTER_ACCOUNT,
                "Destination": account,
                "Amount": funding[account],
            }, seq + offset)

        wait_for(lambda: all(exists(url, account) for account in funding),
                 "the accounts to validate")

    print("accounts funded", file=sys.stderr)

    # 2. DefaultRipple on the issuer. Without it the issuer's token can only be sent back to the
    #    issuer, and a buyer paying a shop is a move between two holders.
    if not default_ripple(url, issuer):
        submit(url, issuer_seed, {
            "TransactionType": "AccountSet",
            "Account": issuer,
            "SetFlag": ASF_DEFAULT_RIPPLE,
        }, sequence(url, issuer))

        wait_for(lambda: default_ripple(url, issuer), "DefaultRipple on the issuer")

    # 3. Trust lines. The receiving account needs one per issued currency it accepts, or the ledger
    #    rejects the payment before the gateway ever sees it, and so does the demo payer.
    for holder, holder_seed, wanted in (
            (gateway, gateway_seed, currencies),
            (payer, payer_seed, currencies),
            (liquidity, liquidity_seed, pooled)):
        held = trust_lines(url, holder)
        missing = [currency for currency in wanted if currency not in held]
        if not missing:
            continue

        seq = sequence(url, holder)
        for offset, currency in enumerate(missing):
            submit(url, holder_seed, {
                "TransactionType": "TrustSet",
                "Account": holder,
                "LimitAmount": {"currency": currency, "issuer": issuer, "value": TRUST_LIMIT},
            }, seq + offset)

    wait_for(lambda: set(currencies) <= set(trust_lines(url, gateway))
             and set(currencies) <= set(trust_lines(url, payer))
             and set(pooled) <= set(trust_lines(url, liquidity)), "the trust lines")
    print("trust lines open", file=sys.stderr)

    # 4. Give the demo payer something to pay with.
    balances = trust_lines(url, payer)
    empty = [currency for currency in currencies if float(balances.get(currency, "0")) <= 0]
    if empty:
        seq = sequence(url, issuer)
        for offset, currency in enumerate(empty):
            submit(url, issuer_seed, {
                "TransactionType": "Payment",
                "Account": issuer,
                "Destination": payer,
                "Amount": {"currency": currency, "issuer": issuer, "value": ISSUED_BALANCE},
            }, seq + offset)

        wait_for(lambda: all(float(trust_lines(url, payer).get(currency, "0")) > 0
                             for currency in currencies), "the payer's balances")

    print("payer funded", file=sys.stderr)

    # 5. The liquidity provider, holding exactly what its pools will take.
    needs = liquidity_needs()
    balances = trust_lines(url, liquidity)
    short = {currency: amount for currency, amount in needs.items()
             if float(balances.get(currency, "0")) < amount}
    if short:
        seq = sequence(url, issuer)
        for offset, (currency, amount) in enumerate(short.items()):
            submit(url, issuer_seed, {
                "TransactionType": "Payment",
                "Account": issuer,
                "Destination": liquidity,
                "Amount": {"currency": currency, "issuer": issuer, "value": f"{amount:.6f}"},
            }, seq + offset)

        wait_for(lambda: all(float(trust_lines(url, liquidity).get(currency, "0")) >= amount
                             for currency, amount in needs.items()),
                 "the liquidity provider's balances")

    # 6. The pools. Each is created once; an existing one is left alone, because recreating it is rejected
    #    and depositing into it would move the very price this demo quotes.
    fee = amm_create_fee(url)
    for currency, own_side, quote_side in POOLS:
        if pool_exists(url, currency, issuer):
            continue

        # AMMCreate takes the two sides as its two amounts, and XRP is the one written in drops.
        own = ({"currency": currency, "issuer": issuer, "value": own_side} if currency != "XRP"
               else str(int(float(own_side) * 1_000_000)))

        submit(url, liquidity_seed, {
            "TransactionType": "AMMCreate",
            "Account": liquidity,
            "Amount": own,
            "Amount2": {"currency": QUOTE_CURRENCY, "issuer": issuer, "value": quote_side},
            "TradingFee": TRADING_FEE,
        }, sequence(url, liquidity), fee=fee)

        wait_for(lambda currency=currency: pool_exists(url, currency, issuer), f"the {currency} pool")

    print("pools created", file=sys.stderr)
    return issuer, gateway, payer, payer_seed


def configuration(node, issuer, gateway, payer_seed):
    """The sample's configuration for the accounts just created, ready to be written or pasted."""
    pairs = [{"Currency": "XRP", "Rate": float(XRP_RATE)}]
    refused = []

    for currency, rate, is_refused in ISSUED:
        if rate is None:
            # The quote asset has no pair: QuotePair refuses to quote an asset against itself, and a
            # payment already in it needs no conversion.
            continue

        pairs.append({"Currency": currency, "Issuer": issuer, "Rate": float(rate)})
        if is_refused:
            refused.append({"Currency": currency, "Issuer": issuer})

    return {
        "Xrpl": {
            "Address": gateway,
            "Nodes": [node],
            "Demo": {"PayerSeed": payer_seed},
            "Quotes": {
                # The pools above are what backs this. Drop the line and the sample falls back to the
                # fixed rates below, which is what it does with no stand at all.
                "Source": "amm",
                "QuoteCurrency": QUOTE_CURRENCY,
                "QuoteIssuer": issuer,
                "Pairs": pairs,
                "RefusedCurrencies": refused,
            },
        }
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--rpc", default=os.environ.get("XRPLPG_RPC_URL", "http://localhost:5005/"),
        help="the stand's admin JSON-RPC endpoint (default: %(default)s)")
    parser.add_argument(
        "--node", default=os.environ.get("XRPLPG_NODE_URL", "ws://localhost:6006"),
        help="the WebSocket endpoint to put in the configuration (default: %(default)s)")
    parser.add_argument(
        "--write", metavar="PATH",
        help="write the configuration to PATH instead of printing it to stdout")
    arguments = parser.parse_args()

    issuer, gateway, payer, payer_seed = seed(arguments.rpc)
    config = json.dumps(configuration(arguments.node, issuer, gateway, payer_seed), indent=2) + "\n"

    if arguments.write:
        with open(arguments.write, "w", encoding="utf-8") as file:
            file.write(config)
        print(f"configuration written to {arguments.write}", file=sys.stderr)
        print(f"it holds {payer}'s seed — keep it off a network with value on it", file=sys.stderr)
    else:
        sys.stdout.write(config)


if __name__ == "__main__":
    main()
