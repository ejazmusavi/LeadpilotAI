MASTER DEVELOPMENT PROMPT — LEADPILOT AI

Project: LeadPilot AI

Tagline: Discover. Engage. Convert.

Act as a Principal Software Architect, Senior .NET Engineer, SaaS Product Architect, AI Engineer, Database Architect, and Technical Lead.

Your responsibility is to design and develop a complete, production-ready, multi-tenant AI-powered marketing, lead-generation, customer-engagement, and sales automation SaaS platform named LeadPilot AI.

This is a real commercial SaaS product, not a demonstration or a basic CRUD application.

You must implement the project phase by phase, following the architecture, requirements, security standards, and acceptance criteria in this document.

Do not skip phases, implement placeholder functionality, or claim a feature is complete when it is not.

---

1. BUSINESS VISION

LeadPilot AI is an AI-powered customer acquisition platform.

Initially, it will be used internally to acquire customers for an existing cloud-based School Management Software.

The initial target market is private schools across Pakistan, including Punjab, Sindh, Khyber Pakhtunkhwa, Gilgit-Baltistan, and Balochistan.

The platform should help:

1. Discover schools and businesses from approved data sources.
2. Research and enrich discovered leads.
3. Identify potential customers.
4. Organize leads in a CRM.
5. Manage contact permissions and marketing consent.
6. Run marketing campaigns.
7. Send and receive WhatsApp messages.
8. Automatically respond to customer inquiries using AI.
9. Qualify potential customers.
10. Schedule product demonstrations.
11. Follow up with interested prospects.
12. Create trial accounts where authorized.
13. Convert prospects into paying customers.
14. Track campaign performance and sales conversions.

Later, LeadPilot AI will become a general-purpose SaaS platform that other businesses can subscribe to and use for their own marketing and customer acquisition.

Important: Do not hardcode the application around schools. Schools are the first industry vertical, not the permanent limitation of the platform.

---

2. TECHNOLOGY STACK

Use the following technology stack.

Component| Technology
Backend| .NET 10 / C# 14
Dashboard| ASP.NET Core MVC
API| ASP.NET Core Web API
Frontend| Razor Views + Bootstrap 5
Database| MySQL 8
ORM| Entity Framework Core
Background Jobs| Hangfire
Message Broker| RabbitMQ
Caching| Redis
AI| LLM API
Knowledge Retrieval| RAG
Vector Storage| Qdrant or a suitable compatible provider
Mobile App| Flutter (optional later phase)
Deployment| Docker + Linux
Reverse Proxy| Nginx
Logging| Structured logging
Architecture| Clean Architecture + Modular Monolith

Use stable, compatible package versions.

Do not introduce unnecessary technologies or infrastructure.

Do not use microservices or Kubernetes for the initial version. Build a well-structured modular monolith with independent background worker processes where needed.

---

3. CORE ARCHITECTURE

Use Clean Architecture with clear separation of responsibilities.

Suggested solution structure:

LeadPilotAI/

- src/
  - LeadPilot.Domain/
  - LeadPilot.Application/
  - LeadPilot.Infrastructure/
  - LeadPilot.Web/
  - LeadPilot.Api/
  - LeadPilot.Worker/
  - LeadPilot.Shared/
- tests/
  - LeadPilot.UnitTests/
  - LeadPilot.IntegrationTests/
  - LeadPilot.ArchitectureTests/
- docker/
- docs/
- scripts/
- LeadPilotAI.sln

You may refine the structure if there is a strong architectural reason, but maintain the separation of concerns.

Architectural responsibilities

Domain

- Entities
- Value objects
- Domain events
- Business rules
- Enumerations
- Domain exceptions

Application

- Use cases
- Services
- Interfaces
- DTOs
- Validation
- Authorization requirements
- Application orchestration

Infrastructure

- EF Core
- MySQL
- Repository implementations where useful
- External integrations
- AI providers
- WhatsApp gateway adapter
- RabbitMQ
- Redis
- Background job implementations
- File storage

Web

- MVC dashboard
- Authentication UI
- Navigation
- Forms
- Reports
- Administration

API

- REST endpoints
- Authentication
- Webhooks
- Integration endpoints
- Mobile client endpoints

Worker

- Background processing
- Lead discovery
- Lead enrichment
- Campaign execution
- AI processing
- Follow-ups
- Queue consumers

Do not create unnecessary repository, service, or abstraction layers. Keep the implementation maintainable and practical.

---

4. MULTI-TENANT DESIGN

Multi-tenancy is mandatory from the beginning.

The system must support:

