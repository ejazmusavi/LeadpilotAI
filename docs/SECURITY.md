# Security

## Trust boundaries
Internet → Nginx (TLS) → Web / Api (authenticated) → Application → Infrastructure → MySQL/Redis/RabbitMQ/Qdrant (private network only).
External callers: browsers, mobile app, WhatsApp gateway webhooks, payment/lead-ad webhooks, LLM provider (outbound), Google Places (outbound).
Infrastructure services (MySQL, Redis, RabbitMQ, Qdrant, Hangfire storage) are never exposed publicly; RabbitMQ management UI only via admin tunnel.

## Authentication
- ASP.NET Core Identity: Argon2/PBKDF2 hashing (Identity default PBKDF2, configurable iterations), lockout, password policy, email confirmation, password reset tokens (short-lived), optional 2FA for admin/platform roles.
- MVC: secure cookie (HttpOnly, Secure, SameSite=Lax), sliding expiry, session revocation via security stamp.
- API/mobile: short-lived JWT access tokens + rotating refresh tokens stored hashed.

## Authorization
- Permission codes (`Leads.View`, `Campaigns.Execute`, …) stored in `Permission`; roles map to permissions per tenant (`RolePermission`).
- Enforced server-side via policy-based authorization (`[Authorize(Policy="Leads.View")]`) **and** inside use cases for object-level checks. UI hiding is cosmetic only.
- Unauthenticated → 401; authenticated without permission → 403; resource in other tenant → 404.

## Data protection
- Integration credentials (gateway keys, API keys, webhook secrets) encrypted at rest with ASP.NET Data Protection / envelope encryption (`ISecretProtector`), keys stored outside DB and outside source control. Decrypted only in memory, never logged.
- Minimal PII: store only public business contact data by default; personal numbers require recorded lawful basis/consent source.
- Phones normalized to E.164; emails lower-cased; PII masked in logs.
- Retention controls per tenant (message bodies, discovery results, logs); scheduled purge jobs.

## Input and API hardening
FluentValidation on all commands; parameterized EF queries only (no raw SQL from user input; AI never gets SQL access); request size limits; CSV/Excel import: size/row limits, formula-injection neutralization on export, content-type and extension checks, no macro-enabled formats; rate limiting (per tenant/user/IP, stricter on login, webhooks, AI endpoints); anti-forgery on MVC forms; CORS allow-list; security headers (CSP, HSTS, X-Content-Type-Options, frame protections).

## Webhooks
Verify signature/shared secret per gateway documentation (constant-time compare) before parsing; timestamp/replay window where supported; idempotency via unique `(ChannelAccountId, ExternalEventId)`; payload size cap; respond fast and process asynchronously.

## AI security
- Customer messages, scraped web content and uploaded documents are **untrusted input**: delimited in prompts, never treated as instructions.
- System prompts and secrets never returned to users; output filtered for leakage.
- Tools use an allow-list per agent, tenant/permission re-checked server-side, schema-validated args, execution logged (`ToolExecution`); sensitive tools (discounts, payment, trial creation beyond limits) require human approval.
- No payment/demo/discount claims unless a tool returned a verified result.
- Retrieval is tenant-filtered; confidence threshold → escalate to human.

## Messaging compliance controls
Consent + suppression + eligibility gate immediately pre-send; opt-out keywords ("stop", Urdu/Roman-Urdu equivalents) create suppression automatically; per-tenant rate limits and daily caps; audit trail of every outbound message with the eligibility decision snapshot.

## Auditing & monitoring
AuditLog (who/what/when/tenant/IP/before-after summary) for auth events, permission changes, tenant switch, exports, consent changes, tool executions, credential changes. Structured logs, error monitoring, health checks, Hangfire and RabbitMQ dead-letter alerts.

## Secrets & supply chain
No production secrets in git; `.env.example` only; user-secrets in development; dependency pinning and vulnerability scanning in CI; container images run as non-root.

## Threats considered (initial)
Cross-tenant data leakage (IDOR, filter bypass, cache/vector leakage) · credential stuffing · webhook spoofing/replay · prompt injection / data exfiltration through AI · CSV injection · duplicate sends on retry · mass messaging abuse / account ban · SSRF through website-crawling enrichment (block private IP ranges, redirect limits) · privilege escalation through tenant switching.

## Phase 3 controls (CRM) — implemented
- **Consent**: append-only records (source required, optional evidence reference, no future dates); consent is bound to the phone number/e-mail it was given for (changing the value revokes it); granting consent for a suppressed value is refused. Every grant/revocation is audited (without the raw number).
- **Suppression**: unique per tenant+channel+normalised value, `Any` blocks every channel, add/remove audited with masked values, requires `Suppression.Manage` (the list itself is sensitive).
- **Eligibility gate**: consent AND not suppressed AND valid value (AND lead not archived / not in a *Suppressed* stage). Lead score is not an input (structurally absent; unit-tested).
- **Import**: `.csv`/`.xlsx` only, 5 MB / 5 000 rows / 100 columns / 10 000-char cells, magic-byte sniffing both ways, XLSX zip-bomb check (uncompressed size and entry count), formulas never evaluated, files stored under the tenant folder with generated names, imports permission-gated (`Leads.Import`), owner assignment inside a file needs `Leads.Assign`, imported contacts never carry consent.
- **Export**: permission-gated (`Leads.Export`), audited, capped at 10 000 rows (refuses instead of truncating), CSV `'`-prefix / XLSX quote-prefix neutralisation of `= + - @ TAB CR LF`, never includes another tenant's rows.
- **Web**: every state-changing MVC action is POST + antiforgery, destructive ones behind a confirm dialog; output HTML-encoded (tested with a hostile lead name and note); `returnQuery` redirects are restricted to same-site query strings.
- **Static assets** are served anonymously on purpose (the login page needs them); every application page still requires authentication.
