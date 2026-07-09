# Networthy — Architecture

## Context (C4 L1)

Three personas (household admin, household member, self-hosting power user) interact with
Networthy as a single system. External systems: Plaid (bank linking), the tenant's configured
AI provider (OpenAI/Azure OpenAI/Anthropic, via Cortex's existing pluggable per-tenant AI
connection), an SMTP relay (budget recaps and reminders, via Cortex's existing email
notification channel), and Entra ID (OIDC authentication in production).

Diagram: [`docs/diagrams/c1-context.mmd`](docs/diagrams/c1-context.mmd)

## Containers (C4 L2)

- **Domain UI** — the React SPA (`@cortex/ui` app shell, Networthy-branded via
  `VITE_BRAND_NAME`), served at `/` by `Networthy.Host` (`UseCortexDomainUi`) in production, or
  as a Vite dev server locally.
- **Admin console** — `@cortex/admin-ui`, served at `/admin` by the same host
  (`UseCortexAdminConsole`) — security/RBAC/usage/audit for platform admins, unchanged from
  every other Cortex product.
- **Networthy.Host** — the ASP.NET Core API: Cortex platform (auth, multi-tenancy, RBAC,
  audit, background jobs, connector registry) plus the `Networthy.Finance` module. Serves
  reads over REST, writes over AG-UI/SignalR chat (see ADR-0002).
- **Networthy.AppHost** — Aspire local orchestration (dev-only), the same shape as
  `Casewell.AppHost`.
- **Postgres** — `cortex-platform` (operational data) and `cortex-audit` (append-only audit
  log) databases.
- **Redis** — SignalR backplane, rate-limit windows; inherited from Cortex, not reconfigured.
- **Azure Blob Storage** — statement uploads and generated documents (pre-bill-style exports,
  if any), via the `file-storage` capability declared in `workflow.json`.
- **Plaid**, **Azure Document Intelligence** (scanned-statement OCR fallback), **SMTP relay** —
  external systems the host calls out to.

Diagram: [`docs/diagrams/c2-containers.mmd`](docs/diagrams/c2-containers.mmd)

## Components (C4 L3) — Networthy.Host

Inside `Networthy.Host`: the Cortex platform packages (auth/RBAC/multi-tenancy, the authorized
agent runner with its approval gate and audit log, the background job queue, the connector SDK,
and the domain-UI/admin-console static hosting) alongside `Networthy.Finance`'s tools —
`AccountTools`, `TransactionTools`, `StatementImportTools` (plus its job handler),
`BudgetTools`, `HouseholdTools`, and `ApprovalSurfaceTools`. `PlaidConnector` is drawn
separately: it lives in Cortex core (ADR-0003), not in `Networthy.Finance`.

Diagram: [`docs/diagrams/c3-components-host.mmd`](docs/diagrams/c3-components-host.mmd)

## Solution layout

Per ADR-0001, Networthy does not use the generic from-scratch layered template — it is a thin
host on Cortex platform packages, the same shape Casewell proved out:

```text
src/
  Networthy.AppHost/     ← Aspire local orchestration (dev-only)
  Networthy.Host/        ← thin ASP.NET Core host: AddCortexPlatform() +
                            AddCortexModule<FinanceModule>() +
                            AddCortexConnector<PlaidConnector>() +
                            AddCortexRole("household-member", [...])
  Networthy.Finance/      ← the one domain module (IModule + manifest), organized by file:
                              AccountTools.cs
                              TransactionTools.cs
                              StatementImportTools.cs (+ StatementParseJobHandler)
                              BudgetTools.cs
                              HouseholdTools.cs
                              ApprovalSurfaceTools.cs
                              Persistence/
                                FinanceDbContext.cs
                                Account.cs, Transaction.cs, Category.cs, Budget.cs,
                                StatementImportBatch.cs, PlaidLinkedAccount.cs
                                Migrations/

tests/
  Networthy.Finance.Tests/  ← unit tests + module-composition guard tests
                               (mirrors Casewell.Legal.Tests: manifest tool-list pinning,
                               approval-gating assertions, persona/role guard tests)
```

