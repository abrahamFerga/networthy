# Networthy — Architecture Decision Records

## ADR-0001: Build on Cortex platform packages, not from-scratch Clean Architecture layering

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

The enterprise guardrails' default solution layout (`The<Domain>.AppHost/.Api/.Application/
.Domain/.Infrastructure/.Web`) assumes a system built from scratch. Networthy is explicitly a
product on the Cortex platform (per the user's directive: "only core things for Cortex and
everything else for the new system has to be on its own repo") — the same posture Casewell
took, where a thin host installs Cortex NuGet packages and the domain lives in a single module.
Re-deriving auth, multi-tenancy, RBAC, observability, background jobs, and audit logging
from scratch would duplicate a platform that already provides all of it, tested, and would
violate the explicit constraint against putting product-specific work in Cortex core.

### Decision

Networthy's solution is a thin ASP.NET Core host (`Networthy.Host`) that calls
`AddCortexPlatform()` and installs one domain module (`Networthy.Finance`) plus one connector
(`PlaidConnector`, which lives in `Cortex.Connectors`, not this repo). No `Domain`/
`Application`/`Infrastructure`/`Api` split — Cortex supplies those layers as packages.

### Consequences

- **Positive**: Auth, multi-tenancy, RBAC scaffold, OpenTelemetry, background jobs, and
  append-only audit logging arrive for free, already tested by the platform's own suite —
  Networthy's tests only need to cover finance domain logic.
- **Positive**: A security or compliance fix to the shared platform (e.g. a permission-check
  bug) benefits every product built on Cortex, Networthy included, without a separate patch.
- **Negative**: Networthy is coupled to Cortex's release cadence and package versioning
  (mirrors Casewell's `.packages` local-feed pattern until Cortex publishes to a real registry).
- **Neutral**: The generic guardrails' *Solution layout* section (below) reflects this shape
  instead of the from-scratch template.

### Alternatives considered

- **From-scratch Clean Architecture** (the generic template) — rejected: duplicates a tested
  platform and violates the explicit product/platform separation directive.
- **Fork Cortex into the Networthy repo** — rejected: the same platform serves multiple
  products (Casewell today, Networthy now); forking loses shared fixes and was explicitly
  ruled out ("Cortex is the core for many other systems").

---

## ADR-0002: Writes happen through chat-invoked, approval-gated MAF tools, not a REST CRUD API

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

The guardrails specify a versioned REST API with idempotency keys on all non-GET writes. Every
Cortex product (Casewell included) instead exposes reads via module-declared tab data endpoints
(`GET /api/<module>/<resource>`, RBAC-gated) and exposes **writes exclusively as MAF tools**,
invoked through chat and gated by the platform's approval mechanism before they land — this is
the whole "AI drafts, human approves, everything audited" model the differentiator in SPEC.md
depends on. A parallel REST CRUD surface for writes would let a client bypass the approval gate
entirely, defeating the point.

### Decision

Networthy has no REST write endpoints. Every account, transaction, budget, category, and
household-membership change is a `ModuleTool` on `Networthy.Finance`, most `RequiresApproval =
true` (the exception is `Transactions.LogOwn`, see ADR-0005). Reads are `GET`-only tab data
endpoints per bounded context.

### Consequences

- **Positive**: Consistent with every other Cortex product; a household member cannot script
  around the approval gate because there is no alternate write path to script against.
- **Positive**: The audit log is complete by construction — there is no write path that skips it.
- **Negative**: The guardrails' literal "idempotency key on writes" requirement doesn't map
  onto a chat-tool write the way it would onto an HTTP POST. Resolved: each tool invocation
  already has a unique execution id in the platform's audit record, which serves the same
  replay-detection purpose a client-supplied `Idempotency-Key` header would.
- **Neutral**: `Networthy.Host`'s versioned surface (`/api/v1/...`) exists for reads and for
  the Plaid webhook only, not for a full CRUD API.

### Alternatives considered

- **Full REST CRUD alongside chat tools** — rejected: a second write path undermines the
  approval-gate guarantee the whole product is sold on.
- **REST CRUD with the approval gate enforced at the HTTP layer instead of the tool layer** —
  rejected: duplicates logic Cortex already enforces at the tool layer, and diverges from
  every other Cortex product's proven shape.

---

## ADR-0003: The Plaid connector lives in Cortex core, not the Networthy repo

- **Status**: superseded by ADR-0007
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

PLAN.md already flagged this: bank-account linking via Plaid is not finance-vertical-specific
plumbing — any future Cortex product needing linked financial accounts (a expense-reimbursement
tool, a lending-adjacent product) would need the identical connector shape (service/OAuth-style
auth, pull external transactions into platform records). Cortex already hosts exactly this kind
of connector — Google Drive, S3, Documenso — each generically reusable and each following the
same `IConnector` contract.

### Decision

`PlaidConnector` is implemented in `Cortex.Connectors` (the Cortex repo), following the
`IConnectorSettings`/service-auth pattern the existing connectors use. `Networthy.Host` only
registers it (`AddCortexConnector<PlaidConnector>()`).

### Consequences

- **Positive**: Any future Cortex vertical gets bank-linking for free; Networthy is not a
  bottleneck for a connector that isn't finance-specific.
- **Positive**: Keeps `Networthy.Finance` scoped to genuinely domain logic (accounts,
  transactions, budgets), matching the explicit platform/product boundary directive.
- **Negative**: A Networthy-specific Plaid behavior (if one ever emerges) requires a Cortex
  change, not a Networthy-only change — acceptable, since none is anticipated for v1.

### Alternatives considered

- **Build Plaid integration inside `Networthy.Finance`** — rejected: violates the explicit
  "only core things go in Cortex, product-specific work goes in its own repo" directive by
  putting a genuinely generic capability in the wrong repo (the connector is not finance logic;
  it's a data-source integration exactly like the others already in Cortex core).

---

## ADR-0004: Statement parsing uses a hybrid extraction approach

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

PLAN.md's open question #3 asked whether statement parsing should use LLM/vision extraction
(flexible, handles any layout), a bank-specific template library (more accurate for supported
banks, brittle elsewhere), or a hybrid. This is the single highest-leverage decision in the
plan — it determines both parsing accuracy and unit cost per statement.

### Decision

Hybrid: a small library of template-based extractors for the highest-volume US banks/card
issuers (starting list: the top 10 by household market share, expanded as demand data
justifies it) runs first; any statement that doesn't match a known template falls back to
AI vision-based extraction (via the tenant's configured AI provider, same pluggable connection
Cortex already gives every module). Both paths land in the same review queue before anything
posts as a Transaction (ADR-0002's approval gate applies identically either way).

### Consequences

- **Positive**: Common statements (the templates) parse cheaply and near-deterministically;
  the AI-first promise still holds for every bank a template doesn't cover, satisfying the
  "AI-first" positioning without betting the whole feature's accuracy on general-purpose
  vision extraction from day one.
- **Negative**: Building and maintaining the template library is real, ongoing work — banks
  change statement layouts. Accepted as the cost of the accuracy floor.
- **Neutral**: Which banks make the initial template list is a build-time decision, not an
  architectural one — deferred to `/development:build-system`.

### Alternatives considered

- **LLM/vision-only** — rejected: no accuracy floor for the highest-volume banks on day one;
  every statement costs an inference call.
- **Templates-only** — rejected: contradicts the AI-first positioning and fails immediately
  for any bank without a template — a bad first impression for exactly the users the product
  is trying to win.

---

## ADR-0005: A household member's own logged transaction is an ungated quick-capture exception

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

PLAN.md's open question #1: should `Transactions.LogOwn` (a household member manually entering
their own spending) be approval-gated like every other household-affecting write, or an
ungated quick-capture exception like Casewell's `log_time`? SPEC.md's approval-gate
differentiator is specifically about **AI-suggested** actions being reviewable before they
become financial fact — it was never framed as gating direct human data entry.

### Decision

`Transactions.LogOwn` is the module's one deliberately ungated write, mirroring Casewell's
`log_time`: append-only, own-user, correctable by a follow-up entry, no approval step. AI-
suggested categorization and any transaction arriving via statement import or Plaid sync
**remain fully approval-gated** — that is where an LLM can actually be wrong, which is exactly
what the differentiator promises to catch.

### Consequences

- **Positive**: A household member logging a $4 coffee doesn't wait on the household admin —
  the same capture-friction lesson Casewell already learned (friction is why people under-record).
- **Positive**: The differentiator's actual claim — AI actions are reviewable — stays true and
  precise; it was never a claim about gating humans entering their own known-true data.
- **Negative**: A household member could, in principle, mis-enter their own transaction with
  no review step; accepted, since it's their own data and correctable by a follow-up entry,
  same as Casewell's precedent.

### Alternatives considered

- **Approval-gate every transaction regardless of source** — rejected: kills the exact
  quick-capture behavior the product needs for member-logged spending to actually get logged,
  and conflates "AI might be wrong" with "a human might mistype," which are different problems.

---

## ADR-0006: Plaid credentials follow the existing per-tenant connector settings pattern

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

PLAN.md's open question #2 asked whether Plaid credentials should be platform-managed
(metered) or household bring-your-own. Cortex already has an answer for exactly this shape of
question: every service-auth connector (S3, Documenso, Google Drive) stores its credentials
via `IConnectorSettings`, configured per tenant under Integrations — no platform-wide account
required, no new metering infrastructure needed.

### Decision

Plaid credentials are tenant (household) -level `IConnectorSettings`, identical in shape to
the existing connectors. Each household's admin configures its own Plaid developer credentials
under Integrations to enable bank-linking. A platform-managed convenience tier (so a hosted
household doesn't need its own Plaid developer account) is an explicit fast-follow, not a v1
architecture requirement — it changes nothing structurally when it ships.

### Consequences

- **Positive**: No new pattern to build; this falls directly out of the connector SDK Cortex
  already has, with zero new architecture.
- **Negative**: A hosted-tier household must obtain its own Plaid credentials in v1, which is
  friction the fast-follow removes later.

### Alternatives considered

- **Platform-managed Plaid app with per-household metering** (mirroring Cortex's AI-connection
  billing pattern) — deferred, not rejected: genuinely valuable for the hosted tier, but adds
  scope v1 doesn't need when the existing per-tenant settings pattern already unblocks the
  bank-linking feature end to end.

---

## ADR-0007: The Plaid connector is a domain-specific connector in the Networthy repo

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Product owner directive; supersedes ADR-0003

### Context

ADR-0003 placed the Plaid connector in Cortex core, reasoning that bank-account linking is a
generic data-source integration like Google Drive / S3 / Documenso. The product owner overrode
that: Plaid is *specific to the financial domain*, not a generic platform data source the way a
file store is — a legal or healthcare vertical would never reach for it. Cortex should instead
let a domain system add its **own** connector, keeping Cortex core free of one product's
domain-specific integrations. This turned out to require no new platform mechanism: the
connector SDK's registration, catalog, per-tenant enable/settings, permission gating, and
agent-tool exposure are all DI-driven and never keyed to a connector's assembly — a connector
defined in a product's own assembly is already a first-class connector. Cortex core gained only
a *proof + documentation* of this (a host-defined sample connector, keyless tests, and the
BUILDING_A_PRODUCT.md seam #2 update) — no product-specific code entered the platform.

### Decision

`PlaidConnector` is implemented in the **Networthy repo** (a `Networthy.Connectors.Plaid`
project, or alongside `Networthy.Finance` — a build-time detail), referencing
`Cortex.Connectors.Sdk` + `Cortex.Modules.Sdk`, and registered in `Networthy.Host` with the same
`AddCortexConnector<PlaidConnector>()` call a built-in uses. It follows the same
`IConnectorSettings`/service-auth pattern (ADR-0006 unchanged) as the built-in connectors.

### Consequences

- **Positive**: Cortex core carries no finance-specific integration; the platform stays a clean
  base for every vertical, satisfying the "only core things for Cortex" directive precisely —
  the *generic* capability (host-defined connectors) is core; the *specific* connector (Plaid)
  is not.
- **Positive**: Networthy owns its Plaid connector's release cadence and any finance-specific
  behavior outright — a Plaid change is a Networthy-only change, no Cortex round-trip.
- **Positive**: The proof/documentation added to Cortex benefits every future vertical that
  needs a domain-specific connector, not just Networthy.
- **Negative**: If a second Cortex product ever genuinely needs Plaid, it would either depend on
  Networthy's connector package or re-implement it — acceptable, and a good problem to have
  (it would then be evidence Plaid is generic after all, and could graduate to core).

### Alternatives considered

- **Keep Plaid in Cortex core (ADR-0003)** — rejected by the product owner: it puts a
  finance-domain-specific integration in a platform meant to serve many unrelated verticals.
- **A new plugin/marketplace mechanism for connectors** — rejected: unnecessary, since the
  existing `AddCortexConnector<T>()` + DI-driven discovery already makes any assembly's
  `IConnector` first-class. Building a heavier mechanism would be solving a problem that doesn't
  exist.

---

## Open items not resolved by an ADR

- **Idempotency-Key guardrail for the Plaid webhook route** — `/api/connectors/plaid/webhook`
  is the one true inbound HTTP write surface in this system (everything else is a chat tool
  per ADR-0002). It should follow the same signed-webhook-plus-unique-event-id dedup pattern
  Cortex's Stripe billing webhook already uses (`BillingEvent`'s unique `Provider`+`EventId`
  constraint) rather than a generic `Idempotency-Key` header — confirm this pattern is reused
  verbatim at build time; if Cortex's webhook-dedup primitive isn't generically extracted yet,
  that extraction is a small Cortex-core change (ADR-0003's territory), not a Networthy one.
- **v1 scale target** (households at hosted-tier launch) — carried over from PLAN.md/SPEC.md.
  Genuinely a business decision, not an architectural one; the chosen Postgres/Container
  compute topology below comfortably covers anything up to a few thousand households without
  re-architecture, so this does not block Stage 3.
