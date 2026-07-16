# Networthy — Plan

## Epics (in build order)

1. **Foundations** — Plenipo platform install (`AddPlenipoPlatform`), a household as a Plenipo
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
   (approval gate, append-only audit log) is inherited free from the Plenipo platform in
   Foundations; this epic is the household-facing surface — a "pending approvals" view and an
   "AI activity" log scoped to the household, so the differentiator is visible, not just true.
   Depends on: Foundations.

**The other two SPEC differentiators need no epic of their own** — *"Free, open source,
self-hostable, AI-native"* is a structural/licensing fact (MIT, this repo, Plenipo underneath),
not a work item; *"Upload-first, bank-linking optional"* is satisfied automatically by epics 3
and 6 shipping as genuinely independent paths into the same Transactions entity — no epic
depends on the other for correctness.

**Epics 1–7 are shipped** (all 15 v1 issues closed) — plus a wave of work built directly on
`main` ahead of any backlog entry for it (Goals, Debts & financial health, Recurring detection &
bill reminders, household settings, reports & exports, the setup wizard, manual CRUD, FX
administration; see `SPEC.md`'s *Delivered since v1* table). No epic below re-touches
Foundations or re-derives anything already built; every v2 epic is additive.

## Epics (v2 — in build order, continuing from the shipped 1–7)

8. **Household Command Center** — Capabilities (from SPEC): *Home dashboard*, *Mobile-responsive
   navigation*. A new Overview tab (hero safe-to-spend number, net worth, budget snapshot,
   upcoming bills, recent activity, goal-progress cards) replacing "opens on Chat" as the landing
   experience; the flat 12-tab sidebar collapses to a mobile-friendly nav below a breakpoint.
   First of the v2 epics because epics 9–11 surface their own summaries *on* this dashboard.
   Depends on: Foundations, Accounts & Transactions, Budgets, Household Sharing, and the shipped
   Goals/Debts/Recurring work (the dashboard reads across nearly everything already built).

9. **Visualization & Budget-Progress Upgrades** — Capabilities: *Spending & cash-flow
   visualization*. Category-breakdown chart, an income-vs-expense cash-flow chart, per-category
   budget progress bars (dashboard + Budgets tab), and a calendar view of upcoming bills —
   extending the product's one existing chart (a net-worth line) into a real visualization set.
   Depends on: Household Command Center (epic 8, where most of this surfaces), Budgets, the
   shipped Recurring/bill-reminder work.

10. **Notifications & Smarter Approvals** — Capabilities: *Notification inbox*, *Risk-tiered AI
    approval UX*. A persistent, digest-batched inbox for bill reminders, over-budget flags,
    detected recurring charges, and pending AI approvals — distinct from transient toasts; the
    approval-gate UI moves from one uniform review form to risk-tiered handling (low-stakes AI
    writes get a one-tap confirm, consequential ones get a full diff-and-reasoning review card).
    This is the differentiator epic — approval-gated AI is already Networthy's most defensible
    claim; getting the UX right is what makes it a showcase instead of friction. Depends on:
    Household Command Center (epic 8, the inbox lives in the nav shell), Approval & Audit Trail
    Surfacing (already shipped), the shipped Recurring work.

11. **Cash-Flow Forecasting & Safe-to-Spend** — Capabilities: *Cash-flow forecasting*,
    *Conversational safe-to-spend*. Deterministic forward balance projection from recurring
    income/expenses, upcoming bills, and budget pace (no ML, no fabricated numbers — the same
    "every number comes from a tool call over real data" rule the chat assistant already
    follows); a chat tool that makes the dashboard's hero safe-to-spend number interrogable
    ("why is my number $340 today?", "can I afford X by October?") off the same computed figure
    the dashboard shows, not a second formula. Depends on: Household Command Center (epic 8, the
    hero number it explains), Budgets, the shipped Recurring/Goals work.

12. **Debt Payoff Strategy & Multi-Goal Funding** — Capabilities: *Debt payoff strategy &
    multi-goal funding*. An avalanche-vs-snowball payoff-order comparator on the existing Debts
    tab (time-to-payoff, interest saved per strategy); prioritizing and funding multiple Goals
    from shared surplus instead of independently. Extends two already-shipped capabilities;
    depends on: the shipped Debts and Goals work.

13. **Security Hardening: 2FA & Account Masking** — Capabilities: *Two-factor/passkey
    authentication*, *Masked balances by default*. A second factor at login; account numbers and
    balances masked with a reveal toggle everywhere they appear, not just the Accounts tab.
    Depends on: Foundations. **Flagged** in Open questions below — 2FA is plausibly a
    Plenipo-platform authentication concern (`AddPlenipoAuthentication`), not Networthy-specific;
    confirm the boundary before scoping this epic as a Networthy-repo-only change.

14. **Open Data & Power-User API** *(differentiator — last, so it slips without blocking the
    rest)* — Capabilities: *Power-user API & custom reports*. A scoped, personal-access-token-
    authenticated REST surface for self-hosters to build on; ad-hoc filter-and-save reports and a
    tax-category export mapping, extending the already-shipped Reports & exports capability.
    Depends on: Reports & Exports (shipped), Approval & Audit Trail Surfacing (shipped).
    **Flagged** — this is the system's first REST *write* surface, a deliberate exception to
    ADR-0002; see Open questions below.

**The two v2 differentiators need no epic of their own**, same pattern as v1 —
*"Regulatory-hedge positioning"* is a marketing/positioning claim about the architecture epics
2 and 6 already shipped, not a new work item; *"AI-decision transparency as ADMT-readiness"* is
delivered as a slice of epic 10 (extending the existing audit export into a disclosure surface),
not a standalone epic.

## Delivered since v2 plan (reconciliation, 2026-07-12)

This section was written 2026-07-11 framing epics 8–10 as gated on a future Plenipo platform
release (see Open question 1 below) and ADR-0008 framing `frontend/networthy-ui` as brand new.
Plenipo's alpha.17–19 releases landed within the same day, and epics 8, 9, and 10 shipped
immediately after — this reconciles that, the same way SPEC.md's "Delivered since v1" table
reconciles v1's own retroactive work. Status below is sourced from the GitHub issue tracker
(`gh issue list`), not from this document's own narrative framing, which is why it disagrees
with some of the "waiting on architecture" language elsewhere in this file.

| Epic | Status | Evidence |
|---|---|---|
| 8. Household Command Center | **Shipped** — features #43 (home dashboard) and #44 (mobile nav) closed; epic-tracking issue #42 left open as bookkeeping only. | `frontend/networthy-ui/src/finance/OverviewTab.tsx`, ADR-0008 |
| 9. Visualization & Budget-Progress Upgrades | **Shipped** — epic #45 and feature #46 both closed. | `SpendingTab.tsx`, `BudgetsTab.tsx`, `RecurringTab.tsx` (bill calendar), `CashFlowEndpoint.cs` |
| 10. Notifications & Smarter Approvals | **Shipped, and already extended** — features #48 (notification inbox) and #49 (risk-tiered approvals) closed; issue #71 (statement-upload reminders, closed 2026-07-12) is new off-backlog scope built directly on top of it — the same "shipped ahead of a backlog entry" pattern SPEC.md documents for v1. | `DailyDigestJobHandler.cs`, `StatementReminderJobHandler.cs`, `FinanceToolSource.cs`'s per-tool risk-tier declarations |
| 11. Cash-Flow Forecasting & Safe-to-Spend | **Partially started, not shipped** — epic #50 and features #51/#52 all still open. The dashboard's safe-to-spend figure already exists (`OverviewEndpoint.cs`'s `SafeToSpendMath`, explicitly commented for epic 11 reuse so the chat tool doesn't re-derive it later), but the forward cash-flow projection and the chat-tool interrogation of that number aren't built yet. | `OverviewEndpoint.cs` |
| 12. Debt Payoff Strategy & Multi-Goal Funding | **Not started** — epic #53 and feature #54 open. `HealthTools.cs` already orders debts avalanche-style inside the financial-health tool, but there's no payoff-comparator surface. | — |
| 13. Security Hardening: 2FA & Account Masking | **Not started** — epic #55 and features #56/#57 open. No 2FA/passkey or balance-masking code exists anywhere in the repo. | — |
| 14. Open Data & Power-User API | **Not started** — epic #58 and feature #59 open. No `ApiKey` entity or scoped REST write surface exists. | — |

**Off-backlog, not derived from any SPEC.md capability**: issue #66, "Language preference &
Spanish (es-MX) localization" (open as of 2026-07-12). Worth a SPEC.md addendum if and when it
ships, the same way the v1 "Delivered since v1" table retroactively documented similar
off-backlog work.

**ADR-0008 correction**: its Decision text says `frontend/networthy-ui` depends on the UI library
via a sibling-checkout `file:` dependency. That held for a few hours; commit `45ceb8b` (same day,
PR #44) replaced it with a **vendored tarball** (`file:../../.packages/cortex-ui-0.1.0-alpha.19.tgz`
— a pre-rename name, from before the platform became Plenipo). Both are now history: as of the
**2026-07-15 amendment** to ADR-0008, upstream publishes the library to the public npm registry and
no longer attaches a tarball to releases, so `frontend/networthy-ui` takes a plain registry
dependency (`"@plenipo/ui": "0.1.0-alpha.23"`) and needs no checkout and no vendored tarball. See
the 2026-07-15 amendment in DECISIONS.md for the full consequences.

## Module list

| Module (.NET project name) | Bounded context | Capabilities served | Skills used to build it |
|---|---|---|---|
| `Networthy.Host` | foundations | (cross-cutting: thin host on Plenipo packages) | dotnet-aspire-base |
| `Networthy.AppHost` | foundations | (cross-cutting: local orchestration) | dotnet-aspire-base |
| `Networthy.Finance` | finance | Accounts, Transactions, Statement Import, Household Sharing, Budgets, Chat-first assistant, Approval & Audit Trail Surfacing — plus everything SPEC.md's "Delivered since v1" table adds (Goals, Debts & financial health, Recurring detection & bill reminders, Household preferences, Multi-currency/FX admin, Reports & exports, manual CRUD) and the v2 dashboard/visualization reads (epics 8–9, shipped — see below) | agent-framework-csharp (module tools), entity-framework-core |

One cohesive domain module (`Networthy.Finance`), organized internally by file the way
Casewell's `Casewell.Legal` is — v1's six files (`AccountTools.cs`, `TransactionTools.cs`,
`StatementImportTools.cs`, `BudgetTools.cs`, `HouseholdTools.cs`, `ApprovalSurfaceTools.cs`)
plus their `Persistence/` entities, now joined by the "Delivered since v1" wave's
`GoalTools.cs`/`GoalPlanning.cs`, `HealthTools.cs`, `IncomeSourceTools.cs`, `RecurringTools.cs`,
`HouseholdSettingsTools.cs`, `ExportTools.cs`, `AffordabilityTools.cs`, three hosted services,
two more job handlers, and the v2 read endpoints (`OverviewEndpoint.cs`, `SpendingEndpoint.cs`,
`CashFlowEndpoint.cs`, `UpcomingBillsEndpoint.cs`) plus `ManualCrudEndpoints.cs` — rather than
split into several small projects. See ARCH.md's Solution layout section for the exhaustive,
kept-current file inventory; this list intentionally doesn't duplicate it. The bounded contexts
above are all part of one tightly-coupled finance domain (a budget references transactions
which reference accounts which belong to a household), the same judgment call Casewell made for
matters/calendar/clauses/tasks/time.

The Plaid connector is **not** a Networthy *module* — it's a **connector**, a separate
`Networthy.Connectors.Plaid` project in this repo (ADR-0007, which superseded the original
plan of putting it in Plenipo core). Plaid is specific to the financial domain — a legal or
healthcare vertical would never reach for it — so it stays out of Plenipo core; Plenipo only
provides the generic *ability* for a product to define its own connector (`IConnector` against
`Plenipo.Connectors.Sdk`, registered with `AddPlenipoConnector<PlaidConnector>()` in
`Networthy.Host` exactly like a built-in). This keeps the platform clean of one product's
domain-specific integrations while `Networthy.Finance` stays scoped to pure domain logic.

**v2 note**: epics 8–14's backend slices (forecasting math, the notification entity, API-key
issuance, payoff-comparator math) stay inside `Networthy.Finance` — none introduce a new bounded
context. The dashboard/visualization/mobile-nav/approval-UX work (epics 8–10) is primarily an
SPA concern and doesn't map to a `.NET` module at all; whether it also requires a change in the
separate Plenipo repo (new shared UI component kinds) is Open question 1 below — if so, that work
is tracked and built in that repo, not this one.

