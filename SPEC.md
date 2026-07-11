# Networthy — Product specification

## In one sentence

Networthy is the free, open-source, AI-first personal finance assistant for households — upload
a statement or link an account, and it categorizes, budgets, and answers "can I afford this?" in
chat, with every AI-initiated change reviewable and audited before it sticks.

## Primary jobs to be done

- When I get a new bank or credit-card statement, I want to import it — by upload or a linked
  account — so my spending is captured without manual data entry.
- When I'm reviewing my month, I want to see spending by category against my budget so I know
  where I'm over or under.
- When money comes in, I want to record income so my budget math and what's-safe-to-spend numbers
  stay accurate.
- When I want to know "can I afford X", I want to ask the assistant directly instead of digging
  through a dashboard.
- When my household shares finances, I want shared visibility into budgets and net worth, with
  each member seeing only what they should.
- When I set a savings or spending goal, I want to track progress against it over time.
- When the AI categorizes a transaction or imports a statement, I want to review and approve the
  change before it's final, and see a record of what it did.

## Target personas

- **Household admin** — owns the household tenant: uploads statements, links accounts, sets
  budgets and categories, invites members, approves AI-suggested changes. Top 3 tasks:
  1. Upload a bank statement and review the AI-suggested categorization before it posts.
  2. Set next month's grocery budget and see it reflected against last month's actuals.
  3. Invite a household member and decide what they can see.
- **Household member** — shares the household's budgets and net worth; logs their own spending;
  chats with the assistant; does not manage accounts or connectors. Top 3 tasks:
  1. Ask the assistant "can I afford a $200 dinner this week?" and get a real answer.
  2. Log a cash purchase the household admin wouldn't otherwise see.
  3. Check this month's budget status for a category they care about.
- **Self-hosting power user** — runs their own instance, values full data ownership and the
  audit trail over convenience; in their own deployment, they *are* the household admin. Top 3
  tasks:
  1. Export the household's full transaction and budget history.
  2. Review the audit log of every AI-initiated write the assistant has made.
  3. Configure a second currency on an account held abroad.

## Capabilities

### Must have (v1)

| Capability | One-line description | Personas |
|---|---|---|
| Statement upload & parsing | PDF/CSV/OFX/QFX upload; AI-assisted line-item extraction, held for human review before transactions post. | Household admin |
| Bank-linking connector (Plaid) | Opt-in live account sync as an alternative to upload — never a requirement. | Household admin, Self-hosting power user |
| Accounts | Checking/savings/credit/cash accounts, multi-currency from the data model up; balances roll up into a trended net worth view. | Household admin, Self-hosting power user |
| Transactions | AI-suggested categorization (approval-gated), searchable, editable, with recurring/one-off income captured the same way and feeding the budget math. | Household admin, Household member |
| Budgets | Per-category monthly targets, spent-vs-budget tracking, over-budget flags. | Household admin, Household member |
| Household sharing | A household is a tenant; members are users with roles and per-member visibility. | Household admin, Household member |
| Chat-first assistant | The primary interface: ask spending questions, set a budget, get a direct "can I afford X" answer. | Household admin, Household member |

### Differentiators (v1)

| Capability | Why it matters | Personas |
|---|---|---|
| Free, open source, self-hostable, AI-native | The one intersection no competitor occupies — Firefly III/Actual Budget are open source but explicitly not AI; Monarch/Copilot/Cleo are AI-native but closed-source SaaS. | Household admin, Self-hosting power user |
| Approval-gated AI actions + audit trail | Every AI-initiated write is reviewable before it lands and auditable after — no competitor in the category does this. | Household admin, Household member |
| Upload-first, bank-linking optional | No mandatory hand-off of bank credentials to use the product — addresses the category's most common privacy objection. | Household admin, Self-hosting power user |

### Delivered since v1 (documented retroactively)

Built directly on `main` ahead of a backlog entry for any of it — real, shipped capabilities
that predate this reconciliation. No new epics needed for these; they're listed here so SPEC
stays the accurate source of truth `sync-backlog` and `design-architecture` traceability
depends on.

| Capability | One-line description | Personas |
|---|---|---|
| Savings & debt goals | Contribution-plan math, invested-goal growth projections, income-cadence-aware planning. | Household admin, Household member |
| Debt tracking & financial health score | Loans carry terms/APRs; a computed, configurable-threshold financial-health assessment. | Household admin, Household member |
| Recurring-charge detection & bill reminders | Auto-detects subscriptions/recurring charges; mutable reminders with a configurable lead time. | Household admin, Household member |
| Household preferences | Default currency, time zone, reminder lead time, financial-health thresholds — household-admin-tunable. | Household admin |
| Multi-currency exchange-rate administration | Platform-admin console page for managing the FX rates multi-currency net worth math depends on. | Self-hosting power user (as admin) |
| Reports & exports | CSV transaction export, a monthly PDF report, and an AI-activity/audit log download. | Household admin, Self-hosting power user |
| Guided onboarding wizard | First-run flow: household basics, accounts, income, statement upload, loans, first budget. | Household admin |
| Manual CRUD (non-chat) | Every chat-tool write also has a form-based UI equivalent — AI-first, not AI-only. | Household admin, Household member |
| One-command self-hosting + hosted tier | Docker Compose distribution (GHCR image, multi-arch) alongside a small-fee hosted lane. | Self-hosting power user |

