# Security policy

## Reporting a vulnerability

Report privately through GitHub's [private vulnerability
reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository's **Security** tab. Do not open a public issue for a vulnerability.

Include what you did, what happened, and what you expected. A failing test or a transaction hash on a
public network makes a report far easier to act on.

## Supported versions

The library has not had a stable release yet. Until it does, fixes land on the latest version only.

## What is in scope

This library receives and records payments. The findings that matter most are the ones that break one of
its stated guarantees:

- A payment that reaches the receiving account but is never recorded, or is recorded with the wrong amount,
  currency, issuer, or sender.
- A payment attributed to the wrong buyer, or a destination tag issued to two buyers.
- The ledger cursor advancing past ledgers that were never searched, which would turn a visible gap into a
  permanent one.
- A crafted transaction that stops the monitor — metadata is written by whoever built the payment path, so
  input reaching the balance-change reader is not trusted.

## What is not a vulnerability

- Running more than one monitor against the same receiving account. It is documented as unsupported.
- A receiving account that holds DEX offers or has `DefaultRipple` enabled. The
  [README](README.md#what-it-expects-of-the-receiving-account) states what the account must be, and
  payments arriving in a shape that contradicts it are reported through `AnomalyCount` by design.
- MPT payments not being recorded. That is a documented limitation, and such a payment raises
  `AnomalyCount` rather than disappearing quietly.
- Anything in `samples/`. The sample is a demonstration, not a hardened deployment: it has no
  authentication, and its endpoints expose payment data to anyone who can reach them.
