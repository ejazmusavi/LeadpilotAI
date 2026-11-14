# External Integrations

Every integration sits behind an Application-layer port with an Infrastructure adapter; credentials are encrypted per tenant; calls are tenant-scoped, rate-limited, retried with backoff, and logged without secrets/PII.

| Integration | Port | Phase | Status / contract |
|---|---|---|---|
| WAHA WhatsApp gateway | `IWhatsAppGateway` | 5 | Owner decision: WAHA. Adapter built from WAHA docs in Phase 5 |
| LLM provider | `IAIProvider` | 7 | **Claude API** (owner decision) |
| Embeddings | `IEmbeddingProvider` | 7 | Provider/model TBD |
| Qdrant | `IVectorStore` | 7 | Default; swappable |
| Google Places API (New) | `IDiscoveryConnector` | 4 | **Built; verified only against a local fake server** (no key/network here). Needs the tenant's own API key |
| Public websites | `IDiscoveryConnector` + `IWebsiteEnricher` | 4 | **Built.** robots.txt respected, SSRF guards, public pages only |
| Authorized directories / lead-gen / Meta Lead Ads | `IDiscoveryConnector` | 4+ | **Documented extension points only**: they answer "not configured" and never return data |
| CSV/Excel import, manual entry | listed as in-app connectors | 3–4 | Existing Phase 3 paths (`/Imports/New`, `/Leads/Create`), same duplicate detection; not run as discovery jobs |
| Payment verification | `IPaymentVerifier` | 10 | Provider TBD |
| Email (future) | `IEmailSender` | later | — |
| Calendar/meeting links | `IMeetingLinkProvider` | 10 | Optional |

## WhatsApp gateway — WAHA (Phase 5)
WAHA is a self-hosted HTTP wrapper around WhatsApp **Web** engines (not the official Business Platform). So the account-risk section below applies in full: conservative rate limits, consent gate, auto-pause on failure signals. Phase 5 starts by reading the WAHA docs for sessions, send endpoints, message/ack events, webhook HMAC signing and engine differences, and recording the findings here before coding.

### Original information checklist
**Do not assume endpoints or payloads.** Information required before coding:
1. Base URL(s), environments, authentication (API key/bearer/HMAC) and rotation.
2. Endpoints: send text, send template/media, message status, mark-read, list accounts/sessions, account status, connect (QR/pairing), disconnect.
3. Webhook: events list, payload samples, signature/secret scheme, retry behaviour of the gateway, event ids for idempotency.
4. Multi-account/session model and how an account maps to a tenant.
5. Rate limits, message size/media limits, error codes.
6. Whether it is **official Business Platform** or **WhatsApp Web/QR automation**.

Adapter responsibilities: map gateway payloads to normalized `InboundMessage`/`StatusEvent`; surface gateway errors as typed results (transient vs permanent); supply idempotency (client message id or our own dedup record) so a retried worker never double-sends; never leak gateway specifics outside Infrastructure.

### Operational/policy risk (must be shown to the owner)
If the gateway is WhatsApp Web/QR-based, it is not an officially supported channel for bulk marketing; numbers can be restricted or banned, sessions can disconnect, and delivery/read semantics may be incomplete. Mitigations built in: opt-in/consent gate, template and window rules where official, conservative per-account rate limits and daily caps, warm-up pacing, opt-out handling, human escalation, monitoring of failure/ban signals with automatic campaign pause. Technical capability ≠ permission to send unrestricted bulk marketing. For official Business Platform: opt-in required, approved templates for business-initiated messages, 24h customer service window enforced in `IContactEligibilityService`.

## Discovery source rules
Provider-independent `IDiscoveryConnector` returns normalized candidates with `Source`, `ExternalId/Url`, `RetrievedAtUtc`, `Confidence`. Unverified fields are flagged. Nothing is invented (student strength, existing software, decision-makers). Provider terms drive cache `ExpiresAtUtc` and attribution fields. Public business phone/email are stored as *publicly discovered*, never as consent.

## AI provider
Provider-neutral request/response (messages, tools, structured output, usage). Retries/timeouts/circuit breaker; token usage recorded to `UsageRecord`; model and prompt version stored with each generation.

