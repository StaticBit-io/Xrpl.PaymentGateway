"use strict";

const POLL_PAYMENTS_MS = 2000;
const POLL_HEALTH_MS = 5000;
const POLL_UNRESOLVED_MS = 4000;

const el = (id) => document.getElementById(id);

/** Transaction hashes the page has already rendered, so a later poll does not repeat them. */
const shown = new Set();

/** Transaction hashes whose valuation line has already been appended to their payment row. */
const valuedShown = new Set();

let buyerId = null;
let paymentsTimer = null;

/**
 * TimeSpan comes over the wire as .NET's constant format, "[-][d.]hh:mm:ss[.fffffff]". Just enough
 * parsing to show it as "2m 14s" rather than the raw string.
 */
function formatDuration(timeSpan) {
    if (!timeSpan) {
        return "";
    }

    const colonIndex = timeSpan.indexOf(":");
    const dotIndex = timeSpan.indexOf(".");
    const hasDays = dotIndex !== -1 && dotIndex < colonIndex;

    const days = hasDays ? parseInt(timeSpan.slice(0, dotIndex), 10) : 0;
    const rest = hasDays ? timeSpan.slice(dotIndex + 1) : timeSpan;
    const [hoursPart, minutesPart, secondsPart] = rest.split(":");

    const hours = parseInt(hoursPart, 10) + days * 24;
    const minutes = parseInt(minutesPart, 10);
    const seconds = Math.floor(parseFloat(secondsPart));

    if (hours > 0) {
        return `${hours}h ${minutes}m`;
    }
    if (minutes > 0) {
        return `${minutes}m ${seconds}s`;
    }
    return `${seconds}s`;
}

/**
 * A currency code as a person reads it. Three characters is all the ledger's short form holds, so anything
 * longer — RLUSD, say — travels as forty hex characters, and that is what the node reports and what has to
 * be configured. Printing it raw makes a page unreadable, so this decodes the name back out where the code
 * is one of the two forms that carry an ASCII name, and leaves anything else exactly as it came.
 */
function displayCurrency(code) {
    if (typeof code !== "string" || !/^[0-9a-fA-F]{40}$/.test(code)) {
        return code;
    }

    const bytes = [];
    for (let i = 0; i < 40; i += 2) {
        bytes.push(parseInt(code.slice(i, i + 2), 16));
    }

    const zero = (from, to) => bytes.slice(from, to).every(byte => byte === 0);
    const printable = (slice) => slice.length > 0 && slice.every(byte => byte >= 0x20 && byte <= 0x7e);
    const text = (slice) => String.fromCharCode(...slice);

    // All zeros is how XRP itself is written in this form, which the gateway canonicalizes the same way.
    if (zero(0, 20)) {
        return "XRP";
    }

    // The short form padded out to forty characters: twelve zero bytes, three of name, five zero bytes.
    if (bytes[0] === 0) {
        const name = bytes.slice(12, 15);
        return zero(0, 12) && zero(15, 20) && printable(name) ? text(name).trim() : code;
    }

    // The long form: the name from the front, zero-padded to the end.
    const end = bytes.indexOf(0);
    const name = end === -1 ? bytes : bytes.slice(0, end);
    return zero(name.length, 20) && printable(name) ? text(name) : code;
}

// ---------------------------------------------------------------------------- monitor strip

/**
 * The gateway's own state. Streaming is the healthy one; catching up and reconnecting are transient and
 * worth showing rather than hiding, because a payment sent during them arrives late, not never.
 */
const MONITOR_STATES = {
    Stopped: ["bad", "the monitor is not running"],
    Connecting: ["warn", "connecting to a node"],
    CatchingUp: ["warn", "catching up on missed ledgers"],
    Streaming: ["ok", "watching the ledger"],
    Reconnecting: ["warn", "reconnecting"],
    NetworkStalled: ["warn", "the network is not validating ledgers"],
    StoreUnavailable: ["bad", "the store is unavailable"],
    HistoryGap: ["bad", "a ledger range could not be verified"],
};

