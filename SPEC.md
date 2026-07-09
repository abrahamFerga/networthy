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

### Explicitly out of scope (v1)

- **Investment portfolio management / advisory** — regulated (RIA) territory; net worth *reflects*
  investment-account balances without the product giving investment advice.
- **Bill negotiation / subscription cancellation** — a third-party service-layer feature (someone
  makes calls on your behalf), not a platform capability.
- **Credit score monitoring** — requires a credit-bureau data relationship; a candidate connector
  later, not v1.
- **Tax prep / filing** — a distinct, heavily regulated product category.

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

## Success metrics

- **Week-1 activation** — % of new households that upload a statement or link an account within
  24 hours of signup.
- **Time-to-first-budget** — median time from signup to the first budget category configured.
- **AI suggestion trust** — % of AI-suggested categorizations/imports approved without edit.
- **Household adoption** — % of households that invite a second member within 7 days of creation.
- **Assistant engagement** — % of monthly active users who use the chat assistant (not just the
  dashboard) at least once per week.

## Open questions for plan-system

1. Scale target for v1 (households at hosted-tier launch) — affects the Plaid product tier
   (Transactions vs. Balance) and its cost model.
2. Which bank/card statement formats to prioritize for the parser's initial accuracy bar (e.g. the
   top N US banks by household share) before a format counts as "supported".
