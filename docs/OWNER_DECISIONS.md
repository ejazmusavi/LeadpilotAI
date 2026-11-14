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

## Phase 8 (AI sales agent and controlled tool calling) — all **(default taken)**

### 11. Tool calling is a JSON protocol on top of `IAIProvider`, not native tool-use blocks
- **Options:** native Claude `tool_use` blocks (needs a richer provider port and adapter changes), or the model returns `{"tool_calls": [...]}` and the platform executes them and feeds the results back as untrusted data.
- **Why:** keeps the provider port unchanged (one orchestrator, no second abstraction), keeps the tools provider-independent and testable with the scripted model, and the platform — not the provider SDK — owns validation, allow-lists, approvals and logging. Bounded: 3 rounds, 4 calls per round, 8 per message.
- **Change:** add tool-use support inside the Claude adapter later and translate to the same `ToolCall` records; nothing above the adapter changes.

### 12. One agent per conversation = the workspace's default active agent; no agent = Phase 7 behaviour
Conversations in AI mode are answered by the default active agent. Without an agent the workspace behaves exactly as in Phase 7 (suggestions only, tools limited to TransferToHuman and AddLeadNote). **Change:** create/activate/default agents on the Sales agent page.

### 13. Automatic AI replies: two switches, both OFF
A workspace switch (Agent settings) AND a per-agent switch must be on; an agent cannot be switched on while the workspace has not allowed it. Both are audited. Even then the AI only **answers** a customer message inside the 24 h service window, through the normal outbound pipeline (suppression, opt-out, rate limits, warm-up, idempotency key per interaction), at most 10 automatic replies per conversation per day (editable 1–100). There is no API to start a conversation or to send marketing as the AI. **Change:** the switches and the cap on the Sales agent page.

### 14. No automatic hand-over message to the customer
When the AI escalates (or a person takes over) nothing is sent automatically: "a colleague will answer" is a **suggestion** staff see in the inbox. Reason: the rule "never send while a person is in control or the AI is paused" has no exceptions, which keeps the race conditions simple to prove. **Change:** if you want an acknowledgement on escalation, it can be added as an explicit, separately limited sender mode.

### 15. A person replying takes control
A staff reply in a conversation whose AI mode is Active switches it to Human control (history entry `AutoTakeover`). This changes Phase 5/7 behaviour (previously the mode stayed Active); one Phase 7 test was updated to hand the conversation back before checking history handling. **Change:** resume the AI on the conversation page, or remove the auto-takeover in `ConversationService.ReplyAsync`.

### 16. Catalog entries are facts only when a person confirms them
Products and plans have a **Confirmed** flag (a plan cannot be confirmed without a price). The AI is given confirmed, active entries only; otherwise tools answer `known=false` and the agent must say it does not know. The **School ERP sample** is created by a button (not seeded for everybody), is marked SAMPLE, contains `[PLACEHOLDER]` text only (no features, prices or demo process), and nothing in it is confirmed. **Change:** fill in and confirm the catalog.

### 17. Sensitive tools: what needs approval
`CreatePaymentRequest` always; a discount above the plan's maximum (default: none allowed), any custom terms, and `CreateTrialAccount` beyond the per-lead limit (default 1, editable) also create an `ApprovalRequest` instead of running. Approvals expire after 72 h (editable). Closing/disqualifying a lead is refused to the AI (open statuses only). `TransferToHuman` and `StopMarketingMessages` are always available to every agent because they only reduce what the AI does. **Change:** limits on the Sales agent page; the policy per tool is in `AgentToolCatalog`.

### 18. Opt-out by meaning: only when the classifier is sure
The strict keyword rule (Phase 5) still catches STOP-style messages. Additionally, when the intent classifier says `opt_out` with confidence ≥ 0.8 the platform itself runs `StopMarketingMessages` (suppression, consent revoked, chat blocked, AI paused) and sends nothing; below 0.8 a person decides (a false opt-out silently loses a customer). **Change:** threshold in `AiOrchestrator.CoreAsync`.

### 19. Demo, trial and payment tools answer "not available" until Phase 10
The Application defines `IDemoScheduler`, `ITrialProvisioner`, `IPaymentRequestGateway`; the registered implementations (`Unavailable…`) return "not connected" and nothing happens (architecture-tested: no fake success in the product). Phase 10 registers the real services; the tools, the slot check (a time that is not an available slot is refused) and the approvals are already in place. Subscriptions are never activated by any tool.

### 20. "QualifySchool" is `QualifyLead`
The platform is product-agnostic, so the tool records answers to the **tenant's own qualification questions** (configured per agent) instead of school-specific fields.