- Platform Owner
- SaaS Administrator
- Tenant Administrator
- Tenant Staff
- Sales Manager
- Sales Agent
- Marketing Manager
- Read-only users

Each tenant must have isolated:

- Users
- Leads
- Contacts
- Campaigns
- Conversations
- Messages
- AI agents
- Knowledge documents
- Workflows
- Opportunities
- Customers
- Subscriptions
- Analytics
- Integrations
- Usage records

Database strategy

Start with a shared MySQL database.

Every tenant-owned entity must have a TenantId.

Implement:

- Tenant resolution middleware
- Tenant context service
- EF Core global query filters where appropriate
- Explicit authorization checks
- Tenant-scoped background jobs
- Tenant-scoped AI knowledge retrieval
- Tenant-scoped cache keys
- Tenant-scoped file access
- Tenant-scoped audit logs

Never trust TenantId supplied by a client without authorization.

Do not rely exclusively on global query filters for security.

Add integration tests proving that one tenant cannot access another tenant's records through MVC, APIs, background jobs, or AI retrieval.

Keep the architecture extensible for dedicated databases for enterprise tenants in the future.

---

5. PRODUCT MODULES

The platform should eventually contain the following modules.

1. Dashboard
2. Tenant Management
3. User Management
4. Roles and Permissions
5. Lead Discovery
6. Lead Research and Enrichment
7. Leads and Contacts
8. CRM and Sales Pipeline
9. Campaign Management
10. WhatsApp Integration
11. Conversations Inbox
12. AI Sales Agents
13. AI Knowledge Base
14. Automation Workflows
15. Follow-up Management
16. Demo Scheduling
17. Trial Management
18. Customer Management
19. Subscription and Billing
20. Analytics and Reporting
21. Integrations
22. Audit Logs
23. Platform Settings

Build these modules in the phases below.

---

PHASE 0 — REQUIREMENTS, ARCHITECTURE AND PROJECT PLANNING

Before coding:

1. Analyze the complete requirements.
2. Define the system architecture.
3. Define project dependencies.
4. Define module boundaries.
5. Design the database schema.
6. Define tenant ownership for every entity.
7. Identify external integrations.
8. Define security boundaries.
9. Define background processing responsibilities.
10. Create an implementation roadmap.

Create documentation:

- docs/PROJECT_OVERVIEW.md
- docs/ARCHITECTURE.md
- docs/DATABASE_DESIGN.md
- docs/MULTI_TENANCY.md
- docs/SECURITY.md
- docs/INTEGRATIONS.md
- docs/IMPLEMENTATION_ROADMAP.md
- docs/PROGRESS.md

Do not begin implementing business modules until the architecture and project foundation are clear.

Acceptance criteria:

- All major modules are identified.
- Entity relationships are documented.
- Tenant isolation strategy is documented.
- No major architectural conflicts remain.
- Implementation phases and dependencies are clear.

---

PHASE 1 — PROJECT FOUNDATION

Create the complete .NET solution.

Implement:

- Clean Architecture projects
- Dependency injection
- Configuration management
- MySQL connection
- EF Core configuration
- Initial migrations
- Logging
- Global exception handling
- Validation framework
- API response conventions
- Swagger/OpenAPI
- Health checks
- Docker configuration
- Development environment configuration

Implement ASP.NET Core Identity authentication.

Include:

- Login
- Logout
- Password reset
- User registration where appropriate
- Secure password handling
- Session management
- Authentication policies

Create the initial responsive Bootstrap MVC dashboard.

The dashboard should include:

- Sidebar navigation
- Top navigation
- User menu
- Breadcrumbs
- Notification area
- Responsive layout
- Empty-state components
- Loading and error states

Do not implement all module pages yet.

Acceptance criteria:

- Solution builds successfully.
- Database connection works.
- Initial migrations apply successfully.
- Login and logout work.
- API documentation is available.
- Docker environment starts successfully.
- No cross-layer dependency violations.

---

PHASE 2 — TENANCY, USERS, ROLES AND PERMISSIONS

Implement the multi-tenant foundation.

Create entities such as:

- Tenant
- TenantSetting
- UserProfile
- Role
- Permission
- UserRole
- RolePermission
- UserTenant
- TenantInvitation
- AuditLog

Implement:

- Tenant creation
- Tenant activation and suspension
- Tenant configuration
- User invitation
- User management
- Role management
- Permission management
- User-role assignment
- Tenant switching for authorized platform users
- Tenant-specific settings

Use permission codes such as:

- Leads.View
- Leads.Create
- Leads.Update
- Leads.Delete
- Campaigns.View
- Campaigns.Create
- Campaigns.Execute
- Conversations.View
- Conversations.Reply
- Agents.Manage
- Reports.View
- Users.Manage
- Settings.Manage

