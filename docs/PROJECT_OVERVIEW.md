# LeadPilot AI — Project Overview

**Tagline:** Discover. Engage. Convert.

## Vision
A multi-tenant AI marketing, lead-generation, engagement and sales-automation SaaS. First use: internal acquisition of customers for an existing cloud School Management Software (private schools in Pakistan). Later: a general-purpose platform other businesses subscribe to. Schools are the first **industry vertical**, never a hardcoded limitation.

## Owner decisions (recorded at Phase 0 approval)
1. **WhatsApp gateway = WAHA** (WhatsApp HTTP API). Phase 5 will read the WAHA documentation and build the adapter from it; no endpoints are assumed here.
2. **Product-agnostic from day one.** The School ERP is only the first *product/vertical*. Product knowledge, pricing, qualification questions, agent instructions and workflows are tenant configuration, never code. Phase 13's configurability is pulled forward into the *design* of Phases 3, 7 and 8 (e.g. `Product`/`CatalogItem`, configurable pipeline, agent instructions per tenant).
3. **AI provider = Claude API** (behind `IAIProvider`). Embeddings provider still to be chosen (Anthropic has no embeddings endpoint; a separate embedding provider or a local model is needed for RAG).
4. Product details (features, pricing, demo/trial process) will be supplied later and entered as tenant data; Phase 7/8 are blocked on them for real content, not for code.
5. **Phase 0 approved.**

## Scope decisions (Phase 0)
| Topic | Decision |
|---|---|
| MVP | Items 1–11 of README §13: tenancy, auth/permissions, school CRM, approved discovery, existing WhatsApp gateway, consent/suppression, AI knowledge base, AI sales agent, demo booking, follow-up automation, basic analytics |
| Deferred | Subscription billing (Ph12), industry configurability beyond data-driven design (Ph13), Flutter (Ph14), enterprise DB-per-tenant |
| Compliance | Discovery is never permission to contact. Consent + suppression are enforced at send time, not only at audience selection |
| WhatsApp | Existing WAHA gateway is preserved; adapter is built from the WAHA docs in Phase 5 |
| AI | Provider-independent; grounded answers only; no invented prices/features/payments |

## Modules (23) and target phase
| # | Module | Phase |
|---|---|---|
| 1 | Dashboard | 1 (shell), 11 (metrics) |
| 2–4 | Tenant, User, Roles & Permissions | 2 |
| 5–6 | Lead Discovery, Enrichment | 4 |
| 7–8 | Leads/Contacts, CRM & Pipeline | 3 |
| 9 | Campaigns | 6 |
| 10–11 | WhatsApp, Conversations Inbox | 5 |
| 12–13 | AI Sales Agents, Knowledge Base | 7–8 |
| 14–15 | Workflows, Follow-ups | 9 |
| 16–18 | Demo, Trial, Customers | 10 |
| 19 | Subscription & Billing | 12 |
| 20 | Analytics | 11 |
| 21 | Integrations | 4, 5 (grows) |
| 22 | Audit Logs | 2 |
| 23 | Platform Settings | 2 |

## Personas / roles
Platform Owner, SaaS Administrator, Tenant Administrator, Tenant Staff, Sales Manager, Sales Agent, Marketing Manager, Read-only.

## Ambiguities and conflicts found in the spec (with proposed resolution)
1. **Hangfire vs RabbitMQ overlap** — Resolved: Hangfire = *time-based* (schedules, recurring, delays, cleanup); RabbitMQ = *event-driven* durable fan-out (incoming messages, AI, enrichment, campaign delivery). A Hangfire job may publish an event but never duplicates a queue's work. One scheduler only.
2. **"Platform Owner/SaaS Admin" vs tenant-scoped data** — Platform roles are cross-tenant but every cross-tenant access goes through an explicit, audited "tenant switch"; no implicit global queries.
3. **Global query filters vs "don't rely on them"** — Filters are defence in depth; every command/query also takes tenant from `ITenantContext` and authorization checks verify ownership.
4. **.NET 10 / C# 14** — Spec'd explicitly; the SDK is not installed in the current sandbox. Phase 1 must install/verify it before the "solution builds" criterion can be claimed.
5. **Unofficial WhatsApp gateways** (QR/WhatsApp Web style) carry account-ban and policy risk; documented in INTEGRATIONS.md. Bulk marketing is rate-limited and consent-gated regardless of technical capability.
6. **Qdrant vs alternatives** — Default: Qdrant behind `IVectorStore`; swap without touching business logic.
7. **Payments** — No provider named. Phase 10 builds a `IPaymentVerifier` port; activation requires a verified payment event, never an AI/manual claim.
8. **Auth for API vs MVC** — Cookie auth for MVC, JWT bearer for API/mobile, same Identity store.
9. **Outbox** — Required for DB-change + RabbitMQ-publish consistency (transactional outbox in MySQL).

## Information needed from the owner (essential only)
1. **WAHA specifics for your deployment**: base URL, API key, which WAHA engine (WEBJS / NOWEB / GOWS) and edition (Core / Plus), webhook secret (HMAC), session names. General API shape will be taken from the WAHA docs.
2. **Product info (any product, supplied later)**: feature list, pricing plans, demo & trial process, objection answers, support hours, brand voice (English/Urdu/Roman Urdu).
3. **Google Places API key** availability (or other authorized discovery sources).
4. **Claude API key** (decided) and an **embedding provider/model** for RAG.
5. **Payment methods** used in Pakistan for subscriptions (bank transfer, JazzCash/Easypaisa, card gateway) so Phase 10 can design the verifier.
6. **Hosting target** (VPS size/provider) for Phase 15 sizing.
7. Confirmation of approach to **marketing consent** for Pakistani schools (opt-in source expectations).