### Must have (v2)

Sourced from `research/personal-finance.md`'s 2026 refresh — see that file for full competitive
citations. Ordered as the persona jobs-to-be-done they serve, not build order (that's
`PLAN.md`'s job).

| Capability | One-line description | Personas |
|---|---|---|
| Home dashboard | A dedicated overview screen — hero safe-to-spend number, net worth, budget snapshot, upcoming bills, recent activity, goal progress — replacing "opens on Chat" as the landing experience. | Household admin, Household member |
| Mobile-responsive navigation | Flat 12-tab sidebar collapses to a ~5-item bottom nav + overflow below a breakpoint; tables fall back to a card layout. | Household admin, Household member, Self-hosting power user |
| Spending & cash-flow visualization | Category-breakdown chart, income-vs-expense cash-flow chart, per-category budget progress bars, a calendar view of upcoming bills — extending the single net-worth line chart the product has today. | Household admin, Household member |
| Notification inbox | A persistent, digest-batched surface for bill reminders, over-budget flags, detected recurring charges, and pending AI approvals — distinct from transient toasts. | Household admin, Household member |
| Risk-tiered AI approval UX | Low-stakes AI writes (e.g. categorizing a small, high-confidence transaction) get a one-tap confirm; consequential ones (new recurring bill, a budget or debt change) get the full review card with a diff and the AI's reasoning. | Household admin, Household member |
| Cash-flow forecasting | Forward balance projection from recurring income/expenses, upcoming bills, and budget pace — deterministic, never fabricated, per the chat assistant's existing "every number comes from a tool call" rule. | Household admin, Household member |
| Conversational safe-to-spend | The dashboard's hero number is interrogable in chat ("why is my number $340 today?", "can I afford X by October?") off the same computed figure the dashboard shows. | Household admin, Household member |
| Debt payoff strategy & multi-goal funding | Avalanche-vs-snowball payoff comparator on the existing Debts tab; prioritizing/funding multiple Goals from shared surplus instead of independently. | Household admin, Household member |
| Two-factor / passkey authentication | A second factor at login, for an app holding full transaction and statement history. | Household admin, Household member, Self-hosting power user |
| Masked balances by default | Account numbers and balances masked with a reveal toggle everywhere they appear, not just the Accounts tab. | Household admin, Household member |
| Power-user API & custom reports | A scoped, personal-access-token-authenticated API surface for self-hosters to build on, plus ad-hoc filter-and-save reports and a tax-category export mapping, extending the existing Reports & exports capability. | Self-hosting power user |

### Differentiators (v2)

| Capability | Why it matters | Personas |
|---|---|---|
| Conversational, explainable safe-to-spend | Every competitor's "safe to spend" figure is a static black-box formula; Networthy's is interrogable through the same assistant that already explains every other number. | Household admin, Household member |
| AI-decision transparency as ADMT-readiness | California's 2026 automated-decision-making disclosure rules target exactly the kind of AI-suggested categorization/budgeting Networthy does — the existing approval-gate and audit trail are already ahead of the compliance bar; extending the audit export into an explicit disclosure/opt-out surface turns the obligation into a differentiator competitors will have to catch up to. | Household admin, Self-hosting power user |
| Regulatory-hedge positioning | Upload-first, Plaid-optional architecture is structurally insulated from the CFPB's stalled Section 1033 open-banking rulemaking and banks' 2026 tightening of API access — a claim no Plaid-dependent competitor can credibly make. | Household admin, Self-hosting power user |

### Explicitly out of scope (v1, reaffirmed for v2)

Each reconsidered against the 2026 competitive field in `research/personal-finance.md`; no new
reason found to reopen any of them.

- **Investment portfolio management / advisory** — regulated (RIA) territory; net worth *reflects*
  investment-account balances without the product giving investment advice.
- **Bill negotiation / subscription cancellation** — a third-party service-layer feature (someone
  makes calls on your behalf), not a platform capability.
- **Credit score monitoring** — requires a credit-bureau data relationship; a candidate connector
  later, not v2.
- **Tax prep / filing** — a distinct, heavily regulated product category.
- **Envelope / zero-based budgeting mode** *(new, v2)* — a second budgeting philosophy that
  conflicts with the shipped rollover-target model; a fork of the budgeting feature, not an
  increment to it.
- **Gamification / financial-literacy content** *(new, v2)* — this product's personas are
  self-hosters and households wanting AI leverage over their own data, not financial-literacy
  learners; not this product's identity.
- **Family sub-accounts with prepaid debit cards** *(new, v2)* — requires a card-issuing BaaS
  partner relationship, out of reach for an open-source/self-hosted product.
- **Cash-advance lending** *(new, v2)* — requires state lending licenses; structurally
  incompatible with self-hosting.

## RBAC model (initial)

- **household-admin** — manages accounts, connectors (Plaid), budgets, and categories; invites and
  removes members; approves AI-suggested writes (categorization, statement import, budget
  changes). Full read access within the household.
- **household-member** — reads shared budgets, accounts, and net worth per the admin's visibility
  settings; logs their own transactions; chats with the assistant. Cannot manage connectors,
  remove members, or approve another member's pending AI actions.

Both roles bind to capabilities, not UI screens — a member with a connector-scoped grant could
still not touch billing, for instance, if that's ever split out. `system_admin` (platform-level,
not household-level) remains non-customizable, per Cortex's standing rule.