/**
 * Hides the quote-health half of the one status strip. Used when there is no pair configured (404) and
 * when the API cannot be reached at all — in the latter case the monitor half already says "the sample API
 * is not answering", and a second copy of the same fact under a different id would just be the strip
 * repeating itself.
 */
function hideQuoteHealth() {
    el("quote-monitor-sep").hidden = true;
    el("quote-monitor-dot").hidden = true;
    el("quote-monitor-text").hidden = true;
}

async function pollHealth() {
    try {
        // 503 is the expected answer whenever the monitor is not streaming, so the body is read either
        // way rather than treated as a failure.
        const response = await fetch("/api/health");
        const report = await response.json();
        const [tone, text] = MONITOR_STATES[report.state] ?? ["warn", `state ${report.state}`];

        el("monitor-dot").className = `monitor-dot ${tone}`;

        const parts = [text];
        if (report.currentNode) {
            parts.push(report.currentNode);
        }
        if (report.lastValidatedLedger) {
            parts.push(`ledger ${report.lastValidatedLedger}`);
        }
        if (report.anomalyCount > 0) {
            parts.push(`${report.anomalyCount} anomalies`);
        }

        el("monitor-text").textContent = parts.join(" · ");
    } catch {
        el("monitor-dot").className = "monitor-dot bad";
        el("monitor-text").textContent = "the sample API is not answering";
        hideQuoteHealth();
    }
}

el("reconcile").addEventListener("click", async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    button.textContent = "reconciling…";

    try {
        const result = await (await fetch("/api/reconcile", { method: "POST" })).json();
        button.textContent = result.skipped
            ? "already running"
            : `redelivered ${result.redeliveredCount}, recovered ${result.recoveredCount}`;
    } catch {
        button.textContent = "reconciliation failed";
    } finally {
        window.setTimeout(() => {
            button.textContent = "run reconciliation";
            button.disabled = false;
        }, 4000);
    }
});

// ---------------------------------------------------------------------------- quote health strip

/**
 * 404 means no quote pairs are configured — the sample runs exactly as it does without this feature, so
 * the strip stays hidden rather than reporting on something that does not exist.
 */
async function pollQuoteHealth() {
    try {
        const response = await fetch("/api/quotes/health");
        if (response.status === 404) {
            hideQuoteHealth();
            return;
        }

        const report = await response.json();

        el("quote-monitor-sep").hidden = false;
        el("quote-monitor-dot").hidden = false;
        el("quote-monitor-text").hidden = false;
        el("quote-monitor-dot").className = `monitor-dot ${report.isHealthy ? "ok" : "warn"}`;

        const parts = [`${report.pairsWithFreshQuote}/${report.configuredPairs} pairs fresh`];
        if (report.pairsFailing > 0) {
            parts.push(`${report.pairsFailing} refresh${report.pairsFailing === 1 ? "" : "es"} failing`);
        }
        if (report.pendingValuations > 0) {
            const oldest = report.oldestPendingAge ? `, oldest waiting ${formatDuration(report.oldestPendingAge)}` : "";
            parts.push(`${report.pendingValuations} payment${report.pendingValuations === 1 ? "" : "s"} pending valuation${oldest}`);
        }
        if (report.failedValuations > 0) {
            parts.push(`${report.failedValuations} failed, waiting on an operator`);
        }

        el("quote-monitor-text").textContent = parts.join(" · ");
    } catch {
        // The main half of the strip reports "the sample API is not answering" when the fetch itself
        // fails (pollHealth hits the same wall) — this half just steps out of the way instead of
        // repeating that under a different id.
        hideQuoteHealth();
    }
}

// ---------------------------------------------------------------------------- checkout

