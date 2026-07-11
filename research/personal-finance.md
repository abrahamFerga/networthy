# Industry research: personal-finance

## Top commercial players

1. **Monarch Money** — Category leader, best for couples/households. Two-tier pricing (Core
   $99.99/yr, Plus $199/yr with Morningstar investment analysis). Net worth tracking, investment
   dashboard, shared goals, an AI assistant layered on in 2026, weekly spending recaps.
2. **YNAB (You Need A Budget)** — Zero-based/envelope budgeting purist. $109/yr. Methodology over
   automation: every dollar gets a job, budgeted forward not reviewed backward.
3. **Copilot Money** — Apple-only (iOS/macOS, no web, no Android), $95/yr. Best-reviewed AI
   categorization depth and visual polish; the design bar to clear.
4. **Cleo** — The chatbot-coach model in its purest form: link a bank account, an AI persona
   walks you through spending, sends bill reminders, answers chat questions. Known for "roast
   mode" — sarcastic feedback on spending. Proof that a chat-first UX resonates in this category.
5. **Rocket Money** — Subscription-cancellation angle as the wedge feature, budgeting is
   secondary.
6. **Empower (Personal Capital)** — Free tier; investment/net-worth tracking funds a wealth-
   management upsell (their real business).
7. **PocketGuard** — "In My Pocket": one number, what's safe to spend after bills and goals.
8. **Tiller** — Auto-pulls transactions into a Google Sheet/Excel; the bridge for spreadsheet
   loyalists who hated manual entry but want to own their data.
9. **Simplifi (Quicken)** — Rules-based categorization, envelope budgets update from linked
   transactions.
10. **Origin** — Budgeting fused with investing and high-yield cash — the "one system" bet.
11. **Spend & Invest** — The closest positioning match to what was asked for: AI-powered PDF
    bank-statement upload, explicitly **no bank login required**. Proves the upload-not-link
    model has real demand, not just a workaround.

### Open-source / self-hosted (the differentiation test)

12. **Firefly III** — AGPL, self-hosted, double-entry bookkeeping, powerful rule-based
    auto-categorization. Explicitly **not** AI: "rather than using AI, Firefly III uses
    user-defined rules... no black-box machine learning, no third-party access to your
    transaction data" — a deliberate design stance, not a gap they're closing.
13. **Actual Budget** — MIT-licensed, local-first, YNAB-style envelope budgeting, fast offline
    sync, polished UI. Also rules-based, no AI categorization or chat interface.

**The gap, confirmed the same way it was for Casewell**: no open-source competitor is AI-native
(Firefly III and Actual Budget both treat "no AI" as a feature, not a limitation), and no
AI-native competitor is open-source or self-hostable. Every AI-first player (Monarch, Copilot,
Cleo, Origin) is closed-source SaaS with mandatory bank-credential linking. That intersection is
open.

## Capability matrix

| Capability | Monarch | YNAB | Copilot | Cleo | Rocket | Tiller | Firefly III | Actual |
|---|---|---|---|---|---|---|---|---|
| Bank-account linking (Plaid) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | optional | optional |
| Statement/PDF upload (no bank login) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| AI transaction categorization | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ (rules) | ❌ (rules) | ❌ (rules) |
| Conversational / chat interface | partial | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Budgets (envelope or category) | ✅ | ✅ (purist) | ✅ | partial | partial | ✅ | ✅ | ✅ (purist) |
| Net worth tracking | ✅ | ❌ | ✅ | ❌ | ❌ | ✅ | ✅ | partial |
| Multi-account / household sharing | ✅ | ✅ | ❌ | ❌ | ❌ | ✅ | ✅ | ✅ |
| Self-hostable / data stays yours | ❌ | ❌ | ❌ | ❌ | ❌ | partial (your sheet) | ✅ | ✅ |
| Open source | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ AGPL | ✅ MIT |
| Approval/audit trail on AI actions | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

The last row is the whole point: **nobody in this market gates AI actions behind human approval
or keeps an audit trail of what the AI did to your financial data.** That's not a feature gap in
their roadmaps — it's a category nobody in personal finance is competing in, because it's a
Cortex platform primitive (RBAC before the model, approval gates on every write, append-only
audit) that a from-scratch fintech simply doesn't have for free.

## Synthesized capabilities

### Must-have (v1)

- **Bank-statement upload & parsing** (PDF/CSV/OFX/QFX) — the user's explicit ask, and the
  underserved model per Spend & Invest's traction: no bank credentials handed to a third party.
  AI-assisted extraction (line items, dates, amounts, merchant) with human review before
  transactions post — mirrors Casewell's "AI drafts, human approves" pattern exactly.
