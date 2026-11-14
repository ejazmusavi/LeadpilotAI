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


## Phase 5 — WhatsApp / WAHA security
**Ban and policy risk (read first).** WAHA drives a normal WhatsApp account through WhatsApp Web. That is not an official Business API integration; WhatsApp can restrict or ban the number, and a ban removes the customer's chat history and relationship. The UI shows this warning on the accounts pages and the docs list it in INTEGRATIONS.md. Code-level mitigations: consent-gated marketing (`IContactEligibilityService`), service replies only inside a 24 h customer window, suppression/opt-out blocks everything, warm-up (20 first day, +20/day), conservative caps (30/h, 150/day, 30 s gap; hard ceilings 120/h, 1000/day, 10 s), new-contact blocks on 463/475, auto-pause on 3 consecutive failures / session FAILED or STOPPED / unconfirmed delivery, humans resume. The owner should use a dedicated number, never one that holds the business's only customer history, and move to the official API for volume.

**Webhook authentication.** Two independent secrets: the 192-bit key in the URL (identifies the account) and HMAC-SHA512 over the raw body with the account's own secret (`X-Webhook-Hmac`, constant-time compare). The signed session name must match the account. Body ≤ 256 KB, per (key, IP) rate limit, unknown key = ignored with nothing stored, bad signature = 401 and nothing stored. **Replay:** WAHA does not sign the timestamp header, so a captured valid request can be replayed; replays are harmless because storage is idempotent (unique WAHA event id / message id) and ack handling is monotonic. Keep the endpoint on HTTPS.

**SSRF.** The WAHA base URL is operator-supplied, so it is validated (http/https, no credentials/query), resolved and checked inside the connect callback. Link-local and cloud-metadata addresses are always refused; private addresses are refused unless `Messaging:Waha:AllowPrivateHosts=true` (needed for WAHA on the same docker network; default false in compose).

**Secrets.** Base URL, API key and webhook secret are encrypted with Data Protection, bound to tenant+account; masked everywhere (hint only), absent from DTOs, audit and logs. The Worker must share the key ring with Web.

**Inbound content is untrusted.** Messages are stored and HTML-encoded on output; attachments are metadata only (size/MIME checked, no content downloaded in this phase); no inbound text is ever executed or sent to a model (the AI event is a stub).

**Dead-letter queues** are shared by all tenants, so they are visible to platform operators only and show headers, never message bodies. **Opt-out:** keywords STOP/UNSUBSCRIBE/etc. in English, Roman Urdu and Urdu (whole message or short phrase; ordinary sentences containing the word are not opt-outs) create a suppression entry, revoke consent and are audited.

**Residual risks:** WAHA is a third-party gateway with its own security posture (run it on a private network, set its API key); session loss needs a human QR scan; `sendText` has no idempotency key, so "unknown outcome" messages are reconciled rather than resent (a message that was truly lost is marked failed, not resent); live behaviour was not verified against a real WAHA.

## Phase 6 — campaign security
- **Consent/suppression**: checked when the audience is selected, before each recipient is queued and immediately before the gateway call; a high lead score never enters the decision (`IContactEligibilityService`). Campaign messages are always Marketing, even inside the 24 h service window. Opt-outs received mid-run stop the send; an opt-out reply is never counted as a conversion.
- **Marketing text safety**: placeholders come from a fixed dictionary (no code evaluated); values are stripped of braces, WhatsApp formatting marks, control/zero-width/bidi characters and cut to 100 characters; a message with an unresolved placeholder never leaves; the opt-out text is mandatory and must contain a word the opt-out detector understands.
- **Controls**: approval policy with optional four-eyes (creator/submitter cannot approve), per-tenant kill switch (only `Campaigns.Settings`; audited), failure-rate and monthly-limit auto-pause, test sends only to an explicit allow-list of the team's own numbers (labelled, never part of statistics), campaign settings audited.
- **Ban-risk**: the account's caps, minimum gap and warm-up always apply; the random pause between batches is documented as a ban-risk reduction, not evasion; the wizard and settings pages show the warning; there is no unrestricted bulk mode.
- **Permissions** (`Campaigns.View/Create/Update/Execute/Approve/Cancel/Delete/Settings`, `Audiences.Preview`) enforced server side in MVC and API; mutating MVC actions are POST-only with antiforgery; output is HTML-encoded (tested with a script in the campaign name).
- Execution log and usage ledger are append-only (DB guard).