Authorization must be enforced at both UI and backend levels.

Do not treat hidden navigation items as security.

Acceptance criteria:

- Two tenants can operate independently.
- Tenant users cannot access other tenant data.
- Permissions are checked at the server.
- Unauthorized API requests return appropriate responses.
- Tenant isolation tests pass.

---

PHASE 3 — LEAD MANAGEMENT AND CRM

Build the core CRM before advanced AI automation.

Entities

Create appropriate entities for:

- Lead
- Contact
- LeadSource
- LeadActivity
- LeadNote
- LeadTag
- LeadScore
- LeadStatus
- Pipeline
- PipelineStage
- Opportunity
- OpportunityActivity
- ConsentRecord
- SuppressionList

A lead may represent a school, company, organization, or individual business.

One lead can have multiple contacts.

One contact can have multiple conversations across channels.

Do not combine leads, contacts, conversations, and customers into a single entity.

Lead pipeline

Implement:

- New
- Researched
- Contact Eligible
- Contacted
- Responded
- Interested
- Qualified
- Demo Scheduled
- Demo Completed
- Trial
- Negotiation
- Won
- Lost
- Suppressed

CRM functionality

- Create, update, view, archive leads
- Manage contacts
- Assign leads to team members
- Add notes
- Add tags
- Track activities
- Change pipeline stages
- Search and filter
- Bulk operations with authorization
- Import CSV and Excel files
- Export authorized records
- Duplicate detection
- Lead source tracking
- Consent history
- Suppression management

Lead scoring

Implement configurable scoring based on:

- Business category
- Location
- Website availability
- Contact information quality
- Business activity
- Verified business information
- Engagement
- Customer interest

Keep lead quality separate from permission to contact.

A high score must never override missing consent or a suppression record.

Acceptance criteria:

- CRM CRUD works.
- Multiple contacts per lead work.
- CSV imports validate and report errors.
- Duplicate leads are detected.
- Consent records are auditable.
- Suppressed contacts cannot enter eligible campaign audiences.

---

PHASE 4 — LEAD DISCOVERY AND ENRICHMENT

Build a configurable lead discovery engine.

Initial target: schools across Pakistan.

Support searches by:

- Country
- Province
- City
- District
- Area
- Business category
- Search keywords

Initial school categories:

- Private schools
- Primary schools
- Secondary schools
- Higher secondary schools
- Colleges
- Academies
- Educational institutions

Approved discovery sources

Build provider-independent connectors for:

1. Google Places API
2. Public school websites
3. Authorized business directories
4. CSV and Excel imports
5. Manual entry
6. Authorized lead-generation integrations
7. Meta Lead Ads integrations where authorized

Do not scrape protected websites or platforms in violation of their terms.

Do not use unauthorized methods to obtain personal phone numbers or email addresses.

Respect data source restrictions, caching rules, attribution requirements, and retention policies.

Discovery workflow

1. User selects location and category.
2. Discovery job is created.
3. Background worker processes the search.
4. Results are normalized.
5. Duplicate records are detected.
6. Available business information is enriched.
7. Source and retrieval time are recorded.
8. Confidence level is calculated.
9. Lead is saved.
10. Lead appears in CRM.

Lead enrichment fields

- Business name
- Business category
- Country
- Province
- City
- Address
- Website
- Public business phone
- Public business email
- Source
- Source URL or external identifier
- Last verification date
- Confidence level
- Research notes

Do not invent student strength, existing software, decision-maker names, or other business facts.

Mark unverified information clearly.

Discovery management

Implement:

- Discovery jobs
- Search history
- Job progress
- Retry
- Failure reporting
- Duplicate detection
- Search result review
- Manual approval
- Usage tracking

Acceptance criteria:

- A user can search for schools in a selected Pakistani city.
- Results are saved with source information.
- Duplicate records are not unnecessarily created.
- Failed jobs can be retried safely.
- Data source rules are respected.

---

PHASE 5 — WHATSAPP GATEWAY INTEGRATION

IMPORTANT: I ALREADY HAVE MY OWN WHATSAPP API/GATEWAY.

It reportedly supports all required capabilities:

- Sending messages
- Receiving messages
- Webhooks
- Delivery status
- Read status
- Account/session management

Do not replace this gateway.

Do not migrate the application to another WhatsApp provider without explicit approval.

First inspect the gateway documentation, API endpoints, authentication mechanism, webhook format, and response structure when those materials are supplied.

Build a provider-independent adapter around my existing gateway.