## Regulatory constraints

- **GLBA (Gramm-Leach-Bliley Act) Safeguards Rule** — requires a written information-security
  program for handling nonpublic personal financial information. Whether Networthy is a "financial
  institution" under GLBA depends on the exact activities offered (aggregation can trigger it) —
  flag for real legal review before commercial launch; treat the Safeguards Rule's technical
  controls (access controls, encryption, audit logging) as a floor regardless.
- **State privacy law (CCPA and peers)** — GLBA does not blanket-exempt a company from state privacy
  law; CCPA's exemption is data-type-specific. A "we never sell data" posture is the intended
  default and sidesteps most sell/share triggers; the 2026 CPPA rules add cybersecurity-audit and
  automated-decision-making disclosure obligations that only bite at data-broker/seller scale.
- **PCI-DSS** — out of scope by default; the product reads and categorizes statement data, it does
  not process card payments. Revisit if a future feature (bill pay, card issuing) changes that.
- **Plaid's compliance posture** (SOC 2, GLBA-aligned data agreements) belongs to the bank-linking
  connector, not the platform core — same boundary as Casewell's Documenso/S3 connectors.
- **CFPB Section 1033 ("open banking" rule)** *(v2 update)* — finalized in 2024, enjoined by a
  federal court, and back in ANPRM rulemaking as of mid-2026 with no binding compliance date.
  Doesn't create a new obligation for Networthy; the upload-first/Plaid-optional architecture is
  already insulated from however it resolves, and that insulation is now claimed as a v2
  differentiator (see above).
- **California CPPA automated-decision-making (ADMT) rules** *(v2 update)*, effective 2026,
  add disclosure/opt-out obligations for automated decisions — AI-suggested categorization and
  AI-set budgets plausibly qualify. Flag for real legal review before commercial launch, same as
  GLBA above; the existing approval-gate + audit trail are a head start, not full compliance —
  epic 10's AI-decision-transparency work should be scoped with this in mind.

## Success metrics

- **Week-1 activation** — % of new households that upload a statement or link an account within
  24 hours of signup.
- **Time-to-first-budget** — median time from signup to the first budget category configured.
- **AI suggestion trust** — % of AI-suggested categorizations/imports approved without edit.
- **Household adoption** — % of households that invite a second member within 7 days of creation.
- **Assistant engagement** — % of monthly active users who use the chat assistant (not just the
  dashboard) at least once per week.

## Open questions for plan-system

v1's open questions (scale target, statement-format priority) were resolved during v1's
architecture/build and don't recur here. New for v2:

1. Do the dashboard and visualization must-haves require new generic component types in the
   shared Cortex UI package (a card-grid tab kind, a proportional/categorical chart kind, a
   budget-progress-bar primitive, mobile card-mode for the data table, a notification-inbox
   primitive) — meaning a companion change in the Cortex repo — or should Networthy build these
   as bespoke, product-owned SPA components? This is the single highest-leverage open question
   for the whole v2 round; carried into `PLAN.md` for `design-architecture` to resolve.
2. Is two-factor/passkey authentication a Cortex-platform authentication capability (benefiting
   every product built on the platform) or Networthy-specific? ADR-0001's precedent (generic
   capabilities live in the platform) leans platform.
3. The power-user API introduces the first REST *write* surface in the system — a deliberate
   exception to the chat-only-writes model. How does an API-originated write still land in the
   approval queue instead of bypassing it?