No `Domain`/`Application`/`Infrastructure`/`Api` split, and no `Networthy.Infrastructure.Azure`
project — cloud-specific behavior Networthy itself needs (none identified for v1; Blob storage
and Document Intelligence are consumed through Cortex's existing `IFileStore`/OCR seams, not
reimplemented) would live there if it ever arises.

## Cross-cutting wiring

- **AuthN** — inherited from Cortex (`AddCortexAuthentication`): OIDC/Entra ID in production,
  dev-auth headers locally. No Networthy-specific auth code.
- **RBAC** — `household-admin` and `household-member` roles, registered via
  `AddCortexRole(...)` in `Networthy.Host` (same call Casewell uses for `paralegal`). Policies
  are `tools.finance.*` permissions bound to each `ModuleTool.Permission`, exactly like every
  other Cortex module.
- **Multi-tenancy** — a household *is* a Cortex `Tenant`; every `Networthy.Finance` entity
  implements `ITenantOwned` and is covered by Cortex's global EF query filter. No new
  multi-tenancy code.
- **Observability** — inherited via `Cortex.ServiceDefaults` (OpenTelemetry traces/metrics/
  logs, health checks). No Networthy-specific wiring.
- **Audit** — every approval-gated tool write is captured by Cortex's existing append-only
  audit log automatically; `Transactions.LogOwn` (ADR-0005) is still logged as a tool
  invocation, just not held for approval first.
- **Resilience** — `PlaidConnector`'s outbound calls use the same `HttpClientFactory` +
  resilience-handler registration pattern as the S3/Documenso connectors (Cortex core, per
  ADR-0003) — no Networthy-specific Polly configuration.
- **Caching** — Redis, inherited from Cortex (SignalR backplane, rate limiting). No
  Networthy-specific cache usage identified for v1.
- **Background work** — the `StatementImportTools` parse job runs on Cortex's existing
  `IJobQueue`/`IJobHandler` primitive (the exact shape of Casewell's `BulkReviewJobHandler`).
  Budget-period rollover and the daily net-worth snapshot are lightweight `PeriodicTimer`
  hosted services registered by `Networthy.Finance` (the shape of Casewell's
  `DeadlineReminderService`). Plaid transaction sync is scheduled (every 6h) plus
  webhook-triggered, and — because it's an external API call with real failure modes — goes
  through the platform's job primitive rather than a bare timer, giving it retry/lease
  semantics for free.
- **Idempotency** — write idempotency is satisfied by ADR-0002's tool-execution audit record,
  not a client `Idempotency-Key` header (there is no REST write surface for one to attach to).
  The one true inbound HTTP write, the Plaid webhook, needs event-id dedup — see
  DECISIONS.md's *Open items* for the exact mechanism to confirm at build time.
- **Compliance posture** — GDPR-style per-tenant export and deletion are inherited from
  Cortex's existing platform data-export/deletion procedures (the same ones every Cortex
  product relies on); PII fields (`Account.InstitutionName`, `Account.MaskedAccountNumber`,
  `Transaction.Description`) are tagged `[Pii]` per the guardrail, flowing through the
  platform's existing export/audit machinery unchanged.

## Cloud topology

- **Provider**: Azure (`workflow.json.cloud`).
- **Compute**: Azure App Service (Linux) — the stack's documented default, and consistent
  with Cortex's own dedicated-per-customer deployment target.
- **Data**: Azure Database for PostgreSQL — Flexible Server (matches Cortex's Postgres-first
  default; two logical databases, `cortex-platform` and `cortex-audit`, same as every other
  Cortex product).
- **Vector**: not provisioned for v1 — no `vector-db` capability was declared (SPEC's chat
  queries are structured aggregation over transactions/budgets, not semantic document search;
  adding pgvector infrastructure nothing in v1 needs would be exactly the anticipated-problem
  anti-pattern the plan-system guardrails warn against).
