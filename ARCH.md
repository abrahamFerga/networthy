# Networthy — Architecture

## Context (C4 L1)

Three personas (household admin, household member, self-hosting power user) interact with
Networthy as a single system. External systems: Plaid (bank linking), the tenant's configured
AI provider (OpenAI/Azure OpenAI/Anthropic, via Plenipo's existing pluggable per-tenant AI
connection), an SMTP relay (budget recaps and reminders, via Plenipo's existing email
notification channel), and Entra ID (OIDC authentication in production).

Diagram: [`docs/diagrams/c1-context.mmd`](docs/diagrams/c1-context.mmd)

## Containers (C4 L2)

- **Domain UI** — the React SPA (`@plenipo/ui` app shell, Networthy-branded via
  `VITE_BRAND_NAME`), served at `/` by `Networthy.Host` (`UsePlenipoDomainUi`) in production, or
  as a Vite dev server locally.
- **Admin console** — `@plenipo/admin-ui`, served at `/admin` by the same host
  (`UsePlenipoAdminConsole`) — security/RBAC/usage/audit for platform admins, unchanged from
  every other Plenipo product.
- **Networthy.Host** — the ASP.NET Core API: Plenipo platform (auth, multi-tenancy, RBAC,
  audit, background jobs, connector registry) plus the `Networthy.Finance` module. Serves
  reads over REST, writes over AG-UI/SignalR chat (see ADR-0002).
- **Networthy.AppHost** — Aspire local orchestration (dev-only), the same shape as
  `Casewell.AppHost`.
- **Postgres** — `plenipo-platform` (operational data) and `plenipo-audit` (append-only audit
  log) databases.
- **Redis** — SignalR backplane, rate-limit windows; inherited from Plenipo, not reconfigured.
- **Azure Blob Storage** — statement uploads and generated documents (pre-bill-style exports,
  if any), via the `file-storage` capability declared in `workflow.json`.
- **Plaid**, **Azure Document Intelligence** (scanned-statement OCR fallback), **SMTP relay** —
  external systems the host calls out to.

Diagram: [`docs/diagrams/c2-containers.mmd`](docs/diagrams/c2-containers.mmd)

## Components (C4 L3) — Networthy.Host

Inside `Networthy.Host`: the Plenipo platform packages (auth/RBAC/multi-tenancy, the authorized
agent runner with its approval gate and audit log, the background job queue, the connector SDK,
and the domain-UI/admin-console static hosting) alongside `Networthy.Finance`'s tools — the
original six (`AccountTools`, `TransactionTools`, `StatementImportTools` plus its job handler,
`BudgetTools`, `HouseholdTools`, `ApprovalSurfaceTools`) plus everything the "Delivered since v1"
wave added (SPEC.md): `GoalTools`/`GoalPlanning`, `HealthTools`, `IncomeSourceTools`,
`RecurringTools`/`RecurringDetection`, `HouseholdSettingsTools`/`HouseholdContext`,
`ExportTools`, `AffordabilityTools`, plus the hosted services `BillReminderService`,
`BudgetRolloverService`, `NetWorthSnapshotService` and the job handlers `DailyDigestJobHandler`,
`StatementReminderJobHandler`. The v2 dashboard/visualization reads (`OverviewEndpoint`,
`SpendingEndpoint`, `CashFlowEndpoint`, `UpcomingBillsEndpoint`) and the direct-human-bookkeeping
`ManualCrudEndpoints` (RBAC-gated, not approval-gated — see API surface below) round out the
module. `FinanceToolSource` is the DI-bound tool catalog; `FinanceModule.Manifest.Tools` is the
pinned list tests assert against. `PlaidConnector` is drawn
separately: it is a **Networthy-repo connector** (ADR-0007, superseding ADR-0003) — defined in
this repo against `Plenipo.Connectors.Sdk`, registered like a built-in, but not part of
`Networthy.Finance`'s domain logic.

Diagram: [`docs/diagrams/c3-components-host.mmd`](docs/diagrams/c3-components-host.mmd)

## Solution layout

Per ADR-0001, Networthy does not use the generic from-scratch layered template — it is a thin
host on Plenipo platform packages, the same shape Casewell proved out:

```text
src/
  Networthy.AppHost/     ← Aspire local orchestration (dev-only)
  Networthy.Host/        ← thin ASP.NET Core host: AddPlenipoPlatform() +
                            AddPlenipoModule<FinanceModule>() +
                            AddPlenipoConnector<PlaidConnector>() +
                            AddPlenipoRole("household-member", [...])
  Networthy.Finance/      ← the one domain module (IModule + manifest). v1's six tool files —
                              AccountTools.cs
                              TransactionTools.cs
                              StatementImportTools.cs (+ StatementParseJobHandler.cs,
                                StatementExtraction.cs, PlatformDocumentStatementExtractor.cs)
                              BudgetTools.cs (+ BudgetRolloverService.cs)
                              HouseholdTools.cs
                              ApprovalSurfaceTools.cs
                            — plus the "Delivered since v1" wave (SPEC.md):
                              AffordabilityTools.cs, GoalTools.cs, GoalPlanning.cs,
                              HealthTools.cs, IncomeSourceTools.cs, RecurringTools.cs
                                (+ RecurringDetection.cs), HouseholdSettingsTools.cs,
                              HouseholdContext.cs, ExportTools.cs, BillReminderService.cs,
                              NetWorthSnapshotService.cs, DailyDigestJobHandler.cs,
                              StatementReminderJobHandler.cs
                            — plus the v2 dashboard/visualization reads and the manual-CRUD
                              REST surface (see API surface below):
                              OverviewEndpoint.cs, SpendingEndpoint.cs, CashFlowEndpoint.cs,
                              UpcomingBillsEndpoint.cs, ManualCrudEndpoints.cs
                            — and the module scaffolding itself:
                              FinanceModule.cs (manifest + pinned tool list),
                              FinanceToolSource.cs (DI-bound tool catalog)
                              Persistence/
                                FinanceDbContext.cs (+ FinanceDbContextFactory.cs)
                                Account.cs, Transaction.cs, Category.cs, Budget.cs,
                                StatementImportBatch.cs, PlaidLinkedAccount.cs,
                                Goal.cs, IncomeSource.cs, BillReminder.cs, StatementReminder.cs,
                                HouseholdSettings.cs, ExchangeRate.cs
                                Migrations/
  Networthy.Connectors.Plaid/ ← the domain-specific Plaid connector (ADR-0007):
                              IConnector + IConnectorToolSource against Plenipo.Connectors.Sdk,
                              in THIS repo — not Plenipo core, not Networthy.Finance's domain logic
                              (PlaidClient.cs, PlaidConnector.cs, PlaidTools.cs)

tests/
  Networthy.Finance.Tests/  ← unit tests + module-composition guard tests
                               (mirrors Casewell.Legal.Tests: manifest tool-list pinning,
                               approval-gating assertions, persona/role guard tests) — also
                               where the Plaid connector's own mapping tests
                               (PlaidMappingTests.cs) live; there is no separate
                               Networthy.Connectors.Plaid.Tests project.
  Networthy.IntegrationTests/ ← the whole journey E2E (see README.md)
```

No `Domain`/`Application`/`Infrastructure`/`Api` split, and no `Networthy.Infrastructure.Azure`
project — cloud-specific behavior Networthy itself needs (none identified for v1; Blob storage
and Document Intelligence are consumed through Plenipo's existing `IFileStore`/OCR seams, not
reimplemented) would live there if it ever arises.

## Cross-cutting wiring

- **AuthN** — inherited from Plenipo (`AddPlenipoAuthentication`): OIDC/Entra ID in production,
  dev-auth headers locally. No Networthy-specific auth code.
- **RBAC** — `household-admin` and `household-member` roles, registered via
  `AddPlenipoRole(...)` in `Networthy.Host` (same call Casewell uses for `paralegal`). Policies
  are `tools.finance.*` permissions bound to each `ModuleTool.Permission`, exactly like every
  other Plenipo module.
- **Multi-tenancy** — a household *is* a Plenipo `Tenant`; every `Networthy.Finance` entity
  implements `ITenantOwned` and is covered by Plenipo's global EF query filter. No new
  multi-tenancy code.
- **Observability** — inherited via `Plenipo.ServiceDefaults` (OpenTelemetry traces/metrics/
  logs, health checks). No Networthy-specific wiring.
