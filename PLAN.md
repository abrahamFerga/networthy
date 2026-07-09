# Networthy — Plan

## Epics (in build order)

1. **Foundations** — Cortex platform install (`AddCortexPlatform`), a household as a Cortex
   tenant, RBAC scaffold (`household-admin`/`household-member`), dashboard shell, chat panel,
   connector registry, seeded starter category taxonomy. Guardrail-mandated; always epic 1; no
   direct SPEC capability row (Foundations delivers the cross-cutting platform, not a capability).

2. **Accounts & Transactions** — Capabilities (from SPEC): *Accounts*, *Transactions* (income
   folded in as a transaction direction, per SPEC), *Chat-first assistant* (primary owner — the
   first module tools ship here: list accounts, log a transaction, ask "how much do I have" /
   "how much did I spend on X"). Depends on: Foundations.

3. **Statement Import** — Capabilities: *Statement upload & parsing*. Upload → AI-assisted
   line-item extraction → human review → approved lines become Transactions (epic 2's entity).
   Depends on: Accounts & Transactions.

4. **Household Sharing** — Capabilities: *Household sharing*. Member invites, per-member
   visibility scoping on accounts/transactions/budgets. Depends on: Foundations (RBAC),
   Accounts & Transactions (the data being shared).

5. **Budgets** — Capabilities: *Budgets* (net worth trending already delivered in epic 2, as
   accounts roll up regardless of budgeting). Per-category monthly targets, spent-vs-budget,
   over-budget flags; extends the chat surface with budget tools ("set my grocery budget to
   $400"). Depends on: Accounts & Transactions, Household Sharing (budgets are a shared object).

6. **Bank-Linking Connector (Plaid)** — Capabilities: *Bank-linking connector (Plaid)*. An
   alternative, opt-in path into the same Transactions/Accounts epic 2 already delivers — never
   a replacement for upload. Depends on: Accounts & Transactions.

7. **Approval & Audit Trail Surfacing** *(differentiator — last, so it slips without blocking
   v1)* — Capabilities: *Approval-gated AI actions + audit trail*. The mechanism itself
   (approval gate, append-only audit log) is inherited free from the Cortex platform in
   Foundations; this epic is the household-facing surface — a "pending approvals" view and an
   "AI activity" log scoped to the household, so the differentiator is visible, not just true.
   Depends on: Foundations.

**The other two SPEC differentiators need no epic of their own** — *"Free, open source,
self-hostable, AI-native"* is a structural/licensing fact (MIT, this repo, Cortex underneath),
not a work item; *"Upload-first, bank-linking optional"* is satisfied automatically by epics 3
and 6 shipping as genuinely independent paths into the same Transactions entity — no epic
depends on the other for correctness.

## Module list

| Module (.NET project name) | Bounded context | Capabilities served | Skills used to build it |
|---|---|---|---|
| `Networthy.Host` | foundations | (cross-cutting: thin host on Cortex packages) | dotnet-aspire-base |
| `Networthy.AppHost` | foundations | (cross-cutting: local orchestration) | dotnet-aspire-base |
| `Networthy.Finance` | finance | Accounts, Transactions, Statement Import, Household Sharing, Budgets, Chat-first assistant, Approval & Audit Trail Surfacing | agent-framework-csharp (module tools), entity-framework-core |

One cohesive domain module (`Networthy.Finance`), organized internally by file the way
Casewell's `Casewell.Legal` is (`AccountTools.cs`, `TransactionTools.cs`,
`StatementImportTools.cs`, `BudgetTools.cs`, `HouseholdTools.cs`, plus their `Persistence/`
entities) rather than split into several small projects — the bounded contexts above are all
part of one tightly-coupled finance domain (a budget references transactions which reference
accounts which belong to a household), the same judgment call Casewell made for
matters/calendar/clauses/tasks/time.

The Plaid connector is **not** a Networthy *module* — it's a **connector**, a separate
`Networthy.Connectors.Plaid` project in this repo (ADR-0007, which superseded the original
plan of putting it in Cortex core). Plaid is specific to the financial domain — a legal or
healthcare vertical would never reach for it — so it stays out of Cortex core; Cortex only
provides the generic *ability* for a product to define its own connector (`IConnector` against
`Cortex.Connectors.Sdk`, registered with `AddCortexConnector<PlaidConnector>()` in
`Networthy.Host` exactly like a built-in). This keeps the platform clean of one product's
domain-specific integrations while `Networthy.Finance` stays scoped to pure domain logic.

## Data model sketch

Conceptual only — schemas are `/architecture:design-architecture`'s output.

- **Household** — not a new entity; a Cortex `Tenant` *is* the household. No separate table.
- **Household membership** — not a new entity; Cortex `User` + `UserRole`
  (`household-admin`/`household-member`) inside the household's tenant, using the platform's
  existing multi-tenancy primitives directly.
- **Account** — name, type (checking/savings/credit/cash), institution name (optional,
  **PII**), masked account number if stored (**PII**), currency code, cached current balance,
  visibility scope (household-wide vs. one member's private view), `TenantId`.
- **Transaction** — `AccountId`, date, amount, currency, merchant/description (**PII** —
  spending patterns can reveal medical, political, or other sensitive facts under GLBA's
  "nonpublic personal information"), `CategoryId`, direction (income/expense), source
  (upload/plaid/manual), approval status, `CreatedByUserId`, `TenantId`.
- **Category** — name, optional parent (subcategories), seeded starter taxonomy, tenant-
  curatable — same pattern as Casewell's clause library. `TenantId`.
- **Budget** — `CategoryId`, period (month), target amount, currency, `TenantId`.
- **StatementImportBatch** — the uploaded file, parse status, extracted line items pending
  review, driven by the platform's background-job primitive (same shape as Casewell's bulk
  document review job). `TenantId`.
- **PlaidLinkedAccount** — the binding between a Plaid item/account and a Networthy `Account`
  — mirrors how Casewell's `ConnectorBinding` ties a matter to a synced folder. `TenantId`.

Every entity is tenant-owned (`ITenantOwned`), enforced by Cortex's global query filter —
one household's data is structurally invisible to another's, same guarantee Casewell gives
per-matter.

## RBAC model (refined)

| Role | Policies | Notes |
|---|---|---|
| `household-admin` | `Accounts.Manage`, `Accounts.View`, `Transactions.View`, `Transactions.Approve`, `Categories.Manage`, `Budgets.Manage`, `Budgets.View`, `StatementImport.Upload`, `StatementImport.Approve`, `Connectors.ManagePlaid`, `Household.ManageMembers`, `Household.View` | Full household control; the only role that can invite/remove members or configure Plaid. |
| `household-member` | `Accounts.View` (scoped to shared visibility), `Transactions.View` (scoped), `Transactions.LogOwn`, `Budgets.View`, `Household.View` | Cannot manage accounts, connectors, categories, or budgets, or approve another member's pending AI actions. Whether `Transactions.LogOwn` is approval-gated or a Casewell-`log_time`-style quick-capture exception is an open question below. |

`system_admin` (platform-level) stays non-customizable per Cortex's standing rule; it is not a
household role.

## Integration surface

| Connector | Direction | Purpose | Webhook routes | Per-tenant config |
|---|---|---|---|---|
| Plaid | outbound (pull Link + Transactions API) and inbound (Plaid item-update webhooks) | Opt-in live account/transaction sync, alongside — never instead of — statement upload | `/api/connectors/plaid/webhook` | Household's linked Plaid item token(s) (secret, write-only); product-level `client_id`/`secret` (platform-managed or BYO — open question below) |

## Background work

| Job | Trigger | Cadence | Outbox required? |
|---|---|---|---|
| Statement parse & categorize | reactive (upload event) | on-demand | No — in-process via the platform job primitive, same as Casewell's bulk review job. |
| Plaid transaction sync | scheduled + webhook-triggered | every 6h, plus on webhook | Yes — an external API call with retry/failure semantics belongs in the outbox pattern. |
| Budget period rollover | scheduled | monthly (1st) | No — internal state transition only. |
| Net worth snapshot | scheduled | daily | No — internal aggregation for the trend view. |

## Open questions for design-architecture

1. Should a household member's own manually-logged transaction (`Transactions.LogOwn`) be
   approval-gated like every other household-affecting write, or an ungated quick-capture
   exception like Casewell's `log_time` (append-only, low-stakes, correctable by a follow-up
   entry)? This is a real product-feel decision, not just a technical one.
2. Plaid credentials: platform-managed (metered, like Cortex's existing AI-connection billing
   pattern) or household bring-your-own API keys — or must every household register its own
   Plaid developer account? Affects the hosted-tier cost model directly.
3. Statement parsing approach: LLM/vision-based extraction (flexible across any bank's PDF
   layout, the true AI-first bet) vs. a bank-specific template library (more accurate for
   supported banks, brittle elsewhere) vs. a hybrid (templates first, LLM fallback). This is
   the single highest-leverage architecture decision in the plan.
4. Scale target for v1 (households at hosted-tier launch) — carried over from SPEC's open
   questions; drives the Plaid product tier (Transactions vs. Balance) and infra sizing.