- **Secrets**: Azure Key Vault, via Cortex's existing `ISecretVault` Key Vault provider —
  Plaid credentials (ADR-0006), the AI provider key, and SMTP credentials all flow through it
  write-only, unchanged from how every other Cortex product handles secrets.
- **Identity**: Entra ID, via Cortex's existing OIDC integration.
- **File storage**: Azure Blob Storage, via Cortex's existing `IFileStore` Azure Blob
  provider — statement uploads and any generated exports.
- **OCR**: Azure Document Intelligence, consumed as the fallback leg of ADR-0004's hybrid
  statement-parsing approach, via Cortex's existing OCR seam (the same one Casewell's
  `ocr_document` platform tool uses).
- **Networking**: no Networthy-specific networking decisions — deployment follows Cortex's
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

Every entity carries `TenantId` and is covered by Cortex's global tenant query filter — one
household's data is structurally invisible to another's.

## API surface (concrete)

Per ADR-0002, there is no REST CRUD write surface. The concrete surface is:

- **Reads** (`GET`, RBAC-gated, versioned `/api/v1/finance/...`): `accounts`, `transactions`,
  `budgets`, `categories` — each backed by a module-declared tab data endpoint, the same
  pattern as Casewell's `/api/legal/matters`.
- **Chat/tool writes** (AG-UI + SignalR, not REST): every `ModuleTool` on `Networthy.Finance`
  — `create_account`, `log_transaction` / `log_own_transaction` (ungated, ADR-0005),
  `categorize_transaction`, `set_budget`, `invite_household_member`, `upload_statement`,
  `approve_statement_batch`, `link_plaid_account`, `get_net_worth`, `can_i_afford`,
  `list_pending_approvals`, `get_ai_activity_log`. Approval-gated tools follow Cortex's
  standing platform mechanism.
- **One inbound webhook**: `POST /api/connectors/plaid/webhook` (Plaid item-update
  notifications) — the one true write-via-HTTP path in the system, per ADR-0002 and the open
  idempotency item in DECISIONS.md.
- **Errors**: Problem Details (RFC 7807), inherited from Cortex's existing error middleware.
- **Rate limiting**: per-tenant + per-endpoint, inherited from Cortex's existing rate-limiter
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
  Cortex's existing per-tenant conversation persistence (Postgres), one conversation thread
  per user per household, unchanged from every other Cortex product's chat.

No second agent is warranted for v1 — the finance domain is cohesive enough (per ADR-0001's
single-module decision) that one agent with the full tool surface, permission-filtered per
role, covers both personas without a handoff pattern.

## SPA architecture

- **Routing**: React Router, the same pattern `@cortex/ui`'s `AppShell` already provides.
- **State**: TanStack Query for server state (mirrors every existing Cortex product's tab
  data-fetching), no separate global client-state library needed for v1.
- **Components**: shadcn primitives + the shared `DataTable` (for the accounts/transactions/
  budgets/categories tabs) + the shared `Form` + the always-present slide-over chatbot panel —
  no new shared components required; Networthy's tabs are declared the same way Casewell's are
  (`TabDescriptor` + `DataEndpoint`).
- **Feature folders**: one bounded context (Accounts, Transactions, Budgets, Household,
  Approvals) — each a tab in the module manifest, not a separate SPA route tree.
- **Branding**: `VITE_BRAND_NAME=Networthy`, built via `@cortex/ui`'s app-shell target
  (`pnpm build:app`) and embedded into `Networthy.Host/wwwroot/app`, the identical mechanism
  Casewell uses.

## Diagrams checked into the repo

- [`docs/diagrams/c1-context.mmd`](docs/diagrams/c1-context.mmd)
- [`docs/diagrams/c2-containers.mmd`](docs/diagrams/c2-containers.mmd)
- [`docs/diagrams/c3-components-host.mmd`](docs/diagrams/c3-components-host.mmd)