## Phase 7: AI safety and secrets
- **Provider keys** are stored encrypted per workspace (`enc:v1:`), shown only as the last four characters, decrypted only in `AiCredentials`, never audited or logged (tests scan every log line of a full indexing + answering + failure run for the keys and the customer's words). Removing a key switches the AI off.
- **Prompt injection:** all customer text and all knowledge are untrusted data in delimited blocks with markup neutralised; injection signals are flagged and shown to staff; passages that contain instructions aimed at the AI are left out of the context (and staff are told); the stable instructions never contain customer/tenant data; the output is validated deterministically (citations, amounts/percentages/offers only from cited passages, no "demo/payment/account done" claims, no leaked code/keys/instructions, no unsupported links or contact details). **Limits:** validation cannot prove that every sentence is true, only that numbers, offers, actions and links are backed by the cited text; a poisoned document from the tenant itself that states a false price is "grounded" (staff own their knowledge base).
- **Escalation:** refunds/payment disputes, legal/privacy, complaints, requests for a person, credentials and safety terms never reach the model; low confidence, validation failure, missing knowledge and provider outages become visible escalations; the AI stops answering an escalated conversation (AI mode → Human control).
- **No unauthorized actions:** the AI can request only `escalate_to_human` and `add_lead_note`; everything else is denied and logged; the AI module cannot reach the message pipeline.
- **Uploads:** size, type-by-content, PDF page limit, DOCX zip-bomb and XXE protection, HTML scripts/forms stripped (never rendered), text cleaned of control/bidi characters. Websites: SSRF-safe fetcher, robots.txt, noindex, same host only.
- **Data:** customer message text is not stored in the retrieval log (hash + length); suggestions store the model's draft and citations for staff; usage is metered and limited per workspace.

## Phase 8: controlled tools, approvals, handoff, automatic replies
- **One gateway for everything the AI does** (`AgentToolRunner`): the tool must exist in `AgentToolCatalog` (13 tools, no generic query/SQL/"call function" tool) and be enabled for the agent (the two safety tools are always on); the arguments are schema-validated (types, lengths, ranges, enumerations; unknown arguments rejected; **arguments that try to choose a tenant, user, lead, conversation or run a query are refused with `tenant_argument_forbidden`**); the tenant comes from the tenant context, the lead from the conversation (the model passes no ids); sensitive calls become approvals; one append-only execution row (inputs, outputs, outcome, approval link, duration) per call; a redelivered event replays the stored result.
- **Approvals:** `CreatePaymentRequest` always, discounts above the plan limit, custom terms and trials over the limit wait for a person with `Approvals.Decide`. The stored validated arguments are what runs; the price always comes from the catalog; the decision is an atomic claim (two simultaneous approvals: exactly one runs); expired/decided requests cannot be approved; if the Phase 10 service is not connected the approval records "not carried out". **No tool can confirm a payment or activate a subscription**, and the validator refuses any reply that claims a payment, trial, account or booking unless a tool of that turn really did it.
- **Facts:** only confirmed catalog entries (and cited knowledge) reach the model; prices in replies must appear in a cited tool result or passage; tool results are untrusted data (instruction-like content is quarantined). Booking a demo is refused for any time that is not an available slot.
- **Human control:** takeover, pause, resume, assignment, notes and status are `Conversations.Reply` actions with a handoff history. The AI never answers while the mode is Human control or Paused: checked at the start, again immediately before sending (fresh read of mode + AI epoch + opt-out), and a third time by the send runner right before the atomic claim; takeover/pause withdraw AI replies still queued. A message already `Sending` finishes (documented limit).
- **Automatic replies:** off by default, two switches (workspace + agent), audited; replies go only through `IAiReplySender` (the AI module cannot reference the send pipeline: architecture test), only as an answer to the customer's newest message inside the service window, with the per-interaction idempotency key, the normal suppression/opt-out/rate checks and a per-conversation daily cap. The AI cannot start a conversation or send marketing.
- **Opt-out:** the keyword rule, or the classifier at ≥ 0.8 confidence, triggers `StopMarketingMessages` (same code as the keyword path: suppression, consent revoked, chat blocked, AI paused); the customer is not answered.
- **Tenancy:** all 10 new entities are tenant-owned and covered by the save guard and query filters; `ConversationNote`, `HandoffEvent` and `AiToolExecution` are append-only.
- **Limits (not hidden):** the validator proves numbers, offers, actions and links are backed by cited text, not that every sentence is true; the scripted-model tests prove what the platform refuses, not how the live model behaves; WAHA remains unofficial (more automatic replies = more volume = more ban risk).

## Phase 9: automation
- **No code in workflows:** conditions and actions come from closed catalogs; unknown fields, operators, actions or properties are rejected when saving; size, depth and text length are bounded; regular expressions, scripts and template evaluation do not exist (unit-tested with hostile input, architecture-tested for banned APIs).
- **Compliance:** a workflow can only message through `WorkflowMessageSender`: always marketing (consent mandatory), eligibility (consent, suppression, opt-out, archived/excluded lead, valid number) re-checked when the step runs and again by the Phase 5 queue and send runner, so consent revoked mid-run or after queueing still stops the message (tested against the fake WAHA's received requests). A compliance block ends the run and is never retried.
- **No duplicates:** persisted step state + idempotency keys; retry, redelivery, concurrent triggers and crash recovery are tested.
- **Control:** per-workflow and workspace kill switches, usage limits (runs per month, concurrent runs, messages per day), cancellation; all audited. Versions are immutable once active (domain rule + save guard).
- **Data:** lead values placed in messages, tasks and notices are sanitised (braces, formatting marks, control and bidi characters removed). Workflow notices are visible only to their recipient.

## Phase 10: conversion, payments, reminders
- **Payment verification** is the money boundary: a subscription activates only inside `PaymentVerificationCore` (manual evidence by `Payments.Verify`, or a signature-verified, tenant-matched, amount-exact provider event). The AI tool catalog has no verify/activate tool; workflows have no payment step; an architecture test pins the callers of `ActivateWithVerifiedPayment` and the writers of `Payment`; a database check constraint refuses an Active subscription without a payment; tests cover the AI tool path (approved request created, still pending), made-up tool names, smuggled arguments, a customer chat claiming payment, forged/tampered/duplicate/wrong-amount webhook events and racing verifications.
- **Webhook** — random id → tenant, HMAC verified before parsing, size capped (256 KB), idempotent by (provider, event id), 401/404 reveal nothing, secret encrypted and never returned. No adapter ships.
- **Reminders** — service-message policy in OWNER_DECISIONS 34; suppressed / opted-out / no-consent contacts get nothing (tested), once per (demo, generation, offset) (idempotency key + atomic claim), sanitised closed-placeholder text.
- **Booking integrity** — only calculator slots; row-lock + recalculation + unique index; race tests (one rep, two reps).
- **Separation of duties** — request, convert and verify are different permissions; verification records the verifier; the agreement records its approver and is immutable.
- **Not covered** — no refunds, no partial payments, no card data of any kind is handled.

## Phase 11: analytics and reporting
- **Authorization** — dashboard `Dashboard.View`, reports `Reports.View`, export `Reports.Export`, workspace-wide data `Reports.Team`; 401 without a session, 403 without the permission, 404 for an unknown report (also on export). Every action carries a permission policy (architecture-tested). Role scope is server-side, never a hidden link.
- **Export** — gated, row-capped (50 000, 413 before audit), audited *before* any data leaves (who, report, rows, range, zone, scope, time, IP via the audit entry), UTF-8 BOM CSV with the shared formula-injection guard (`=`, `+`, `-`, `@`, tab, CR neutralised; tested with hostile city names), streamed in chunks.
- **No fabricated numbers** — empty workspaces show zeros / "—" / empty states; a rate without a denominator is "no rate", never 0 %; unverified payments never count as revenue; amounts are never summed across currencies.
- **Cache** — tenant-scoped keys, short TTL, per-tenant invalidation; a failed key build (no tenant) throws instead of sharing.
- **Data exposure** — reports return aggregates; the only free-text values are discovery city names, campaign names, user display names and agent names of the same workspace, rendered HTML-encoded (Razor) and defused in CSV.
- **Denial of service** — range ≤ 800 days, page size ≤ 200, export cap, statements are grouped SQL (no unbounded materialisation; measured in docs/benchmarks/phase11-large-dataset.md).
- **Not covered** — no per-user export quota, no scheduled e-mail reports, no row-level data-subject filters in aggregates.