## Eventing contracts
RabbitMQ topology: topic exchange per domain (`leadpilot.messaging`, `.ai`, `.discovery`, `.campaigns`), durable queues, per-queue DLX/DLQ, envelope `{ MessageId, TenantId, CorrelationId, CausationId, Type, OccurredAtUtc, Payload }`, schema versioned.


## Phase 4 — discovery connectors (as built)
### Google Places API (New) — Text Search
- Endpoint `POST {BaseUrl}/v1/places:searchText` (`Discovery:GooglePlaces:BaseUrl`, default `https://places.googleapis.com`). Body: `textQuery` (e.g. "private school in Gulberg, Lahore, Punjab, Pakistan"), `pageSize` (20), `regionCode` (from the country when known), `includedType` (the category mapping's place type, optional), `pageToken` for paging.
- Headers: `X-Goog-Api-Key` (the **tenant's** key, decrypted per call, never in the URL, never logged, scrubbed from provider error text) and `X-Goog-FieldMask: places.id,places.displayName,places.formattedAddress,places.addressComponents,places.types,places.websiteUri,places.internationalPhoneNumber,places.googleMapsUri,places.businessStatus,nextPageToken`. Nothing else is requested (no ratings, reviews, photos, opening hours, coordinates).
- Errors: 429 → `provider_quota_exceeded` (retryable); 401/403 → `connector_auth_failed` (not retryable: key invalid, API not enabled or billing off); 5xx/network/timeout/garbled body → `provider_unavailable` (retryable); other 4xx → `provider_rejected_request`. Failed calls are written to the usage ledger with their HTTP status and cost 0.
- Attribution: every result carries `SourceName = Google Places`, `SourceUrl = googleMapsUri` and `Attribution = "Place data from Google Maps"`, shown in the review table and detail page and copied to the lead's source.
- **Verification status:** exercised against a local fake HTTP server returning responses of the documented shape (paging, field mask header, mapping, 429/403/400/5xx, key scrubbing). **Not verified against the live Google service** in this repository's environment (no API key, no outbound access to Google).

### What Google's terms say about keeping data (checked 2026-11-13; re-check before launch)
Sources consulted: Google Maps Platform Service Specific Terms (archived 2023–2025 versions) and secondary summaries; the current Places API policies page itself could **not** be retrieved here.
- `place_id` may be cached/stored without a time limit. Latitude/longitude may be cached for up to 30 consecutive days. Other Places content is, per the policies as summarised, not cacheable beyond what is needed to serve the user's request.
- **Operating choice (not legal advice):** `place_id` is kept indefinitely (it is the unique key that makes retries and repeated searches idempotent). All other provider content is stored with `ExpiresAtUtc = RetrievedAtUtc + Discovery:GoogleContentRetentionDays` (default 30) and a daily Hangfire job (`discovery-retention`, 03:00 UTC, tenant by tenant) purges names, addresses, phones, websites, notes and provenance of expired results while keeping id, status and lead link. Coordinates are not requested at all. A purged result cannot be approved; re-run the search.
- **Open compliance question for the owner:** approving a result copies name/address/phone/website into the tenant's own CRM lead, and that copy is not purged. Whether this is permitted for a tenant's own lead records under Google's terms must be confirmed (options: require the reviewer to verify the data first and treat the lead as the tenant's own record; or store only `place_id` + a link and have the user retype). Until confirmed, treat Google-sourced leads as needing verification; the lead source keeps the attribution and retrieval time.
- Pricing/billing: requesting `websiteUri`/`internationalPhoneNumber` puts Text Search in Google's *Enterprise* SKU (billed at the highest SKU among requested fields). The per-call figure used for the usage page (`Discovery:DefaultCostPerCallUsd`, 0.035, editable per tenant) is an **estimate from a third-party summary, not Google billing data**; the owner must set the figure from Google's current price list. Google bills the tenant's own Cloud account; this system never pays.

### Public website enrichment and the website connector
Fetches only public pages, through the SSRF-safe client (see SECURITY.md). Order: validate URL → robots.txt of the origin (cached per job; 404 = no rules; 401/403/5xx/unreachable = **do not crawl**; honours the `LeadPilotAI-Bot` group, longest-match, `*`/`$`) → home page → at most one same-host contact page. Not read: login pages (password field), `noindex` pages, non-HTML, >1 MB, redirects beyond 3, anything robots disallows. User-Agent: `LeadPilotAI-Bot/1.0 (+https://leadpilot.ai/bot; …)` (configurable). Extracted: page title, schema.org JSON-LD name/address/telephone/email (School, EducationalOrganization, LocalBusiness…), `tel:`/`mailto:` links, e-mail addresses written in the visible text, links to the business's own social profiles (share buttons excluded). **Never extracted or inferred:** student strength, existing software, decision-maker names, owners, anything not literally present. Every value from a website is stored as *Unverified*; a phone that matches the provider's phone is *Corroborated*. Published e-mail addresses are public business details, **not consent**.

### Confidence formula (unit-tested, `ConfidenceCalculator`)
Source base (structured provider 35, authorized directory 30, user supplied 30, website self-published 25) + address 10 + website 10 + phone 15 + e-mail 5 + category matched 10 + website confirmed (name shares a distinctive word with the provider's name) 10 + phone corroborated by a second source 5; −40 when the provider reports the business temporarily closed; 0 when there is no name; clamped 0–100. Levels: ≥75 High, ≥50 Medium, otherwise Low. Permanently closed businesses are skipped (and counted in the job's notes). Confidence ranks data reliability only; it is never an input to contact eligibility.

### Extension points
`authorized_directory`, `leadgen_integration` and `meta_lead_ads` are descriptor-only connectors: creating a search with them answers 409 `connector_not_configured` with the reason; their `SearchAsync` throws and contains no data path (architecture-tested). To add one: implement `IDiscoveryConnector` in Infrastructure, register it, give it a descriptor with `Kind = Search`, and document its terms here first.


## Phase 5 — WAHA documentation findings (read 2026-11-13, BEFORE the adapter was written)
Source pages (all under `https://waha.devlike.pro/docs/`, fetched 2026-11-13): `overview/how-to-avoid-blocking/`, `overview/quick-start/`, `how-to/sessions/`, `how-to/send-messages/`, `how-to/receive-messages/`, `how-to/events/`, `how-to/chats/`, `how-to/contacts/`, `how-to/security/`, `how-to/engines/`, `how-to/waha-plus/`, `how-to/swagger/`, `engines/noweb/`, `engines/gows/`, `engines/webjs/`. The live OpenAPI/Swagger document is served **by the user's own WAHA instance** (`/` Swagger UI, `/-json` OpenAPI) and could not be fetched here (no instance), so response bodies that the prose docs do not show are marked *undocumented* and handled defensively.

### What WAHA is (and is not)
WAHA is a self-hosted Node.js REST wrapper over community WhatsApp **Web** engines (WEBJS/WPP = headless Chromium, NOWEB = Node websocket, GOWS = Go websocket). It is **not** the WhatsApp Business Platform: there are no official templates, no 24-hour-window enforcement by the provider, no delivery SLA. The docs' own "How to Avoid Blocking" page says a bot should **never initiate a conversation**, should only reply, should send *seen* → *typing* → *stop typing* → *text*, and that accounts are banned after a handful of spam reports. See "Operational / policy risk" below and SECURITY.md.

### Authentication
- Header `X-Api-Key: <WAHA_API_KEY>` on every API call (also accepted as `?x-api-key=` query, which we never use because URLs are logged). Media download (`/api/files/...`) needs the key too. Keys API (admin keys / per-session scoped keys) exists; we only need one key.
- Dashboard/Swagger have separate basic-auth credentials; irrelevant to the adapter.
- WAHA docs: "Do not expose the API on public networks"; use HTTPS via a reverse proxy.

### Endpoints used (all prefixed `/api`)
| Port operation | Endpoint | Notes |
|---|---|---|
| list sessions | `GET /api/sessions?all=true` | array of `{name,status,config,me:{id,pushName},engine:{engine}}`; `all=true` includes STOPPED |
| session status | `GET /api/sessions/{session}` | same object |
| create session | `POST /api/sessions` body `{name,start?,config:{webhooks:[{url,events,hmac:{key},retries,customHeaders}],metadata,ignore,...}}` | session `name` is WAHA's id. `start:false` postpones start |
| update session | docs inconsistent: prose says `PUT /api/sessions/{session}`, feature table says `POST /api/sessions/{name}/` | **ambiguous** – adapter uses PUT (prose + examples) and the fake mirrors it; verify on a live instance |
| start / stop / restart / logout / delete | `POST /api/sessions/{s}/start`, `/stop`, `/restart`, `/logout`; `DELETE /api/sessions/{s}` | |
| QR | `GET /api/{session}/auth/qr?format=raw` → `{value}`; `Accept: application/json` with default format → `{mimetype,data(base64)}`; default is a binary PNG | QR expires: first 60 s, then 20 s each, max 6, then status FAILED; refetch on every `SCAN_QR_CODE` status event |
| pairing code | `POST /api/{session}/auth/request-code` body `{phoneNumber}` → `{code}` | "not always available" – QR is the fallback |
| me | `GET /api/sessions/{s}/me` | `{id,pushName,reachoutTimelock,messageCapping}` or `null` when stopped |
| reachout timelock | `GET /api/sessions/{s}/timelock` → `{enforcementType,isActive,timeEnforcementEnds(unix s)}` | send error 463 to *new* contacts; do **not** restart/re-pair |
| message capping | `GET /api/sessions/{s}/capping` → `{cappingStatus: NONE\|FIRST_WARNING\|SECOND_WARNING\|CAPPED,totalQuota,usedQuota,cycleStart,cycleEnd,...}` | send error 475 when CAPPED; open set of values |
| send text | `POST /api/sendText` body `{session,chatId,text,reply_to?,linkPreview?}` | chatId = digits + `@c.us` (no `+`). **Response body shape is not shown in the prose docs** (Swagger only). We expect a message object with `id` like `true_<chat>_<HEX>`; the adapter accepts `id` as string or `id._serialized`/`_data.id._serialized`/`key.id`, and if no id can be read the send is classified **Unknown** (not failed, not sent) |
| seen | `POST /api/sendSeen` body `{session,chatId,messageIds?[],participant?}`. Without ids: all unread (≤30 DM). `messageIds` honoured only by NOWEB/GOWS | |
| typing | `POST /api/startTyping`, `/api/stopTyping` body `{session,chatId}` | used before replies (docs recommendation) |
| message lookup | `GET /api/{session}/chats/{chatId}/messages/{messageId}` (full `true_..`/`false_..` id on any engine; plain id on GOWS/NOWEB) and `GET /api/{session}/chats/{chatId}/messages?limit=&filter.fromMe=&filter.timestamp.gte=` → `[{id,timestamp,from,fromMe,body,hasMedia,ack,ackName,replyTo,_data}]` | NOWEB needs its store enabled to list; used for ack lookup and crash-window reconciliation |
| number check | `GET /api/contacts/check-exists?phone=&session=` → `{numberExists,chatId,pn}` | Brazil 9-digit quirk; optional |
| webhooks | per-session `config.webhooks[]` (or global env `WHATSAPP_HOOK_*`) | see below |
No endpoint accepts a **client-supplied idempotency key** or client message id. WAHA therefore cannot de-duplicate a retried `sendText` — that guarantee must come from us (see ARCHITECTURE.md Phase 5).

### Webhook delivery
- Envelope (documented): `{"id":"evt_<ulid>","timestamp":<ms>,"event":"message","session":"default","metadata":{...},"me":{"id":"7911@c.us","pushName":"~"},"payload":{...},"environment":{"tier":"CORE","version":"..."},"engine":"WEBJS"}`. Older examples omit `id`/`timestamp`, so `id` is treated as **optional**; when absent we derive a deterministic hash (see below).
- Headers: `X-Webhook-Request-Id` (unique per delivery request, changes on retry), `X-Webhook-Timestamp` (unix **ms**), and when `hmac.key` is configured `X-Webhook-Hmac` (hex digest of the **raw request body**) and `X-Webhook-Hmac-Algorithm: sha512`. Algorithm = **HMAC-SHA512**, key = the configured secret, input = raw bytes, output = lowercase hex (docs example: body `{"event":"message","session":"default","engine":"WEBJS"}`, key `my-secret-key`, signature `208f8a55…fa89`; our unit test asserts this vector).
- **Replay protection is not provided by WAHA's scheme** (the timestamp header is not part of the signed input). We add: reject timestamps outside a tolerance window (default ±5 min, configurable) *as a defence-in-depth check only*, and rely on event-id/hash uniqueness for idempotency.
- Retries: configurable per webhook (`constant|linear|exponential`, default none unless set) → **duplicates and out-of-order delivery must be expected**. We configure `exponential`, 2 s, 8 attempts when registering the webhook.
- Events relevant to us: `message` (incoming; `payload`: `id` `false_<chat>_<HEX>`, `timestamp` (unix **seconds**), `from`, `fromMe`, `to`, `body`, `hasMedia`, `media{url,mimetype,filename,error}`, `ack`, `replyTo`, `source: app|api`, `_data`), `message.any` (fires for every creation **including our own sent messages**; `source:"api"` for ones sent via the API), `message.ack` (`payload`: `id`, `from`, `participant`, `fromMe`, `ack`, `ackName`), `session.status` (`payload.status`, `payload.statuses[]`, `payload.data` e.g. `reachoutTimelock`/`messageCapping`/passkey), `message.reaction`, `message.edited`, `message.revoked`, `message.waiting`, `engine.event`, group/label/call/presence events (ignored by us).
- Ack values: `-1 ERROR`, `0 PENDING`, `1 SERVER` (sent), `2 DEVICE` (delivered), `3 READ`, `4 PLAYED`. **WEBJS only emits `message.ack` when the session config sets `webjs.tagsEventsOn=true`** (docs warn of stability impact). Group acks use `message.ack.group` semantic (id carries `_participant`); we ignore group chats.
- Identifiers: `123@c.us` user, `123@lid` hidden user id (WhatsApp is migrating to it; the real phone may only be in `check-exists.pn` or engine-specific `_data`), `@g.us` group, `@newsletter` channel, `status@broadcast`. **Design consequence:** a `@lid` sender whose phone cannot be derived is stored as a conversation without a linked Contact and is never auto-linked by guesswork.
- The `fromMe` flag: `message` is documented as "incoming"; in the sample payload `fromMe` can still be `true` (sample copied across events). We **trust `fromMe=false` only** for inbound and ignore `fromMe=true` on `message`.

### Engines and Core vs Plus (as documented 2026-11-13)
- WEBJS/WPP (Chromium; heavy), NOWEB and GOWS (websocket; light). Payloads "may differ significantly" between engines. GOWS/NOWEB support `messageIds` in seen; NOWEB needs `config.noweb.store.enabled` for message lookup; GOWS has storage flags; passkey pairing only on GOWS. Default recommendation here: **GOWS or NOWEB**, test before switching engines.
- **WAHA Plus no longer exists**: since WAHA 2026.6.1 everything formerly Plus (multiple sessions, media, storages, security) is in the free Core image `devlikeapro/waha`. Older Core builds (< 2026.6.1) allow **one session only** – the account page tells the owner to check. We do not depend on Plus features beyond multi-session.

### Limits / error behaviour
No numeric API rate limit is documented. WhatsApp-side restrictions: error **463** (reachout timelock) and **475** (message capping) on sends to new contacts, session stays WORKING. Error response bodies are *undocumented*; the adapter classifies by HTTP status first (401/403 auth, 404 unknown session/chat, 422 validation, 429 and 5xx/timeouts transient) and then by message text containing `463`/`475`/`timelock`/`capping`.

### Ambiguities handled defensively
1. `sendText` response shape (undocumented in prose) → id extraction fallbacks; missing id ⇒ send outcome *Unknown* ⇒ reconciliation, never an automatic re-send.
2. Update-session verb (PUT vs POST) → PUT; failure reported as a typed error.
3. Webhook event `id` optional in older payload samples → deterministic hash fallback.
4. `message.ack` for WEBJS needs `tagsEventsOn` → account setup sets it for WEBJS and the UI warns; delivery tracking also has a polling fallback (`GetMessageStatusAsync`) used by reconciliation.
5. `@lid` identifiers → no guess-linking.
6. `fromMe` on `message` events → only `false` accepted as inbound.

### Operational / policy risk (WAHA specific — shown to the owner in the UI and SECURITY.md)
Automating WhatsApp Web violates WhatsApp's terms for unofficial clients; **accounts can be restricted or permanently banned without notice**, sessions drop (status FAILED/STOPPED) and need a human to re-scan a QR code, and delivery/read events can be incomplete. WAHA's own guidance: reply-only, small volumes, human-like pacing. LeadPilot therefore ships conservative defaults and does not offer a "bulk send" feature in Phase 5: per-account **hourly cap 30 and daily cap 150 by default (hard ceilings 120/h and 1000/day)**, a minimum random delay of 20–45 s between sends from one account, **warm-up** (day-1 cap 20 growing +20/day to the configured cap), **only contacts that pass `IContactEligibilityService` receive marketing**, opt-outs create suppression immediately, and an account **auto-pauses** after 3 consecutive send failures, a FAILED/STOPPED status, or a 463/475 signal. Use of the official WhatsApp Business Platform is the right answer for bulk/cold marketing and remains a future adapter behind the same `IWhatsAppGateway` port.


### Phase 5 — adapter as built and verification status
`WahaGateway` (Infrastructure/Messaging/Waha) implements `IWhatsAppGateway` from the findings above: `X-Api-Key`; session create/start/stop/update; QR; `sendText`; `sendSeen`/typing; chat message lookup for reconciliation; webhook registration with HMAC, retries and `ignore` of groups/status/channels/broadcast; normalisation of `message`, `message.ack`, `session.status`. Ambiguities were handled defensively: unknown `sendText` response shapes are accepted only if a message id can be found (else outcome Unknown → reconcile); PUT is used for session update; unknown events are stored and ignored.
**Verified:** against a loopback fake WAHA with fault injection (timeouts, 5xx, 401, 463/475, missing id) — request shapes follow the documentation but the fake is our reading of it. **NOT verified:** a real WAHA (any engine), real QR pairing, real ack ordering, real `@lid` behaviour, real webhook retries. Run the manual live check in DEVELOPMENT.md before trusting production behaviour.

## Phase 6 — campaigns and the gateway
Campaigns add **no** gateway calls: every message goes through the Phase 5 queue and the single `WahaGateway.SendTextAsync` path (never retried automatically; unknown outcomes are reconciled, never re-sent). Pacing relies on the account's own limits plus a campaign batch pause (default 60–180 s random, hard minimum 15 s). **Not verified live**: how WAHA behaves under a real campaign (ack timing, reachout timelocks for new contacts, `@lid` ids) is only known from the documentation; the first live run should use a small audience on a disposable number.


## Phase 7: AI providers (Claude, Voyage AI, Qdrant)
| Provider | Used for | Status |
|---|---|---|
| Anthropic Claude Messages API (`POST /v1/messages`, `anthropic-version: 2023-06-01`, per-tenant `x-api-key`) | intent/query triage, grounded answers, conversation summaries | Adapter **tested against a local server that speaks the API's shape; NOT verified against the live service** (no key in the sandbox). Prompt caching: `cache_control: ephemeral` on the stable system block. No sampling parameters are sent (current models reject non-default values). Refusals (`stop_reason: refusal`) are final and escalate. Models: `claude-sonnet-5-5` (answers), `claude-haiku-5-5` (triage/summaries), alternatives `claude-opus-5-5`, `claude-fable-5-2`. |
| Voyage AI embeddings (`POST /v1/embeddings`, Bearer per-tenant key, `input_type` document/query) | passage and query vectors | Same: local fake only. Default `voyage-3.5`. |
| Qdrant (REST) | optional vector store | Adapter tested against a local Qdrant-compatible fake; **no real Qdrant instance exercised**. |
Provider terms: customer message text is sent to Anthropic when the AI answers; check the data-processing terms of your Anthropic account (retention, zero-data-retention eligibility of the chosen model) before enabling the AI for a workspace.

## Phase 8: ports reserved for Phase 10
`IDemoScheduler` (availability, book a slot), `ITrialProvisioner` (create a trial) and `IPaymentRequestGateway` (create a payment **request**) are defined in `LeadPilot.Application.Agents`. In Phase 8 the registered implementations answer "not connected" (`PortResult.NotConnected`): the tools report `not_available`, nothing happens and no booking, trial or payment request is claimed. Phase 10 registers the real services (the registration uses `TryAdd`, so they replace the defaults). The payment port creates requests only; activation of anything is driven by verified payment confirmation (Phase 10), never by a tool or by an AI statement. Tests use a TEST-ONLY double (`Phase10Doubles`) to prove how the tools behave once a service exists.