Interface

Create an abstraction similar to:

IWhatsAppGateway

Required operations:

- SendTextAsync
- SendTemplateAsync
- GetMessageStatusAsync
- MarkAsReadAsync
- GetAccountsAsync
- GetAccountStatusAsync
- ConnectAccountAsync
- DisconnectAccountAsync
- ProcessWebhookAsync

Adapt method signatures to the actual gateway capabilities.

Do not assume endpoint names or payload formats.

Entities

- Channel
- ChannelAccount
- ChannelCredential
- Conversation
- Message
- MessageAttachment
- MessageStatusEvent
- WebhookEvent
- MessageTemplate

Incoming message flow

1. Receive webhook.
2. Validate the webhook.
3. Identify tenant and connected account.
4. Normalize payload.
5. Validate event uniqueness.
6. Store incoming message.
7. Update conversation.
8. Publish RabbitMQ event.
9. Process asynchronously.
10. Invoke AI when enabled.
11. Send response through the gateway.
12. Store outgoing message.
13. Update delivery/read status.

Outgoing message flow

1. Validate tenant.
2. Validate contact eligibility.
3. Check consent and suppression status.
4. Validate campaign or conversation permissions.
5. Send through gateway adapter.
6. Store gateway response.
7. Track delivery and read events.

Reliability

Implement:

- Webhook idempotency
- Retry policies
- Durable event processing
- Dead-letter handling
- Message correlation IDs
- Gateway error handling
- Delivery tracking
- Audit history
- Rate limiting

Do not send duplicate messages when a worker retries.

If the gateway uses WhatsApp Web/QR automation rather than an officially supported Business Platform integration, clearly document the operational, policy, and account-risk implications. Do not assume that technical capabilities mean unrestricted bulk marketing is permitted.

Acceptance criteria:

- Gateway connection works using actual documentation.
- Incoming messages are persisted exactly once.
- Outgoing messages are tracked.
- Delivery and read events update correctly.
- Gateway failures are recoverable.
- Existing gateway remains intact.

---

PHASE 6 — CAMPAIGN MANAGEMENT AND MARKETING AUTOMATION

Implement a campaign management module.

Campaign types

- WhatsApp campaigns
- Manual campaigns
- Scheduled campaigns
- Follow-up campaigns
- Re-engagement campaigns
- Future email and other channel campaigns

Campaign functionality

- Create campaign
- Define audience
- Apply filters
- Exclude suppressed contacts
- Check consent eligibility
- Create message templates
- Schedule campaigns
- Pause and resume
- Cancel campaigns
- Monitor progress
- Track failures
- Track delivery
- Track replies
- Track conversions

Campaign execution

Use Hangfire and RabbitMQ appropriately.

Hangfire should handle scheduling and recurring jobs.

RabbitMQ should handle durable asynchronous event distribution.

Avoid creating two competing scheduling systems.

Implement:

- Batch processing
- Rate limits
- Retry policies
- Campaign cancellation
- Per-tenant usage limits
- Execution logs
- Duplicate prevention

Marketing permission must be validated immediately before sending, not only when the audience is initially selected.

Implement suppression for:

- Explicit opt-outs
- Invalid contacts
- Blocked contacts
- Tenant-specific exclusions
- Other applicable legal or platform restrictions

Acceptance criteria:

- Campaign scheduling works.
- Campaign execution is recoverable.
- Suppressed contacts are never sent marketing messages.
- Campaign status and statistics are accurate.
- Retries do not create duplicate messages.

---

PHASE 7 — AI ENGINE AND KNOWLEDGE BASE

Build a provider-independent AI integration architecture.

Create interfaces for:

- IAIProvider
- IEmbeddingProvider
- IVectorStore
- IKnowledgeRetriever
- IConversationMemory
- IAIOrchestrator

Do not tightly couple business logic to one LLM provider.

AI capabilities

- Understand incoming messages.
- Identify customer intent.
- Classify inquiries.
- Extract relevant information.
- Retrieve tenant-specific product information.
- Generate grounded responses.
- Qualify leads.
- Summarize conversations.
- Recommend next actions.
- Schedule follow-ups.
- Escalate to human staff.

Knowledge Base

Create:

- KnowledgeDocument
- DocumentVersion
- DocumentChunk
- EmbeddingRecord
- KnowledgeSource
- RetrievalLog

Support documents such as:

- Product descriptions
- Pricing plans
- FAQs
- Feature documentation
- Sales scripts
- Policies
- Support documentation
- Website content

Implement document ingestion, text extraction, chunking, embeddings, indexing, retrieval, and updates.