- **Accounts** — checking/savings/credit card/cash, manually created, balances derived from
  posted transactions.
- **Transactions** — categorized (AI-suggested, user/approval-confirmed), searchable, editable.
- **Categories** — a starter taxonomy (seeded per tenant, curatable — same pattern as Casewell's
  clause library), user-extensible.
- **Budgets** — per-category monthly targets, spent-vs-budget tracking, over-budget flags.
- **Income tracking** — recurring and one-off income sources, feeds the budget math.
- **Net worth** — assets minus liabilities, trended over time from account balances.
- **Chat-first interaction** — "How much did I spend on dining last month?", "Set my grocery
  budget to $400", "Upload this statement" — the assistant as the primary interface, matching
  Cleo's proof-of-concept but with an approval gate Cleo doesn't have.
- **Multi-currency-ready data model** — US-first UX, but amounts, accounts, and budgets carry a
  currency from day one (cheap now, expensive to retrofit) — Firefly III and Tiller both treat
  multi-currency as table stakes; skipping it at the data-model level would be a regression vs.
  open source.
- **Bank-linking connector (Plaid)** — ships alongside upload in v1, not as a fast-follow: an
  `IConnector` following the exact shape of Casewell's Google Drive/S3 connectors (OAuth-ish
  service auth, pull external data into platform records). Upload stays first-class and
  privacy-preserving; Plaid is the opt-in path for users who want live sync. Neither is
  mandatory — that optionality is itself the differentiator against every competitor above.
- **Household / shared-tenant budgeting** — multiple users sharing one household's accounts,
  budgets, and net worth, with per-user visibility controls — ships in v1 given how close to
  universal this is outside the solo/Apple-only tools (Monarch, YNAB, Firefly III, Actual all
  have it). Maps onto Cortex's existing multi-tenant + RBAC primitives directly: a household is
  a tenant, members are users with roles.

### Differentiator (v1)

- **Free, open source, self-hostable, AI-native** — the exact intersection nothing else
  occupies. Same story Casewell tells in legal.
- **Approval-gated AI + audit trail** — every AI-initiated write (categorize, create budget,
  import statement) is auditable and, for anything that changes the record, human-approved
  before it lands. No other player in the matrix does this.
- **No mandatory bank-credential linking** — upload-first, Plaid-optional (a connector, not a
  requirement) — addresses the single most common privacy objection to this category.

### Skip for v1

- **Investment portfolio management / advisory** — Origin's and Empower's real business (and a
  regulated one — RIA licensing territory). Net worth can *reflect* investment account balances
  without the product giving investment advice.
- **Bill negotiation / subscription cancellation** — Rocket Money's wedge; a service-layer
  feature (third-party negotiation calls), not a platform capability.
- **Credit score monitoring** — requires a credit-bureau data relationship; a connector for
  later, not v1 scope.
- **Tax prep / filing** — a distinct, heavily regulated product category.

## Notable UX patterns observed