## Data model sketch

Conceptual only — schemas are `/architecture:design-architecture`'s output.

- **Household** — not a new entity; a Plenipo `Tenant` *is* the household. No separate table.
- **Household membership** — not a new entity; Plenipo `User` + `UserRole`
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

Every entity is tenant-owned (`ITenantOwned`), enforced by Plenipo's global query filter —
one household's data is structurally invisible to another's, same guarantee Casewell gives
per-matter.

**v2 additions:**

- **Notification** *(epic 10)* — `Kind` (bill-due | over-budget | recurring-detected |
  pending-approval), linked entity reference, `ReadAt` (nullable), `TenantId`, `UserId` when
  scoped to one member. Feeds the notification inbox and the digest job below.
- **ApiKey** *(epic 14)* — `Name`, hashed token value (the raw secret is shown once at creation,
  never stored — **PII/secret**), `Scopes`, `LastUsedAt`, `RevokedAt` (nullable),
  `CreatedByUserId`, `TenantId`. `household-admin`-only to issue or revoke.
- **Cash-flow forecast and safe-to-spend** *(epic 11)* — no new entity. Computed at read time
  from existing `Transaction`/`Budget`/`BillReminder` rows, per Open question 4 below — keeps the
  "every number comes from real data, never fabricated" rule intact without a cache-invalidation
  problem.