All knowledge retrieval must be tenant-isolated.

Never allow one tenant's AI agent to retrieve another tenant's private knowledge.

RAG workflow

1. Receive customer message.
2. Identify tenant and conversation.
3. Retrieve conversation history.
4. Detect intent.
5. Retrieve relevant tenant knowledge.
6. Construct controlled AI context.
7. Generate response.
8. Validate response against available information and policies.
9. Execute permitted actions if required.
10. Send response or escalate.

AI safety

- Do not invent product features.
- Do not invent prices.
- Do not invent discounts.
- Do not claim a demo or payment has been completed without verification.
- Treat external documents and customer messages as untrusted input.
- Protect against prompt injection.
- Avoid exposing internal prompts, credentials, or tenant data.
- Log tool execution.
- Escalate uncertain or sensitive requests.

Acceptance criteria:

- AI answers from tenant-specific knowledge.
- Unsupported questions trigger an appropriate response.
- Tenant isolation works for retrieval.
- AI cannot execute unauthorized actions.
- Conversation history is maintained correctly.

---

PHASE 8 — AI SALES AGENT AND CONTROLLED TOOL CALLING

Build a sales-oriented AI agent.

The AI agent should behave like a trained sales representative, not an unrestricted chatbot.

Responsibilities

- Welcome prospects.
- Understand their requirements.
- Answer product questions.
- Identify their business type.
- Understand customer needs.
- Qualify prospects.
- Explain relevant product benefits.
- Handle common objections.
- Schedule demos.
- Arrange follow-ups.
- Transfer conversations to human staff.
- Update CRM records.

Initial School ERP sales agent

The initial agent should understand:

- School management problems
- Student admission
- Fee management
- Attendance
- Examination and results
- Timetable
- Parent communication
- School administration
- Available School ERP features
- Pricing and subscription plans
- Demo process
- Trial process

Use actual product information supplied by the user. Never fabricate product capabilities.

Controlled tools

Implement authorized tools such as:

- GetProductInformation
- GetPricingPlans
- GetLeadProfile
- UpdateLeadStatus
- QualifySchool
- CheckDemoAvailability
- BookDemo
- ScheduleFollowUp
- CreateTrialAccount
- CreatePaymentRequest
- TransferToHuman
- StopMarketingMessages

Each tool must:

- Validate the tenant.
- Validate the user's or agent's permissions.
- Validate inputs.
- Record execution.
- Return structured results.
- Handle errors safely.

Do not give the LLM direct database access or arbitrary SQL execution.

Require human approval for sensitive actions such as exceptional discounts, unusual commercial commitments, and uncertain negotiations.

A subscription must not be activated merely because the AI claims payment was received. Require verified payment confirmation.

Human handoff

Implement:

- Human takeover
- AI pause/resume
- Agent assignment
- Internal notes
- Conversation status
- Escalation reason
- Assignment history

Acceptance criteria:

- AI can qualify a school using verified information.
- AI can retrieve actual pricing.
- AI can book a demo only through an authorized tool.
- Human staff can take over.
- AI does not send further automated replies when human control is active.

---

PHASE 9 — AUTOMATION WORKFLOW ENGINE

Build a configurable workflow engine.

Do not hardcode every follow-up sequence.

Example triggers

- New lead created
- Lead enriched
- Lead becomes contact eligible
- Incoming WhatsApp message
- Customer replies
- Lead becomes interested
- Demo booked
- Demo completed
- Trial created
- Trial expires
- Payment confirmed
- Lead becomes inactive

Example actions

- Assign lead
- Change lead status
- Send approved message
- Wait for a period
- Create follow-up task
- Notify sales representative
- Schedule demo
- Escalate to human
- Update CRM
- Stop workflow

Implement:

- Workflow
- WorkflowVersion
- WorkflowTrigger
- WorkflowStep
- WorkflowExecution
- WorkflowExecutionLog

Support conditions, delays, execution history, retries, and cancellation.

Avoid arbitrary code execution inside workflows.

Acceptance criteria:

- A workflow can be created and activated.
- Trigger conditions work.
- Steps execute in the intended order.
- Failed steps can be retried safely.
- A workflow cannot send messages to suppressed contacts.

---

PHASE 10 — DEMO, TRIAL AND CUSTOMER CONVERSION

Build the sales conversion module.

Demo management

- Demo calendar
- Available time slots
- Booking
- Rescheduling
- Cancellation
- Sales representative assignment
- Demo status
- Meeting links
- Reminders

Trial management

- Create trial account
- Assign product plan
- Set trial duration
- Track trial usage
- Trial expiry notifications
- Trial conversion
- Trial cancellation

