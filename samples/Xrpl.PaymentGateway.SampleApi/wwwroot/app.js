"use strict";

const POLL_PAYMENTS_MS = 2000;
const POLL_HEALTH_MS = 5000;

const el = (id) => document.getElementById(id);

/** Transaction hashes the page has already rendered, so a later poll does not repeat them. */
const shown = new Set();

let buyerId = null;
let paymentsTimer = null;

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

        el("pay-address").textContent = instructions.address;
        el("pay-tag").textContent = instructions.destinationTag;
        el("pay-uri").textContent = instructions.paymentUri;
        el("waiting-tag").textContent = instructions.destinationTag;
        el("cli-snippet").textContent = standaloneSnippet(instructions);

        el("step-pay").hidden = false;
        el("step-wait").hidden = false;
        el("payments").replaceChildren();
        el("waiting").hidden = false;

        startPollingPayments();
    } catch (problem) {
        error.textContent = `Could not get instructions: ${problem.message}`;
        error.hidden = false;
    } finally {
        button.disabled = false;
    }
});

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
}

function renderPayment(payment) {
    const item = document.createElement("li");

    const amount = document.createElement("div");
    amount.className = "paid-amount";
    amount.textContent = `${payment.value} ${payment.currency} received`;

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
    return item;
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
