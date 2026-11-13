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