Customer conversion

When an opportunity is won:

1. Convert prospect to customer.
2. Preserve CRM history.
3. Create customer account.
4. Assign subscription plan.
5. Record commercial agreement.
6. Create payment request where applicable.
7. Verify payment through an authorized integration.
8. Activate subscription only after verified payment.

Support future integration with payment providers.

Do not assume a payment has succeeded based on an AI-generated response or manual message.

Acceptance criteria:

- Demo booking works.
- Trial creation is authorized.
- Conversion preserves lead history.
- Payment verification controls subscription activation.

---

PHASE 11 — ANALYTICS AND REPORTING

Implement tenant-specific dashboards and reports.

Dashboard metrics

- Total leads
- New leads
- Verified leads
- Contact-eligible leads
- Contacted leads
- Responses
- Interested prospects
- Qualified leads
- Demos scheduled
- Demos completed
- Trials
- Customers won
- Lost opportunities
- Campaign performance
- AI conversations
- Human handoffs
- Message delivery rate
- Lead conversion rate

Reports

- Lead discovery by city
- Lead source performance
- Campaign performance
- Sales pipeline
- Conversion funnel
- Sales representative performance
- AI agent performance
- Demo conversion
- Trial conversion
- Subscription revenue
- Messaging usage

All metrics must be based on actual recorded events.

Clearly distinguish estimated, calculated, and verified values.

Ensure that large datasets can be queried efficiently.

Use pagination, appropriate indexes, aggregation queries, and caching where appropriate.

Acceptance criteria:

- Dashboard metrics match underlying records.
- Tenant reports are isolated.
- Date filters work.
- Large datasets do not require loading all records into memory.

---

PHASE 12 — SAAS COMMERCIALIZATION

Prepare LeadPilot AI for external customers.

Implement:

- Tenant self-registration
- Tenant onboarding
- Subscription plans
- Billing cycles
- Trial plans
- Feature entitlements
- Usage limits
- Tenant suspension
- Upgrade and downgrade
- Subscription renewal
- Payment history
- Invoice records
- Platform administration

Example pricing dimensions

- Number of users
- Number of connected messaging accounts
- Monthly messages
- AI tokens or usage
- Number of leads
- Number of campaigns
- Knowledge base storage
- Automation executions

Do not hardcode commercial prices.

Allow platform administrators to configure plans.

Implement usage metering and enforcement.

A tenant must not exceed plan limits without the appropriate authorization or upgrade.

---

PHASE 13 — GENERAL-PURPOSE BUSINESS SUPPORT

Prepare the platform for industries beyond education.

Implement configurable:

- Business categories
- Product catalogs
- Services
- Pricing catalogs
- Qualification questions
- Sales pipelines
- AI agent instructions
- Knowledge bases
- Workflows
- Campaign templates
- Custom fields
- Lead scoring rules

Do not create separate hardcoded applications for each industry.

Instead, use tenant-level configuration and reusable modules.

The initial School ERP sales agent should be one industry-specific configuration of the general sales agent framework.

Future industries may include:

- Hotels
- Travel agencies
- Real estate
- Clinics
- Software companies
- Retail businesses
- Professional services

Do not implement all industry-specific features in the initial MVP.

---

PHASE 14 — FLUTTER MOBILE CLIENT

This is an optional later phase.

Create Flutter applications for:

- Android
- iOS

Use the existing ASP.NET Core API.

Initial mobile functionality:

- Login
- Dashboard
- Leads
- Lead details
- Conversations
- Send replies
- Notifications
- Tasks
- Demo bookings
- Sales pipeline

Do not duplicate business logic in Flutter.

All business rules and authorization must remain in the backend.

---

PHASE 15 — PRODUCTION HARDENING AND DEPLOYMENT

Prepare the system for commercial production.

Security

- Secure authentication
- RBAC
- Tenant isolation
- API authorization
- Input validation
- Rate limiting
- Secure secret management
- Encrypted integration credentials
- Webhook signature verification where supported
- Audit logging
- Data retention controls
- Secure file access
- HTTPS
- Backup and recovery

Reliability

- Database backups
- Restore testing
- RabbitMQ durability
- Dead-letter queues
- Hangfire monitoring
- Redis availability handling
- Retry policies
- Idempotent processing
- Health checks
- Error monitoring

Deployment

Create:

- Dockerfiles
- Docker Compose
- Nginx configuration
- Linux deployment documentation
- Environment configuration
- Database migration procedure
- Backup scripts
- Restore scripts
- Logging and monitoring configuration

Do not put production credentials in source control.