- **Audit** — every approval-gated tool write is captured by Plenipo's existing append-only
  audit log automatically; `Transactions.LogOwn` (ADR-0005) is still logged as a tool
  invocation, just not held for approval first.
- **Resilience** — `PlaidConnector`'s outbound calls use the same `HttpClientFactory` +
  resilience-handler registration pattern the built-in S3/Documenso connectors use (ADR-0007) —
  the connector lives in this repo but reuses the platform's established resilience wiring, no
  Networthy-specific Polly configuration.
- **Caching** — Redis, inherited from Plenipo (SignalR backplane, rate limiting). No
  Networthy-specific cache usage identified for v1.
- **Background work** — the `StatementImportTools` parse job runs on Plenipo's existing
  `IJobQueue`/`IJobHandler` primitive (the exact shape of Casewell's `BulkReviewJobHandler`).
  Budget-period rollover and the daily net-worth snapshot are lightweight `PeriodicTimer`
  hosted services registered by `Networthy.Finance` (the shape of Casewell's
  `DeadlineReminderService`). Plaid transaction sync is scheduled (every 6h), and — because
  it's an external API call with real failure modes — goes through the platform's job primitive
  rather than a bare timer, giving it retry/lease semantics for free. Webhook-triggered sync
  (in addition to the schedule) is not built yet — see the Idempotency note below.
- **Idempotency** — for chat-tool writes, idempotency is satisfied by ADR-0002's tool-execution
  audit record, not a client `Idempotency-Key` header. The manual-CRUD REST surface
  (`ManualCrudEndpoints.cs`, see API surface below) has no idempotency-key mechanism either —
  it's a direct human form submission, not a retriable client integration. The Plaid webhook
  route (`/api/connectors/plaid/webhook`) that would need event-id dedup isn't built yet; when
  it is, see DECISIONS.md's *Open items* for the dedup mechanism to use.
- **Compliance posture** — GDPR-style per-tenant export and deletion are inherited from
  Plenipo's existing platform data-export/deletion procedures (the same ones every Plenipo
  product relies on); PII fields (`Account.InstitutionName`, `Account.MaskedAccountNumber`,
  `Transaction.Description`) are tagged `[Pii]` per the guardrail, flowing through the
  platform's existing export/audit machinery unchanged.

## Cloud topology

- **Provider**: Azure (`workflow.json.cloud`).
- **Compute**: Azure App Service (Linux) — the stack's documented default, and consistent
  with Plenipo's own dedicated-per-customer deployment target.
- **Data**: Azure Database for PostgreSQL — Flexible Server (matches Plenipo's Postgres-first
  default; two logical databases, `plenipo-platform` and `plenipo-audit`, same as every other
  Plenipo product).
- **Vector**: not provisioned for v1 — no `vector-db` capability was declared (SPEC's chat
  queries are structured aggregation over transactions/budgets, not semantic document search;
  adding pgvector infrastructure nothing in v1 needs would be exactly the anticipated-problem
  anti-pattern the plan-system guardrails warn against).
- **Secrets**: Azure Key Vault, via Plenipo's existing `ISecretVault` Key Vault provider —
  Plaid credentials (ADR-0006), the AI provider key, and SMTP credentials all flow through it
  write-only, unchanged from how every other Plenipo product handles secrets.
- **Identity**: Entra ID, via Plenipo's existing OIDC integration.
- **File storage**: Azure Blob Storage, via Plenipo's existing `IFileStore` Azure Blob
  provider — statement uploads and any generated exports.
- **OCR**: Azure Document Intelligence, consumed as the fallback leg of ADR-0004's hybrid
  statement-parsing approach, via Plenipo's existing OCR seam (the same one Casewell's
  `ocr_document` platform tool uses).
- **Networking**: no Networthy-specific networking decisions — deployment follows Plenipo's
  existing per-customer Terraform topology (App Service + managed identity + Key Vault +
  private Postgres/Redis endpoints), extended with this product's resource names, not
  redesigned.

## Data model (concrete)

Entities below extend PLAN.md's conceptual sketch with the concrete relationships and PII
tagging; migrations and exact EF Core column types are `/development:build-system`'s job.

