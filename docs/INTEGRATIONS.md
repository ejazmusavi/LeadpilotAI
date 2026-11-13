# External Integrations

Every integration sits behind an Application-layer port with an Infrastructure adapter; credentials are encrypted per tenant; calls are tenant-scoped, rate-limited, retried with backoff, and logged without secrets/PII.

| Integration | Port | Phase | Status / contract |
|---|---|---|---|
| Existing WhatsApp gateway | `IWhatsAppGateway` | 5 | **Blocked on documentation** from owner |
| LLM provider | `IAIProvider` | 7 | Provider TBD |
| Embeddings | `IEmbeddingProvider` | 7 | Provider/model TBD |
| Qdrant | `IVectorStore` | 7 | Default; swappable |
| Google Places API | `IDiscoveryConnector` | 4 | Needs API key; obey ToS (field masks, caching/retention limits, attribution) |
| Public school websites | `IDiscoveryConnector` | 4 | robots.txt respected, SSRF guards, no protected platforms |
| Authorized directories / lead-gen / Meta Lead Ads | `IDiscoveryConnector` | 4+ | Only with authorization/API access |
| CSV/Excel import, manual entry | `IDiscoveryConnector` | 3–4 | In-app |
| Payment verification | `IPaymentVerifier` | 10 | Provider TBD |
| Email (future) | `IEmailSender` | later | — |
| Calendar/meeting links | `IMeetingLinkProvider` | 10 | Optional |

## WhatsApp gateway (Phase 5)
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