el("checkout-form").addEventListener("submit", async (event) => {
    event.preventDefault();

    const requested = el("buyer").value.trim();
    if (!requested) {
        return;
    }

    const button = el("checkout");
    const error = el("checkout-error");
    button.disabled = true;
    error.hidden = true;

    try {
        const response = await fetch(`/api/checkout/${encodeURIComponent(requested)}`, { method: "POST" });
        if (!response.ok) {
            throw new Error(await response.text());
        }

        const instructions = await response.json();
        buyerId = requested;
        shown.clear();
        valuedShown.clear();

        el("pay-address").textContent = instructions.address;
        el("pay-tag").textContent = instructions.destinationTag;
        el("pay-xaddress").textContent = instructions.xAddress;
        el("pay-qr").src = `/api/checkout/${encodeURIComponent(requested)}/qr.svg`;
        el("waiting-tag").textContent = instructions.destinationTag;
        el("cli-snippet").textContent = standaloneSnippet(instructions);

        el("step-pay").hidden = false;
        el("step-wait").hidden = false;
        el("payments").replaceChildren();
        el("waiting").hidden = false;

        startPollingPayments();
        loadCheckoutPricing(requested);
    } catch (problem) {
        error.textContent = `Could not get instructions: ${problem.message}`;
        error.hidden = false;
    } finally {
        button.disabled = false;
    }
});

/**
 * The price panel and, under it, the demo wallet's buttons. Chained rather than run side by side: what a
 * button offers to send is the amount the price panel just worked out, so the second needs the first.
 */
async function loadCheckoutPricing(buyer) {
    const prices = await loadPrice(buyer);
    await loadDemoAssets(buyer, prices);
}

/**
 * What this checkout is priced at, before any payment exists — ExactOutput against the demo item's fixed
 * quote price, one line per pair that holds a usable reading. Empty when none does (including every refused
 * pair, which never captures one), in which case the panel stays hidden rather than showing an empty price.
 */
async function loadPrice(buyer) {
    try {
        const prices = await (await fetch(`/api/checkout/${encodeURIComponent(buyer)}/price`)).json();
        if (prices.length === 0) {
            el("pay-price-field").hidden = true;
            return [];
        }

        // Every pair prices into the same asset, so the invoice total is stated once above the list rather
        // than repeated on each line.
        el("pay-price-total").textContent =
            `${prices[0].quotePrice} ${displayCurrency(prices[0].quoteCurrency)}`;

        const list = el("pay-price-list");
        list.replaceChildren();
        for (const price of prices) {
            const item = document.createElement("li");

            const amount = document.createElement("span");
            amount.className = "price-amount";
            amount.textContent = `${price.inputAmount} ${displayCurrency(price.currency)}`;

            const age = document.createElement("span");
            age.className = "hint";

            // Slippage is what separates a price for this size from a price per unit. A source that
            // ignores size reports none, and then the line simply does not mention it.
            const slippage = price.slippagePercent > 0
                ? `, ${price.slippagePercent.toFixed(3)}% below spot`
                : "";

            age.textContent = `priced ${formatDuration(price.age)} ago at ledger ${price.ledgerIndex}${slippage}`
                + (price.isStale ? " — past the age limit, served anyway" : "");

            item.append(amount, age);
            list.appendChild(item);
        }

        el("pay-price-field").hidden = false;
        return prices;
    } catch {
        el("pay-price-field").hidden = true;
        return [];
    }
}

// ---------------------------------------------------------------------------- paying from the demo wallet

/**
 * The demo wallet's buttons, one per asset the sample accepts. 404 means no seed is configured, which is
 * the shipped default — the block stays hidden and the page waits for money sent from somewhere else.
 */