- **Account** (`ITenantOwned`) — `Id`, `TenantId`, `Name`, `Type` (checking/savings/credit/
  cash), `InstitutionName` `[Pii]`, `MaskedAccountNumber` `[Pii]`, `CurrencyCode`,
  `CachedBalance`, `VisibilityScope` (household-wide | one member). One household (tenant) has
  many accounts.
- **Transaction** (`ITenantOwned`) — `Id`, `TenantId`, `AccountId` (FK → Account),
  `OccurredOn`, `Amount`, `CurrencyCode`, `Description` `[Pii]`, `CategoryId` (FK → Category,
  nullable until categorized), `Direction` (income | expense), `Source` (upload | plaid |
  manual), `ApprovalStatus`, `CreatedByUserId`. One account has many transactions.
- **Category** (`ITenantOwned`) — `Id`, `TenantId`, `Name`, `ParentCategoryId` (nullable,
  self-referencing for subcategories). Seeded starter taxonomy per tenant, curatable
  (`household-admin` only), the same pattern as Casewell's clause library.
- **Budget** (`ITenantOwned`) — `Id`, `TenantId`, `CategoryId` (FK → Category), `PeriodMonth`,
  `TargetAmount`, `CurrencyCode`. One category has many budgets (one per period).
- **StatementImportBatch** (`ITenantOwned`) — `Id`, `TenantId`, `SourceFileId` (FK → the
  platform `StoredFile`), `ParseStatus`, `ExtractedLineItemsJson` (pending review),
  `ReviewedByUserId`, `ReviewedAt`. Approved batches produce `Transaction` rows.
- **PlaidLinkedAccount** (`ITenantOwned`) — `Id`, `TenantId`, `AccountId` (FK → Account),
  `PlaidItemId` `[Pii]`, `PlaidAccessToken` (secret, write-only via `IConnectorSettings` per
  ADR-0006, not stored on this row directly), `LastSyncedAt`.

Every entity carries `TenantId` and is covered by Plenipo's global tenant query filter — one
household's data is structurally invisible to another's.

## API surface (concrete)