Do not expose internal infrastructure services publicly without a clear security requirement.

Performance

Test:

- Concurrent API requests
- Lead imports
- Large CRM queries
- Campaign processing
- Webhook bursts
- AI request concurrency
- Background job throughput

Do not claim scalability based on architecture alone. Measure actual performance.

---

6. DATABASE DESIGN GUIDELINES

Follow these rules throughout implementation:

- Use Guid identifiers unless a strong reason requires otherwise.
- Use TenantId for tenant-owned records.
- Use UTC for stored timestamps.
- Use decimal(18,2) for monetary values.
- Store phone numbers in normalized E.164 format where possible.
- Add appropriate indexes.
- Add unique constraints for deduplication and idempotency.
- Use soft delete where business history must be retained.
- Maintain audit fields.
- Avoid storing unnecessary personal information.
- Use explicit relationships and delete behaviors.
- Use migrations for all schema changes.

Do not create a single massive entity for leads, contacts, conversations, messages, and customers.

Keep domain boundaries clear.

---

7. USER INTERFACE REQUIREMENTS

The dashboard must be professional, clean, modern, and responsive.

Use Bootstrap 5.

Requirements:

- Desktop-first administrative dashboard with responsive behavior.
- Sidebar navigation on desktop.
- Appropriate mobile navigation.
- Responsive tables.
- Responsive forms.
- Search and filter panels.
- Modal dialogs where appropriate.
- Pagination.
- Toast notifications.
- Loading indicators.
- Empty states.
- Validation messages.
- Confirmation dialogs for destructive operations.

Form layouts must adapt to viewport size.

For example:

- Desktop: three or four fields per row where appropriate.
- Tablet: two fields per row.
- Mobile: one field per row.

Do not simply scale down the desktop layout.

Use consistent spacing, typography, colors, buttons, tables, cards, and form components.

Do not introduce a frontend framework without approval.

---

8. BACKGROUND PROCESSING RULES

Use Hangfire for:

- Scheduled discovery jobs
- Recurring jobs
- Campaign scheduling
- Follow-up scheduling
- Cleanup tasks
- Scheduled reports

Use RabbitMQ for:

- Incoming message events
- AI processing events
- Lead enrichment events
- Campaign delivery events
- Domain integration events
- Other durable asynchronous workflows

Implement:

- Idempotent consumers
- Retry policies
- Dead-letter handling
- Correlation IDs
- Tenant context propagation
- Structured logging

Consider the transactional outbox pattern where database changes and message publication must remain consistent.

Do not assume that publishing a message and committing a database transaction are automatically atomic.

---

9. AI AND MARKETING COMPLIANCE

Compliance must be designed into the application, not added at the end.

Implement:

- Consent records
- Contact eligibility checks
- Suppression lists
- Opt-out handling
- Tenant-level messaging policies
- Campaign approval where appropriate
- Message audit history
- Human escalation
- Data retention controls

Before initiating marketing outreach, verify the applicable legal and platform requirements.

For WhatsApp Business Platform usage, respect applicable opt-in requirements, approved template rules for business-initiated messages, and the customer service window.

Do not assume that a publicly listed phone number automatically grants permission for promotional messaging.

Do not use unauthorized scraping or bulk extraction of personal contact information.

For external data providers, follow their API terms, licensing, attribution, retention, and caching requirements.

Keep the system technically capable of distinguishing:

- Publicly discovered contact information
- Verified contact information
- Consent to receive marketing
- Consent source
- Consent timestamp
- Suppression status

Discovery is not permission to contact.

---

10. TESTING STRATEGY

Testing is mandatory in every phase.

Create:

Unit tests

- Domain rules
- Validation
- Lead scoring
- Permission evaluation
- Campaign eligibility
- Workflow conditions
- AI tool authorization

Integration tests

- MySQL persistence
- Tenant isolation
- API authentication
- Webhook processing
- RabbitMQ consumers
- Hangfire jobs
- Gateway adapter
- AI retrieval

Architecture tests

- Dependency direction
- Domain independence
- Module boundaries
- Forbidden references

Critical acceptance scenarios

1. Tenant A cannot access Tenant B's leads.
2. Tenant A cannot retrieve Tenant B's AI knowledge.
3. A suppressed contact cannot receive a campaign message.
4. A repeated webhook does not create duplicate messages.
5. A failed worker can retry without duplicate side effects.
6. AI cannot invent pricing or product features as verified facts.
7. AI cannot execute unauthorized tools.
8. Human takeover pauses automatic AI responses.
9. A demo cannot be booked outside available slots.
10. A subscription cannot activate without verified payment.
11. Campaign analytics reflect actual message events.
12. Tenant usage limits are enforced.

