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
