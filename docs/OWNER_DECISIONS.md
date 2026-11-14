# Owner decisions

Decisions taken while the owner was unavailable are marked **(default taken)**: the recommended option was chosen, work continued, and this file says how to change it. Earlier decisions given by the owner are listed first for reference.

## Given by the owner (earlier phases)
- WhatsApp through the existing WAHA gateway (unofficial, ban risk accepted, conservative defaults).
- The platform is product-agnostic; schools are only the first vertical.
- AI = the Claude API behind `IAIProvider`.

## Phase 7 (AI engine and knowledge base) — all **(default taken)**

### 1. Embedding provider: Voyage AI over HTTP
- **Options:** Voyage AI (Anthropic's recommended embeddings partner), OpenAI embeddings, a self-hosted model, none.
- **Why:** best fit with a Claude-based stack, simple HTTP API, no new infrastructure, no second AI vendor relationship with a different data-handling policy. The port (`IEmbeddingProvider`) keeps the choice open; a deterministic fake exists for tests only.
- **Change:** implement `IRawEmbeddingProvider` for another vendor in `Infrastructure/Ai/Providers` and register it in `AddAi`; re-index all documents (the AI settings page shows how many need it).

### 2. Vector store: MySQL-backed by default, Qdrant adapter available
- **Options:** Qdrant only, MySQL-backed only, both.
- **Why:** Docker/Qdrant could not be run in the sandbox (a real Qdrant binary was downloaded but not executed: the sandbox policy refused to run it), and a mandatory extra service is a bigger operational step than the MVP needs. The MySQL store (brute-force cosine over the tenant's own vectors, refuses beyond 20,000 passages per workspace) is fully tested; the Qdrant adapter is tested against a local Qdrant-compatible fake only.
- **Change:** set `Ai:VectorStore:Provider` to `qdrant` and `Ai:VectorStore:Qdrant:BaseUrl` (+ `ApiKey` from the secret store), then re-index (or call re-index all). Move before a workspace exceeds ~5,000 passages.

### 3. Raw HTTP instead of the Anthropic C# SDK
- **Why:** the adapter needs per-tenant keys, a configurable base URL (so it is tested against a local server), its own retry/timeout/circuit-breaker and no new dependency. Request shape follows the Messages API reference (system blocks with `cache_control`, no sampling parameters, `usage` incl. cache tokens, `stop_reason: refusal` handled).
- **Change:** a different adapter behind `IRawAIProvider`.

### 4. Server-side fallback on refusals is OFF
- `Ai:Claude:ServerSideFallback` (default false). The beta flag was not verifiable without a live key; a refusal is handled by escalating to a person.
- **Change:** set it to `true` after a live check.

### 5. Default models
Answer `claude-sonnet-5-5`, intent and summaries `claude-haiku-5-5` (cheap, frequent), embeddings `voyage-3.5`. Every model id is editable per workspace (AI settings). "Per agent" models arrive with agents in Phase 8; today the per-purpose models are the granularity.

### 6. Automatic sending is not implemented
- The README keeps AI sending off until Phase 8. Instead of a toggle that does nothing, the settings page shows a disabled switch "not available yet", the AI module cannot reach the send pipeline at all (architecture test), and staff turn a suggestion into a reply themselves.
- **Change:** Phase 8 adds the sales-agent controls and an explicit tenant opt-in.

### 7. Thresholds
Minimum retrieval score 0.35, minimum confidence 0.6, 5 passages, 12 recent messages, monthly token limit 2,000,000 per workspace (0 = unlimited; empty = this platform default). All editable; defaults are conservative so that the AI says "I don't know" and hands over rather than guessing.

### 8. Sensitive requests never reach the model
Refund/payment disputes, legal/privacy requests, complaints, requests for a person, credentials and safety keywords are escalated by deterministic rules before any model call (no tokens spent, nothing for the model to get wrong). Tenants add their own keywords.

### 9. Cost precision
`AiUsageEntries.EstimatedCostUsd` is `decimal(18,8)` instead of the usual `decimal(18,2)`: one call costs fractions of a cent and two decimals would round every entry to zero. Costs are estimates from a configurable price table (`Ai:Prices`).

### 10. Tenant AI is off until a person enables it
Both provider keys must be saved before AI can be turned on; removing a key turns it off. Conversations additionally need AI mode "Active" (Phase 5 default is Paused).
