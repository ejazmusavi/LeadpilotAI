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


## Phase 4 — discovery security
**SSRF-safe website fetching** (`UrlGuard`, `SafeWebFetcher`, `SafeHttpClientFactory`; unit-tested incl. real sockets):
- URL checks before every request and on **every redirect hop**: scheme http/https only; no credentials in the URL; ports 80/443 only; host names such as `localhost`, `*.local`, `*.internal`, `metadata.google.internal`, single-label names rejected; IP literals (including decimal/hex/octal forms, which `Uri` normalises) checked; the name is resolved and **every** answer must be public.
- Address policy (`IpAddressClassifier`): rejects 0/8, 10/8, 100.64/10 (CGNAT), 127/8, 169.254/16 (cloud metadata 169.254.169.254), 172.16/12, 192.0.0/24, 192.0.2/24, 192.88.99/24, 192.168/16, 198.18/15, 198.51.100/24, 203.0.113/24, ≥224/4; IPv6 `::`, `::1`, fc00::/7 (incl. fd00:ec2::254), fe80::/10, fec0::/10, ff00::/8, 2001:db8::/32, Teredo, 100::/64; IPv4-mapped, NAT64 and 6to4 addresses are classified by the IPv4 they embed; only 2000::/3 global unicast passes.
- **DNS rebinding:** the HTTP handler resolves the name itself inside `SocketsHttpHandler.ConnectCallback`, filters with the same policy and connects the socket to the validated address; there is no second lookup between check and connect (tested with a resolver that answers public first, loopback afterwards).
- Redirects are never followed by the handler (3 hops max, each re-validated); no proxy, no cookies, no `Authorization`; total timeout 10 s; body ≤ 1 MB read incrementally; only `text/html`, `application/xhtml+xml`, `text/plain`; 401/403/407 are "protected content" and never retried; a page with a password field or `noindex` is not read.
- Only `ISafeHttpClientFactory` may produce a client for arbitrary hosts (architecture test: no other type in that namespace references `HttpClient`/`IHttpClientFactory`; a source scan rejects `new HttpClient`/handlers). The Google adapter uses its own named client to a fixed, configured base URL (not user input).
- Logging: only the host and failure reason; never the path/query, never the API key.

**Credentials:** connector keys are encrypted with ASP.NET Data Protection (`ISecretProtector`, `enc:v1:` prefix, shared key ring `DataProtection:KeysPath` — **the Worker must mount the same key ring as Web**, as compose does), shown only as a 4-character hint, excluded from DTOs (architecture test), audit text and logs; provider error messages are scrubbed of the key. Keys can be removed; saving needs `Integrations.Manage`.

**Data-source rules enforced in code:** discovery never writes consent (contacts approved from discovery are `DataOrigin.Public` with no `ConsentRecord`; `IContactEligibilityService` blocks them on every channel/purpose until consent exists — integration-tested, and the same test shows consent opens the gate); no scraping of protected platforms (no such connector exists; the stubs return nothing); every unverified value is labelled; nothing is invented (the extractor has no field for student strength, software, decision makers).

**Abuse and cost controls:** per-tenant monthly call limit (default 1000) and optional budget, enforced when creating/retrying a search and again before every provider call; page delay (`Discovery:PageDelayMs`); `Discovery.Run` is a separate permission; the result of a limit hit is `429 usage_limit_reached` / a failed, resumable job.

**Hangfire dashboard:** `/hangfire`, authenticated users only, further restricted to `Tenants.Manage` or `Platform.SwitchTenant`; it can enqueue/delete jobs, so it stays platform-only. Hangfire job arguments contain ids only.

**Known residual risks:** the Google caching question (INTEGRATIONS.md); `Hangfire.MySqlStorage` is unmaintained; website enrichment depends on the host's DNS answering truthfully for the *validation* only (the connect itself is pinned); a very large site could still return 1 MB per request (bounded by `MaxWebsitesPerJob`).