async function loadDemoAssets(buyer, prices) {
    try {
        const response = await fetch("/api/demo");
        if (!response.ok) {
            el("pay-demo-field").hidden = true;
            return;
        }

        const demo = await response.json();
        el("pay-demo-payer").textContent = `paying from ${demo.payer}`;

        const list = el("demo-assets");
        list.replaceChildren();
        for (const asset of demo.assets) {
            list.appendChild(renderDemoAsset(buyer, asset, suggestedAmount(asset, prices)));
        }

        el("pay-demo-field").hidden = false;
    } catch {
        el("pay-demo-field").hidden = true;
    }
}

/**
 * What to put in an asset's amount box. The priced assets get exactly what the panel above asks for; the
 * quote asset gets the invoice total, since it needs no conversion; anything left — a pair with no usable
 * reading, such as one this demo's source refuses — gets a round number to send, because the point of
 * sending it is to watch it land unpriced.
 */
function suggestedAmount(asset, prices) {
    if (asset.isQuoteAsset) {
        return prices.length > 0 ? prices[0].quotePrice : 10;
    }

    const priced = prices.find(price =>
        price.currency === asset.currency && (price.issuer ?? null) === (asset.issuer ?? null));

    return priced ? priced.inputAmount : 10;
}

function renderDemoAsset(buyer, asset, amount) {
    const item = document.createElement("li");
    item.className = "demo-asset";

    const code = document.createElement("code");
    code.className = "demo-asset-code";
    code.textContent = displayCurrency(asset.currency);
    code.title = asset.issuer ? `${asset.currency} issued by ${asset.issuer}` : "the ledger's native asset";

    const input = document.createElement("input");
    input.type = "number";
    input.step = "any";
    input.min = "0";
    input.value = amount;

    const button = document.createElement("button");
    button.type = "button";
    button.textContent = "send";

    const status = document.createElement("span");
    status.className = "demo-asset-status";

    button.addEventListener("click", () => sendDemoPayment(buyer, asset, input, button, status));

    item.append(code, input, button, status);
    return item;
}

/**
 * Submits one payment and reports only what the node made of the submission. Deliberately not a success
 * message about the payment itself: the payment is not on the ledger yet, and the row that says it is
 * arrives below, through the gateway, the same way it would for money sent from anywhere else.
 */
async function sendDemoPayment(buyer, asset, input, button, status) {
    const amount = parseFloat(input.value);
    if (!(amount > 0)) {
        status.textContent = "enter an amount";
        return;
    }

    button.disabled = true;
    status.textContent = "submitting…";

    try {
        const response = await fetch(`/api/checkout/${encodeURIComponent(buyer)}/pay`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ currency: asset.currency, issuer: asset.issuer, amount }),
        });

        const result = await response.json().catch(() => null);
        if (response.ok) {
            status.textContent = `submitted — waiting for the ledger`;
        } else {
            status.textContent = result?.engineResult ?? result?.message ?? `failed (${response.status})`;
        }
    } catch {
        status.textContent = "request failed";
    } finally {
        button.disabled = false;
    }
}

/**
 * Three calls against the standalone stand's admin port. Deliberately not chained through shell
 * variables: the values are worth seeing, and a copied one-liner that half-works is worse than steps.
 */
function standaloneSnippet(instructions) {
    const rpc = "curl -s -X POST http://localhost:5005/ -H 'Content-Type: application/json' -d";
    const master = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";
    const masterAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";

    const fund = {
        method: "submit",
        params: [{
            secret: master,
            tx_json: {
                TransactionType: "Payment",
                Account: masterAccount,
                Destination: "ACCOUNT",
                Amount: "500000000",
            },
        }],
    };

    const pay = {
        method: "submit",
        params: [{
            secret: "SEED",
            tx_json: {
                TransactionType: "Payment",
                Account: "ACCOUNT",
                Destination: instructions.address,
                DestinationTag: instructions.destinationTag,
                Amount: "12500000",
            },
        }],
    };

    return [
        "# 1. make a throwaway account, and note account_id and master_seed from the output",
        `${rpc} '{"method":"wallet_propose"}'`,
        "",
        "# 2. fund it from the stand's master account, with account_id in place of ACCOUNT",
        `${rpc} '${JSON.stringify(fund)}'`,
        "",
        "# 3. wait a few seconds for a ledger to close, then pay this checkout:",
        "#    master_seed for SEED, account_id for ACCOUNT. 12500000 drops is 12.5 XRP.",
        `${rpc} '${JSON.stringify(pay)}'`,
    ].join("\n");
}