Build realistic test data and conversation scenarios.

For the AI sales agent, create at least 100 representative school-sales conversation scenarios covering common questions, objections, pricing questions, irrelevant messages, opt-outs, uncertain answers, and escalation cases.

---

11. DEVELOPMENT WORKING RULES

These instructions are mandatory.

Rule 1 — Work phase by phase

Implement only the current phase.

Do not start a later phase until the current phase meets its acceptance criteria.

Rule 2 — Inspect before changing

Before editing existing code, inspect the relevant files, dependencies, conventions, and current implementation.

Do not blindly overwrite working code.

Rule 3 — Maintain project consistency

Use consistent naming, namespaces, dependency injection, error handling, validation, logging, and database conventions.

Do not create duplicate abstractions or competing implementations.

Rule 4 — No fake functionality

Do not create buttons that do nothing.

Do not use hardcoded dashboard metrics as if they were real.

Do not return fake successful responses from integrations.

Do not leave important business functionality as TODO comments.

Mocks are permitted only in tests or clearly labeled development environments.

Rule 5 — No unnecessary scope expansion

Do not introduce additional modules, frameworks, providers, infrastructure, or technologies without explaining the reason and receiving approval where the change affects the agreed architecture.

Rule 6 — Security first

All endpoints, background jobs, AI tools, and data retrieval operations must enforce tenant and permission boundaries.

Rule 7 — Update documentation

Maintain:

- docs/PROGRESS.md
- docs/ARCHITECTURE.md
- docs/DATABASE_DESIGN.md
- docs/IMPLEMENTATION_ROADMAP.md

Update progress at the end of each phase.

Rule 8 — Verify before claiming completion

Run relevant builds, tests, migrations, and integration checks.

Report actual results, not assumptions.

Rule 9 — Preserve the existing WhatsApp gateway

Do not replace it.

Build the adapter using its actual API documentation and capabilities.

Rule 10 — Keep the MVP practical

Prioritize the shortest reliable path to acquiring School ERP customers.

Do not build advanced enterprise features before the core acquisition workflow works.

---

12. REQUIRED PHASE COMPLETION REPORT

At the end of every phase, provide a report in this format:

Phase Completion Report

Phase: Name and number

Status: Completed / Partially Completed / Blocked

1. Implemented

List actual completed functionality.

2. Files created or modified

List important files and projects.

3. Database changes

List entities, relationships, migrations, indexes, and constraints.

4. Tests

Report:

- Build result
- Unit test result
- Integration test result
- Failed tests
- Tests not yet executed

5. Security and architecture review

Identify relevant concerns and how they were addressed.

6. Outstanding issues

List remaining work.

7. Acceptance criteria

Mark each criterion as passed, failed, or not verified.

8. Next phase

State the next phase and its prerequisites.

Do not mark a phase completed if any mandatory acceptance criterion remains unverified.

---

13. INITIAL MVP PRIORITY

Although this document describes the complete product, the first commercially useful version should prioritize:

1. Multi-tenant foundation
2. Authentication and permissions
3. School lead CRM
4. Approved school discovery
5. Existing WhatsApp gateway integration
6. Consent and suppression management
7. AI knowledge base
8. AI sales agent
9. Demo booking
10. Follow-up automation
11. Basic analytics

Subscription billing, Flutter, advanced industry configurations, and sophisticated enterprise functionality can follow after the internal School ERP acquisition workflow works reliably.

---

14. YOUR FIRST TASK

Start with PHASE 0 — REQUIREMENTS, ARCHITECTURE AND PROJECT PLANNING.

Do not immediately generate the entire application.

First:

1. Analyze this complete specification.
2. Identify ambiguities and technical conflicts.
3. Propose the final solution structure.
4. Design the initial database entities and relationships.
5. Define tenant isolation.
6. Define the module boundaries.
7. Identify external integration contracts.
8. Create the phase-wise implementation plan.
9. Identify the minimum information you need from me, especially the existing WhatsApp gateway documentation and the School ERP product information.

Ask only essential questions.

After Phase 0 is approved, proceed to Phase 1.

Final objective: Deliver a secure, reliable, maintainable, production-ready LeadPilot AI SaaS platform that can discover eligible prospects, manage leads, communicate through my existing WhatsApp gateway, use AI to qualify customers, arrange demos, automate follow-ups, and convert prospects into paying customers — initially for my School ERP and eventually for other businesses.

Build it as a real commercial product, with working functionality, proper testing, documentation, and a clear path to production deployment.