Reads are unversioned, at `/api/finance/...` — not `/api/v1/...` as originally planned; no
versioning need has arisen yet, so none was added. ADR-0002 held that **AI-initiated** writes
only happen through approval-gated chat tools, never a parallel REST CRUD path for the
assistant to script around — that part shipped exactly as designed. What ADR-0002 didn't
anticipate is a second, **RBAC-gated (not approval-gated) REST write surface for the human
acting directly** — the tab editors' manual-bookkeeping forms (`ManualCrudEndpoints.cs`). That
surface is real and intentional (its own doc comment: "AI-first ≠ chat-only... no AI in the
loop means no approval gate; RBAC is the gate") and doesn't weaken ADR-0002's guarantee, since
the approval gate exists to catch an AI acting on the household's behalf, not a human editing
their own books by hand. The concrete surface is:

- **Reads** (`GET`, RBAC-gated, `/api/finance/...`): `accounts`, `transactions`, `budgets`,
  `categories`, `debts`, `recurring`, `income-sources`, `goals`, `net-worth/history`, plus the
  v2 composed reads `overview` (ADR-0008), `spending`, `cashflow`, `bills/upcoming` — each
  backed by a module-declared tab data endpoint, the same pattern as Casewell's
  `/api/legal/matters`.
- **Manual writes** (`POST`/`DELETE`, RBAC-gated on `FinanceModule.ManageFinance`, no approval
  gate): the tab editors' direct-entry forms — accounts, transactions, budgets, goals, income
  sources, categories, exchange rates, household settings, and import-batch line
  corrections/approval (`ManualCrudEndpoints.cs`).
- **Chat/tool writes** (AG-UI + SignalR, not REST): every `ModuleTool` on `Networthy.Finance`
  — 32 today, pinned by `FinanceCatalogTests.Manifest_ToolList_IsPinned`; `FinanceModule.cs` is
  the authoritative list. Core v1 examples: `create_account`, `log_own_transaction` (the
  module's one ungated write, ADR-0005), `categorize_transaction`, `edit_transaction`,
  `set_budget`, `import_statement`, `review_import_batch`, `approve_import_batch`,
  `get_net_worth`, `can_i_afford`, `list_pending_approvals`, `get_activity_log`. The "Delivered
  since v1" wave (SPEC.md) added `set_goal`/`contribute_to_goal`/`list_goals`/`get_goal_plan`,
  `update_account_terms`/`get_financial_health`, `set_income_source`/`list_income_sources`,
  `list_recurring`, `get_household_settings`/`update_household_settings`, `set_exchange_rate`,
  `export_transactions`/`generate_monthly_report`/`export_activity_log`, `list_import_batches`.
  Member invites are **not** a chat tool — they go through the platform's existing Admin →
  Users surface (`HouseholdTools.cs`'s only tool is `set_account_visibility`). Approval-gated
  tools follow Plenipo's standing platform mechanism. `Networthy.Connectors.Plaid` adds three
  more once installed: `list_plaid_accounts`, `link_plaid_account`, `sync_plaid_transactions`.
- **No inbound Plaid webhook yet**: `POST /api/connectors/plaid/webhook` remains an open item
  (DECISIONS.md), not a built route — Plaid sync today is pull-only, via the scheduled-plus-
  on-demand `sync_plaid_transactions` tool. Confirm the signed-webhook-plus-event-id dedup
  pattern when the webhook is actually built.
- **Errors**: Problem Details (RFC 7807), inherited from Plenipo's existing error middleware.
- **Rate limiting**: per-tenant + per-endpoint, inherited from Plenipo's existing rate-limiter
  registration.

## MAF agents

- **Finance assistant** — the chat-first assistant SPEC.md names as a must-have. Purpose:
  answer spending/budget/affordability questions and execute every write tool listed above.
  Tools registered: all `Networthy.Finance` `ModuleTool`s, plus the platform's generic
  document tools (`read_document`, `generate_pdf`) for statement handling, plus
  `PlaidConnector`'s tools (link status, manual sync trigger) once installed. System prompt
  outline: household-finance framing, "never fabricate a balance or transaction — every
  numeric answer must come from a tool call," and explicit instruction to use
  `Transactions.LogOwn`'s ungated path only for the caller's own manual entries. Memory:
  Plenipo's existing per-tenant conversation persistence (Postgres), one conversation thread
  per user per household, unchanged from every other Plenipo product's chat.

No second agent is warranted for v1 — the finance domain is cohesive enough (per ADR-0001's
single-module decision) that one agent with the full tool surface, permission-filtered per
role, covers both personas without a handoff pattern.

## SPA architecture

- **Routing**: React Router, the same pattern `@plenipo/ui`'s `AppShell` already provides.
- **State**: TanStack Query for server state (mirrors every existing Plenipo product's tab
  data-fetching), no separate global client-state library needed for v1.
- **Components**: shadcn primitives + the shared `DataTable` (for the accounts/transactions/
  budgets/categories tabs) + the shared `Form` + the always-present slide-over chatbot panel;
  Networthy's tabs are declared the same way Casewell's are (`TabDescriptor` + `DataEndpoint`).
- **Custom entry** *(v2, ADR-0008)*: `frontend/networthy-ui` — a minimal Vite app rendering
  `<PlenipoApp moduleUi={[finance]} />`, registering the custom **Overview** dashboard tab
  (composed from `@plenipo/ui`'s `StatTile`/`ProgressBar` primitives over one
  `/api/finance/overview` aggregate fetch); every other tab still falls back to the
  server-driven `GenericTab`. Built and embedded by `scripts/build-ui.ps1` from the sibling
  Plenipo checkout; the committed bundle keeps clone-and-run true.
- **Feature folders**: one bounded context (Accounts, Transactions, Budgets, Household,
  Approvals) — each a tab in the module manifest, not a separate SPA route tree.
- **Branding**: `VITE_BRAND_NAME=Networthy` baked at build; runtime `Branding:ProductName`
  supersedes it, unchanged.

## Diagrams checked into the repo

- [`docs/diagrams/c1-context.mmd`](docs/diagrams/c1-context.mmd)
- [`docs/diagrams/c2-containers.mmd`](docs/diagrams/c2-containers.mmd)
- [`docs/diagrams/c3-components-host.mmd`](docs/diagrams/c3-components-host.mmd)