// ---------------------------------------------------------------------------- waiting for the money

function startPollingPayments() {
    if (paymentsTimer !== null) {
        window.clearInterval(paymentsTimer);
    }

    pollPayments();
    paymentsTimer = window.setInterval(pollPayments, POLL_PAYMENTS_MS);
}

async function pollPayments() {
    if (buyerId === null) {
        return;
    }

    try {
        const payments = await (await fetch(`/api/checkout/${encodeURIComponent(buyerId)}/payments`)).json();
        for (const payment of payments) {
            if (!shown.has(payment.transactionHash)) {
                shown.add(payment.transactionHash);
                el("payments").prepend(renderPayment(payment));
            }
        }

        if (shown.size > 0) {
            el("waiting").hidden = true;
        }
    } catch {
        // The API blinked. The next tick asks again; nothing here is worth interrupting the page for.
    }

    await pollValuations();
}

function renderPayment(payment) {
    const item = document.createElement("li");
    item.dataset.hash = payment.transactionHash;

    const amount = document.createElement("div");
    amount.className = "paid-amount";
    amount.textContent = `${payment.value} ${displayCurrency(payment.currency)} received`;

    const meta = document.createElement("p");
    meta.className = "paid-meta";

    const facts = [
        `from ${payment.sender}`,
        `ledger ${payment.ledgerIndex}`,
        `tag ${payment.destinationTag ?? "none"}`,
    ];
    if (payment.issuer) {
        facts.push(`issuer ${payment.issuer}`);
    }
    facts.push(payment.transactionHash);

    meta.textContent = facts.join(" · ");

    item.append(amount, meta);

    // Already the asset every pair prices into (USD in this sample): QuotePair refuses to quote an asset
    // against itself, so there is no pair for it and the library queues no valuation for it. Shown at its
    // own amount right away, rather than leaving the row waiting on a signal that will never arrive.
    if (payment.isQuoteAsset) {
        valuedShown.add(payment.transactionHash);
        const line = document.createElement("div");
        line.className = "valuation valued";
        line.textContent =
            `worth ${payment.value} ${displayCurrency(payment.currency)} — already the quote asset, no conversion needed`;
        item.appendChild(line);
        return item;
    }

    // Everything else is waiting on a second signal, and says so. A row that showed the amount and nothing
    // else could not be told apart from one whose valuation is never coming — which is exactly the case a
    // buyer most needs to hear about.
    const pending = document.createElement("div");
    pending.className = "valuation pending";
    pending.textContent = "waiting to be priced…";
    item.appendChild(pending);

    return item;
}

/**
 * A payment's value, fetched separately from the payment itself: it is computed afterwards, sometimes
 * seconds later, and this poll is what makes that ordering visible on the page rather than hiding it
 * behind a row that only ever appears once it is already priced.
 */
async function pollValuations() {
    if (buyerId === null) {
        return;
    }

    try {
        const valuations = await (await fetch(`/api/checkout/${encodeURIComponent(buyerId)}/valuations`)).json();
        for (const valuation of valuations) {
            if (valuedShown.has(valuation.transactionHash)) {
                continue;
            }

            const row = document.querySelector(`#payments li[data-hash="${CSS.escape(valuation.transactionHash)}"]`);
            if (!row) {
                // The valuation reached the page before the payment row did — possible on a fresh load
                // that catches both polls close together. Nothing to attach it to yet; caught next tick.
                continue;
            }

            valuedShown.add(valuation.transactionHash);

            // Replaces the waiting line rather than stacking under it: one row, one thing said about its
            // value at a time.
            const line = renderValuation(valuation);
            const existing = row.querySelector(".valuation");
            if (existing) {
                existing.replaceWith(line);
            } else {
                row.appendChild(line);
            }
        }
    } catch {
        // Same as pollPayments: the next tick asks again.
    }
}

