# Implementation Roadmap

Rule: implement one phase at a time; no phase starts until the previous meets its acceptance criteria and a Phase Completion Report is issued (README §12).

## Phases and dependencies
| Phase | Scope | Depends on | External prerequisite |
|---|---|---|---|
| 0 | Requirements, architecture, docs | — | Owner review/approval |
| 1 | Solution, Identity auth, EF/MySQL, logging, Swagger, health, Docker, MVC shell | 0 | .NET 10 SDK + Docker available — **code done; Docker run unverified** |
| 2 | Tenancy, users, roles, permissions, audit, isolation tests | 1 | — **built and verified on MySQL 8; see PROGRESS.md** |
| 3 | CRM: leads, contacts, pipeline, import/export, scoring, consent, suppression | 2 | — **built and verified on MySQL 8; see PROGRESS.md** (import/rescoring still synchronous until Phase 6) |
| 4 | Discovery & enrichment (Google Places, websites, imports) | 3 | Google Places key — **built; verified with a fake connector and a local fake Google server; live Google NOT verified; see PROGRESS.md** |
| 5 | WhatsApp gateway adapter, conversations, webhooks, outbox/RabbitMQ | 3 | WAHA docs + deployment details |
| 6 | Campaigns, eligibility at send time, Hangfire scheduling (Hangfire itself arrived in Phase 4) | 3, 5 | — |
| 7 | AI engine, knowledge base, RAG | 2, 5 | Claude API key, embedding provider, product info (any product) |
| 8 | AI sales agent, controlled tools, human handoff | 7, 3, 5 | Product pricing/features, demo process |
| 9 | Workflow engine, follow-ups | 6, 8 | — |
| 10 | Demo, trial, conversion, payment verification | 8, 9 | Payment method info |
| 11 | Analytics & reports | 3–10 (grows incrementally) | — |
| 12 | SaaS commercialization (plans, usage metering, billing) | 2, 11 | — |
| 13 | Multi-industry configuration | 8, 12 | — |
| 14 | Flutter mobile (optional) | API stable | — |
| 15 | Production hardening, deployment, load tests | all | Hosting target |

## MVP path (README §13)
0 → 1 → 2 → 3 → 4 → 5 → 6 (consent-gated minimal) → 7 → 8 → 9 (follow-ups) → 10 (demo booking only) → 11 (basic) → 15 (hardening subset). Phase 12–14 follow after the internal School ERP workflow is reliable.

## Parallelism
Phases 4 and 5 can proceed in parallel once Phase 3 is done (independent adapters). Basic analytics tiles are added to each phase as events exist, but the analytics phase validates them against records.

## Per-phase definition of done
Build green · unit + integration tests green (incl. tenant-isolation tests for new entities) · migrations apply on clean MySQL · docs updated (PROGRESS, ARCHITECTURE, DATABASE_DESIGN, ROADMAP) · acceptance criteria each marked passed/failed/not verified · completion report delivered.

## Risks
| Risk | Mitigation |
|---|---|
| .NET 10 package compatibility (Pomelo, Hangfire MySQL) | Verify in Phase 1 before committing versions; fallback to latest stable-compatible with documented note |
| Gateway docs delayed | Phase 5 blocked, not faked; Phases 3/4/7 continue |
| WhatsApp account bans | Rate caps, consent gate, monitoring, auto-pause (see INTEGRATIONS.md) |
| AI hallucination of price/features | Grounding + tool-only facts + validation + escalation + 100-scenario test suite |
| Duplicate sends on retry | Idempotency keys, unique recipient/message constraints, outbox |
| Scope creep | README Rule 5/10; changes to stack require approval |
