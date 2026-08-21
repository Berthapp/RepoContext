# Refund Policy

Owner: Payments. Source: https://example.atlassian.net/wiki/spaces/PAY/pages/4711/Refund+Policy

## Window

A captured payment may be refunded within 30 days. After that the request is
rejected. See PAY-142 for the implementation ticket.

## Audit

Every refund is recorded through `AuditLog`, implemented in
`src/billing/audit.ts`. The refund itself lives in `src/billing/refund.ts`.

## Open questions

Partial refunds (PAY-155) are not covered by this policy yet.