- **Debt payoff comparator** *(epic 12)* — no new entity; a computed view over the existing
  `Debt`/loan rows already in the shipped Debts capability.

## RBAC model (as built)

This table originally sketched a fine-grained action-noun policy taxonomy (`Accounts.Manage`,
`Transactions.Approve`, etc., see git history for the pre-build version).
`/development:build-system` implemented a coarser, tool-scoped model instead: a single
`tools.finance.*` wildcard for the admin role, a hand-enumerated allowlist of individual
`tools.finance.<name>` grants for the member role, and four module-level policy constants for
the tab/editor surfaces. Registered in `Networthy.Host/Program.cs`'s `AddPlenipoRole(...)` calls;
the four constants are defined in `FinanceModule.cs`.

| Role | Policies (as registered in `Program.cs`) | Notes |
|---|---|---|
| `household-admin` | `chat.use`, `chat.conversations.view`, `files.upload`, `files.read`, `tools.documents.read_document`, `tools.documents.list_documents`, `tools.finance.*` (every finance tool, read and write — writes stay approval-gated at the tool layer per ADR-0002/ADR-0005), `tools.connectors.plaid.*`, plus module policies `FinanceModule.ViewFinance` (`finance.view`), `FinanceModule.ManageCategories` (`finance.categories.manage`), `FinanceModule.ReviewImports` (`finance.imports.review`), `FinanceModule.ManageFinance` (`finance.manage` — the gate on the manual-CRUD REST endpoints too, see ARCH.md). | Full household control: the only role that can review/approve statement imports, manage Plaid, curate categories, or use the manual-CRUD forms. Member invites go through the platform's Admin → Users surface, not a finance policy. |
| `household-member` | `chat.use`, `chat.conversations.view`, `files.read`, plus an explicit per-tool allowlist: `tools.finance.list_accounts`, `get_net_worth`, `search_transactions`, `summarize_spending`, `log_own_transaction` (ADR-0005's ungated exception), `can_i_afford`, `get_budget_status`, `list_goals`, `contribute_to_goal`, `get_financial_health`, `list_income_sources`, `get_goal_plan`, `list_recurring`, `get_household_settings`, `list_import_batches`, `export_transactions`, `generate_monthly_report`, `export_activity_log`; plus `FinanceModule.ViewFinance`. | Cannot manage accounts/categories/budgets/settings/FX rates, review or approve imports, manage Plaid, or use the manual-CRUD endpoints (no `ManageFinance` grant). |

`system_admin` (platform-level) stays non-customizable per Plenipo's standing rule; it is not a
household role.

**v2 additions (`Notifications.View`, `Api.ManageKeys`, `Reports.ManageCustom`) have not been
built yet** — consistent with epics 13/14 below still being open on the GitHub board.

## Integration surface

| Connector | Direction | Purpose | Webhook routes | Per-tenant config |
|---|---|---|---|---|
| Plaid | outbound (pull Link + Transactions API) and inbound (Plaid item-update webhooks) | Opt-in live account/transaction sync, alongside — never instead of — statement upload | `/api/connectors/plaid/webhook` | Household's linked Plaid item token(s) (secret, write-only); product-level `client_id`/`secret` (platform-managed or BYO — open question below) |

Epic 14's power-user API is **not a connector** in this table's sense — it's a first-party,
inbound-only surface Networthy exposes *to* its own self-hosters, not an outbound integration
with a third party. It belongs in the API surface section of `ARCH.md`, not here.

## Background work

| Job | Trigger | Cadence | Outbox required? |
|---|---|---|---|
| Statement parse & categorize | reactive (upload event) | on-demand | No — in-process via the platform job primitive, same as Casewell's bulk review job. |
| Plaid transaction sync | scheduled + webhook-triggered | every 6h, plus on webhook | Yes — an external API call with retry/failure semantics belongs in the outbox pattern. |
| Budget period rollover | scheduled | monthly (1st) | No — internal state transition only. |
| Net worth snapshot | scheduled | daily | No — internal aggregation for the trend view. |
| Notification digest compilation *(v2, epic 10)* | scheduled | daily (in-app) / weekly (email digest, per Open question 5) | No — internal aggregation over already-persisted `Notification` rows. |

## Open questions for design-architecture

v1's four open questions were resolved during v1's build (see ADR-0004, ADR-0005, ADR-0006;
scale target is now moot post-launch) and don't recur here. New for v2:

1. ~~Do the dashboard and visualization must-haves (epics 8–9) require new generic component
   types in the shared Plenipo UI package, or bespoke product-owned SPA components?~~
   **Resolved (2026-07-11)** — answered in the Plenipo repo by verifying its actual frontend
   source, recorded as `docs/RICH_UI_KIT_PLAN.md` there. Split verdict: the
   **notification-inbox primitive already exists** in the platform (`NotificationBell` +
   `INotifier` + `/api/notifications` — epic 10's inbox just produces events into it), and the
   **bespoke dashboard needs no new seam** (`<PlenipoApp moduleUi>` already lets a product
   register custom React per tab — the Overview screen is Networthy-owned, composed from
   platform primitives). What *was* genuinely missing lands as Plenipo phases: donut/bar chart
   kinds, stat-tile + progress-bar primitives, data-table mobile card-mode, `[Pii]`
   masked-value rendering, and the risk-tiered approval queue. **Consequence for build order:**
   epics 8–10 wait on those Plenipo phases shipping in a vendored platform release; epics 11–12
   (forecasting, payoff math) are pure Networthy work and can proceed in parallel.
   **Update (2026-07-12): the wait is over** — those Plenipo phases shipped (alpha.17–19) and
   epics 8, 9, and 10 merged the same day; see "Delivered since v2 plan" above for the
   issue-by-issue status. This paragraph is left as-written for history.
2. ~~Is two-factor/passkey authentication (epic 13) a Plenipo-platform authentication capability
   or Networthy-specific?~~ **Resolved (2026-07-11)** — platform, per ADR-0001's precedent;
   confirmed no 2FA/WebAuthn/TOTP code exists anywhere in Plenipo today. Planned as the
   `AddPlenipoAuthentication` phase of Plenipo's `docs/RICH_UI_KIT_PLAN.md`; epic 13's
   Networthy-side scope shrinks to adopting the platform release plus the masked-balances UI.
3. Epic 14's API introduces the system's first REST *write* surface — a deliberate, scoped
   exception to ADR-0002 ("writes happen through chat-invoked, approval-gated MAF tools, not a
   REST CRUD API"). How does an API-originated write still land in the approval queue instead of
   bypassing it — does it enqueue the same pending-approval record a chat tool call would,
   authenticated via the new `ApiKey` instead of a user session?
4. Cash-flow forecasting (epic 11): confirm it's a deterministic projection from recurring
   transactions/bills/budget pace only (no ML, no probabilistic range) — consistent with the
   chat assistant's existing "never fabricate a balance or transaction" rule. Flag if a
   probabilistic range is wanted later; that would be a materially different (and riskier) bet.
5. Notification delivery (epic 10): does the digest also go out over the existing SMTP email
   channel (already used for budget recaps/reminders per `ARCH.md`), or stay in-app-only for v2?