function renderValuation(valuation) {
    const line = document.createElement("div");

    if (valuation.state === "Valued" || valuation.state === "ValuedManually") {
        line.className = "valuation valued";
        const manually = valuation.state === "ValuedManually" ? " — an operator's rate" : "";
        const quoteCurrency = displayCurrency(valuation.readableQuoteCurrency ?? "?");
        line.textContent = `worth ${valuation.quoteAmount} ${quoteCurrency}${manually}`;
    } else if (valuation.state === "WrittenOff") {
        line.className = "valuation written-off";
        line.textContent = `written off: ${valuation.writeOffReason ?? "no reason given"}`;
    } else {
        // Failed: the automatic pipeline gave up on a per-entry, deterministic cause and an operator has
        // not resolved it yet.
        line.className = "valuation failed";
        line.textContent = `could not be priced: ${valuation.failureReason ?? "unknown reason"}`;
    }

    return line;
}

// ---------------------------------------------------------------------------- unresolved payments

/**
 * The operator's queue. Not scoped to a buyer — an operator works this list independent of who paid —
 * so it polls on its own timer rather than piggybacking on pollPayments the way pollValuations does.
 */
async function pollUnresolved() {
    try {
        const response = await fetch("/api/quotes/unresolved");
        if (response.status === 404) {
            el("step-unresolved").hidden = true;
            return;
        }

        el("step-unresolved").hidden = false;
        const page = await response.json();

        // Rebuilt by hash rather than wholesale: a blind replaceChildren() every four seconds would wipe
        // out a rate an operator is mid-typing into a still-unresolved row. An entry present in both the
        // old and new page keeps its existing DOM — and whatever is currently in its inputs — untouched;
        // only rows that appeared or disappeared are added or removed.
        const list = el("unresolved-list");
        const seen = new Set();
        for (const entry of page.items) {
            seen.add(entry.transactionHash);
            if (!list.querySelector(`li[data-hash="${CSS.escape(entry.transactionHash)}"]`)) {
                list.appendChild(renderUnresolved(entry));
            }
        }
        for (const row of Array.from(list.children)) {
            if (!seen.has(row.dataset.hash)) {
                row.remove();
            }
        }

        el("unresolved-empty").hidden = page.items.length !== 0;
        markPaymentsWaitingOnOperator(page.items);
    } catch {
        // Next tick tries again.
    }
}

/**
 * How long an entry sits in the operator's queue before its payment row stops saying "waiting" and starts
 * naming the operator. The queue itself is read with a minimum age of zero here, so an entry appears on it
 * the instant it exists — including one a healthy pair is about to price a second later — and saying a
 * buyer's money is stuck the moment it arrives would be wrong far more often than right.
 */
const STUCK_AFTER_MS = 30000;

/**
 * The buyer's half of the operator queue: a payment nobody could price is money that arrived and has no
 * value against it yet, and the row should say that rather than sit blank. Matched by hash, so it covers
 * only rows this page is showing; the queue itself is not scoped to one buyer.
 */
function markPaymentsWaitingOnOperator(entries) {
    for (const entry of entries) {
        if (Date.now() - Date.parse(entry.enqueuedAt) < STUCK_AFTER_MS) {
            continue;
        }

        const row = document.querySelector(`#payments li[data-hash="${CSS.escape(entry.transactionHash)}"]`);
        const line = row?.querySelector(".valuation.pending");
        if (line) {
            line.className = "valuation stuck";
            line.textContent = "received, but it could not be priced — an operator has been given it";
        }
    }
}