- **The "one number" pattern** (PocketGuard's "In My Pocket") — users want a single answer to
  "can I spend this?", not a dashboard to interpret. Worth a chat-first equivalent: an assistant
  that answers "can I afford X?" directly.
- **Weekly/monthly recap pushes** (Monarch) — proactive summaries beat requiring the user to
  open the app; maps directly onto Cortex's existing notification-channel seam.
- **Household sharing** (Monarch, YNAB, Firefly III, Actual) is near-universal outside the
  Apple-only/solo tools — multi-tenant-with-shared-household is a real requirement, not a
  nice-to-have.
- **Roast mode / personality** (Cleo) — a tone setting, not a core mechanic; worth a lighter
  version (encouraging vs. neutral vs. blunt) but not the product's identity.

## Compliance / regulatory considerations

- **GLBA (Gramm-Leach-Bliley Act)** — governs how financial institutions handle nonpublic
  personal financial information; the Safeguards Rule requires a written information-security
  program. Whether Networthy counts as a "financial institution" under GLBA depends on exact
  activities (aggregation/advice can trigger it) — flag for a real legal review before
  commercial launch, same caveat as Casewell's "not legal advice" framing.
- **State privacy law (CCPA and peers)** — GLBA doesn't blanket-exempt a company from state
  privacy law; CCPA's exemption is data-type-specific, not entity-wide. A "we never sell data"
  posture (natural given the open-source/self-host story) sidesteps most of the sell/share
  triggers that create the heaviest compliance burden (2026 CPPA rules add cybersecurity audits
  and automated-decision-making disclosures for larger data brokers/sellers specifically).
- **No PCI-DSS scope by default** — the product reads bank statements and categorizes spending;
  it does not process card payments. Stays out of PCI scope unless a future feature (bill pay,
  card issuing) changes that.
- **Plaid / bank-aggregation connector (if built)** inherits Plaid's own compliance posture
  (SOC 2, GLBA-aligned data agreements) — the connector, not the platform, carries that
  relationship, same shape as Casewell's Documenso/S3 connectors.

## Open questions for the user

None outstanding — see Answers below.

## Answers (committed defaults for v1)

1. **Market/geography**: US-first. The data model is multi-currency-ready from day one (every
   amount, account, and budget carries a currency code) even though v1 UX assumes USD — cheap to
   build in now, expensive to retrofit later.
2. **Bank linking**: Plaid ships in v1, alongside statement upload — not gated behind it. Upload
   stays the privacy-preserving default; Plaid is opt-in for users who want live sync.
3. **Household budgeting**: ships in v1. A household is a Cortex tenant; members are users with
   roles (e.g. an "adult"/"dependent" split is plausible, refined at Spec time). Solo use is the
   one-person-household case, not a separate mode.

These three decisions widen v1 beyond the initial draft (Plaid and household sharing move from
"fast-follow" to "must-have") — reflected in the Must-have (v1) list above.

---

## v2 refresh (2026-07-11)

v1's entire backlog (epics 1–7, all 15 issues) shipped, plus a wave of product work built
directly against `main` ahead of any backlog entry for it: Goals (contribution math, invested-
goal growth, income-cadence planning), Debt tracking + a computed financial-health score,
recurring-charge detection + bill reminders, a guided first-run setup wizard, manual CRUD in
the UI (AI-first, not AI-only), household settings (currency/timezone/reminder lead,
configurable health thresholds), reports & exports (CSV, monthly PDF, audit download),
one-command self-hosting distribution + a small-fee hosted tier, and an admin console page for
exchange-rate management. See `SPEC.md`'s new *Delivered since v1* table for the reconciled
capability list — this research refresh assumes all of the above already exists and does not
re-recommend any of it.

### What's still genuinely missing, checked against the 2026 field

Re-surveyed Monarch, YNAB, Copilot Money, Cleo, Rocket Money, Empower, PocketGuard, Tiller,
Simplifi, Origin, Firefly III, and Actual Budget, plus newer 2025–2026 entrants (OpenAI's
ChatGPT Finances, Ray Finance, Maybe Finance → the community-run Sure fork).

| Gap | Who does it | Why it matters here |
|---|---|---|
| No home/overview dashboard — the app opens on Chat, every other screen is a generic table or a single line chart | Copilot ("Free to Spend" hero), Monarch (customizable widget grid), PocketGuard ("In My Pocket") | The single biggest structural product gap. Every competitor's home screen is a card-grid summary, not a list. |
| No "safe to spend today" number | PocketGuard, Rocket Money Payday View | A proven activation hook; Networthy's chat assistant could make it *conversational and explainable* ("why is my number $340 today?") — nobody else does that. |
| No cash-flow / forward-balance forecasting | Monarch Forecasting, Simplifi Projected Cash Flows, PocketSmith | Natural extension of budgets + recurring detection, already-shipped data. |
| No debt payoff strategy comparator (avalanche vs. snowball) or cross-goal funding prioritization | Monarch Goals 3.0 | Extends the already-shipped Debts and Goals tabs rather than adding new domain concepts. |
| No notification/alert surface — bill reminders and over-budget flags exist as data but nothing surfaces them proactively; reports are pull (download), not push | Monarch weekly recap, PocketGuard/Simplifi watchlists & pace alerts | Backend data already exists (BillReminderService, budget over-flags, recurring detection); this is a presentation gap, not a new data model. |
| No risk-tiering on the AI approval queue — every AI write gets the same review UI regardless of stakes | General human-in-the-loop UX research (the "rubber-stamp problem": uniform approval friction trains users to stop reading) | Directly touches the product's core differentiator — approval-gated AI. Getting this wrong undermines the differentiator instead of showcasing it. |
| Visualization is a single line chart (net worth) — no category breakdown, no cash-flow chart, no budget progress bars, no bill calendar | Copilot, Monarch, PocketGuard, Rocket Money, CalendarBudget | The shared `Chart` component (X/Y/series line only) needs a proportional/categorical mode and Budgets needs a progress-bar idiom, not just table rows. |
| No mobile-responsive navigation — 12 flat tabs, generic DataTable, no card-mode below a breakpoint | Universal across every competitor (finance apps are checked from a phone) | A self-hosted product competing with app-store incumbents cannot skip this. |
| No 2FA/passkey | Increasingly table-stakes industry-wide by 2026 | Trust gap for an app holding full transaction/statement history. |
| No public API / developer surface | Firefly III (full REST API; its refusal to build native AI spawned a third-party MCP server so users could get AI anyway) | Networthy's approval-gated *native* AI is the direct rebuttal to Firefly's "AI is unreliable" stance — an API extends that story to power users/self-hosters instead of ceding it. |
| No masked-by-default account numbers/balances | Standard banking-app trust pattern by 2026 | Cheap, high-trust-signal fix. |

**Deliberately not re-recommending** (each already ruled out in SPEC.md's *Explicitly out of
scope*, and the 2026 survey found no new reason to reopen any of them): investment
portfolio/advisory depth (still RIA-licensed territory — net worth continues to *reflect*
balances without advising), bill-negotiation/subscription-cancellation concierge (needs a BPO
call-center backend, not software), credit score monitoring (needs a credit-bureau data
relationship — stays a candidate connector, not a build item), tax prep/filing (its own
regulated category). Newly considered and also rejected: envelope/zero-based budgeting as a
second budget philosophy (conflicts with the shipped rollover-budget model — a fork, not an
increment), gamification/financial-literacy content (wrong audience — this product's users are
self-hosters and households wanting AI leverage, not financial-literacy learners), family
sub-accounts with prepaid debit cards (needs a card-issuing BaaS partner, out of an
open-source/self-hosted product's reach), cash-advance lending (Cleo's model — requires state
lending licenses, structurally incompatible with self-hosting).

### Notable 2026 market shifts

- **OpenAI shipped ChatGPT Finances (May 2026)**: Plaid-linked, 12,000 institutions, dashboard +
  chat, but **read/insight-only — it cannot make changes to accounts.** This validates the
  chat-first bet Networthy already made and raises the bar on execution, but it does not erode
  Networthy's actual differentiator: ChatGPT Finances has no approval-gated *write* capability at
  all, because it doesn't write. Networthy's "AI drafts, human approves, everything's audited"
  story is untouched.
- **Ray Finance** (open-source, local-first, strips PII before any LLM call) is the closest
  ideological neighbor — but it's single-user, advice/read-only, no household RBAC, no statement
  OCR, no approval gate. Worth watching, not treating as a solved threat.
- **Maybe Finance shut down commercially in 2025**, open-sourced under AGPLv3; a community fork
  ("Sure") continues it. Reads as validation that self-host-only isn't enough of a business model
  without a support/hosted-tier lane — which Networthy already has.
- **CFPB Section 1033 ("open banking" rule)** was enjoined by a federal court and is back in
  ANPRM rulemaking as of mid-2026, with banks increasingly tightening or monetizing account-data
  API access in the meantime. Networthy's upload-first, Plaid-optional architecture is
  structurally insulated from this — worth stating as explicit positioning, not just a technical
  footnote.
- **California's CPPA rules (effective 2026)** add disclosure/opt-out obligations for automated
  decision-making technology (ADMT). Networthy's AI-suggested categorization and AI-set budgets
  are exactly the kind of automated financial decision these rules target — but the product
  already has approval-gating and an audit trail, which is *ahead* of where ADMT compliance is
  pushing the rest of the industry. Extending the existing AI-activity export into an explicit
  disclosure/opt-out surface turns a compliance obligation into a differentiator.

### Candidate new differentiators (beyond the three already claimed)

1. **Conversational, explainable safe-to-spend** — competitors compute a static black-box number;
   Networthy's chat assistant can make it interrogable using infrastructure that already exists.
2. **AI-decision transparency as ADMT-readiness** — extend the audit/export surface into an
   explicit automated-decision disclosure, ahead of where regulation is pushing competitors.
3. **The direct rebuttal to open source's own "no AI" camp** — Firefly III's maintainer states
   plainly that LLMs are "hallucinatory" and "impossible to get... to work reliably," which is
   exactly why Networthy gates every AI write behind human approval rather than avoiding AI
   altogether. Quotable, and aimed at the category's largest open-source incumbent.
4. **Regulatory-hedge positioning** — upload-first architecture as explicit insurance against
   Section 1033 litigation risk and banks' 2026 API lockdowns, a claim no Plaid-dependent
   competitor can credibly make.

See `SPEC.md` for how these translate into v2 must-haves/differentiators and `PLAN.md` for the
v2 epic sequence (epics 8–14, building on the already-shipped 1–7).