function renderUnresolved(entry) {
    const item = document.createElement("li");
    item.dataset.hash = entry.transactionHash;

    const amount = document.createElement("div");
    amount.className = "unresolved-amount";
    amount.textContent = `${entry.amount} ${displayCurrency(entry.readableCurrency ?? "?")}`;

    const meta = document.createElement("p");
    meta.className = "unresolved-meta";
    const reason = entry.failureReason ?? "queued, no usable reading yet";
    meta.textContent = `${entry.state} · ${reason} · enqueued ${entry.enqueuedAt} · ${entry.transactionHash}`;

    const actions = document.createElement("div");
    actions.className = "unresolved-actions";

    const rateInput = document.createElement("input");
    rateInput.type = "number";
    rateInput.step = "any";
    rateInput.min = "0";
    rateInput.placeholder = "rate";

    const settleButton = document.createElement("button");
    settleButton.type = "button";
    settleButton.textContent = "settle";

    const reasonInput = document.createElement("input");
    reasonInput.type = "text";
    reasonInput.placeholder = "reason";

    const writeOffButton = document.createElement("button");
    writeOffButton.type = "button";
    writeOffButton.className = "secondary";
    writeOffButton.textContent = "write off";

    const status = document.createElement("span");
    status.className = "unresolved-status";

    settleButton.addEventListener("click", () => resolveUnresolved(
        entry.transactionHash, "settle", { rate: parseFloat(rateInput.value) }, settleButton, status,
        () => parseFloat(rateInput.value) > 0 || "enter a positive rate"));

    writeOffButton.addEventListener("click", () => resolveUnresolved(
        entry.transactionHash, "write-off", { reason: reasonInput.value.trim() || "no reason given" },
        writeOffButton, status));

    actions.append(rateInput, settleButton, reasonInput, writeOffButton, status);
    item.append(amount, meta, actions);
    return item;
}

/**
 * Both operator actions land through IUnresolvedValuationAdmin and, from there, the same
 * IPaymentValuedHandler an automatic valuation reaches — so a short delay and a re-poll of the buyer's
 * valuations is enough to see the resolution arrive the same way a priced payment does.
 */
async function resolveUnresolved(transactionHash, action, body, button, status, validate) {
    if (validate) {
        const validation = validate();
        if (validation !== true) {
            status.textContent = validation;
            return;
        }
    }

    button.disabled = true;
    status.textContent = "";

    try {
        const response = await fetch(`/api/quotes/unresolved/${encodeURIComponent(transactionHash)}/${action}`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });

        if (response.ok) {
            window.setTimeout(pollUnresolved, 500);
            window.setTimeout(pollValuations, 750);
        } else {
            const problem = await response.json().catch(() => null);
            status.textContent = problem?.message ?? `request failed (${response.status})`;
        }
    } catch {
        status.textContent = "request failed";
    } finally {
        button.disabled = false;
    }
}

// ---------------------------------------------------------------------------- copy buttons

document.addEventListener("click", async (event) => {
    const button = event.target.closest("button.copy");
    if (!button) {
        return;
    }

    const text = el(button.dataset.copy).textContent;

    try {
        await navigator.clipboard.writeText(text);
        button.textContent = "copied";
    } catch {
        // The clipboard API needs a secure context, and a sample often runs on plain http.
        button.textContent = "copy failed";
    }

    window.setTimeout(() => {
        button.textContent = "copy";
    }, 1500);
});

// ---------------------------------------------------------------------------- start

el("buyer").value = `buyer-${Math.floor(Math.random() * 9000 + 1000)}`;
pollHealth();
window.setInterval(pollHealth, POLL_HEALTH_MS);
pollQuoteHealth();
window.setInterval(pollQuoteHealth, POLL_HEALTH_MS);
pollUnresolved();
window.setInterval(pollUnresolved, POLL_UNRESOLVED_MS);
