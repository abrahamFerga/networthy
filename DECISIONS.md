# Networthy — Architecture Decision Records

## ADR-0001: Build on Plenipo platform packages, not from-scratch Clean Architecture layering

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

The enterprise guardrails' default solution layout (`The<Domain>.AppHost/.Api/.Application/
.Domain/.Infrastructure/.Web`) assumes a system built from scratch. Networthy is explicitly a
product on the Plenipo platform (per the user's directive: "only core things for Plenipo and
everything else for the new system has to be on its own repo") — the same posture Casewell
took, where a thin host installs Plenipo NuGet packages and the domain lives in a single module.
Re-deriving auth, multi-tenancy, RBAC, observability, background jobs, and audit logging
from scratch would duplicate a platform that already provides all of it, tested, and would
violate the explicit constraint against putting product-specific work in Plenipo core.

### Decision

Networthy's solution is a thin ASP.NET Core host (`Networthy.Host`) that calls
`AddPlenipoPlatform()` and installs one domain module (`Networthy.Finance`) plus one connector
(`PlaidConnector`, which lives in `Plenipo.Connectors`, not this repo). No `Domain`/
`Application`/`Infrastructure`/`Api` split — Plenipo supplies those layers as packages.

### Consequences

- **Positive**: Auth, multi-tenancy, RBAC scaffold, OpenTelemetry, background jobs, and
  append-only audit logging arrive for free, already tested by the platform's own suite —
  Networthy's tests only need to cover finance domain logic.
- **Positive**: A security or compliance fix to the shared platform (e.g. a permission-check
  bug) benefits every product built on Plenipo, Networthy included, without a separate patch.
- **Negative**: Networthy is coupled to Plenipo's release cadence and package versioning
  (mirrors Casewell's `.packages` local-feed pattern until Plenipo publishes to a real registry).
- **Neutral**: The generic guardrails' *Solution layout* section (below) reflects this shape
  instead of the from-scratch template.

### Alternatives considered

- **From-scratch Clean Architecture** (the generic template) — rejected: duplicates a tested
  platform and violates the explicit product/platform separation directive.
- **Fork Plenipo into the Networthy repo** — rejected: the same platform serves multiple
  products (Casewell today, Networthy now); forking loses shared fixes and was explicitly
  ruled out ("Plenipo is the core for many other systems").

---

## ADR-0002: Writes happen through chat-invoked, approval-gated MAF tools, not a REST CRUD API

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

The guardrails specify a versioned REST API with idempotency keys on all non-GET writes. Every
Plenipo product (Casewell included) instead exposes reads via module-declared tab data endpoints
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

- **Positive**: Consistent with every other Plenipo product; a household member cannot script
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
  rejected: duplicates logic Plenipo already enforces at the tool layer, and diverges from
  every other Plenipo product's proven shape.

### Amendment (manual CRUD, issue #29): scope clarified, not reversed

This ADR governs **AI-initiated** writes: an AI action must never reach the household's data
except through an approval-gated tool. It didn't anticipate a second, orthogonal write path —
the tab editors' manual-bookkeeping forms (`ManualCrudEndpoints.cs`, shipped in the "Delivered
since v1" wave, see SPEC.md), which are `POST`/`DELETE` endpoints under `/api/finance/...`
gated by RBAC (`FinanceModule.ManageFinance`) instead of the approval queue. This is not a
regression of the guarantee above: the approval gate exists to catch an AI acting on the
household's behalf, and a human directly editing their own books was never the case ADR-0002
was written to prevent — "AI-first ≠ chat-only" (`ManualCrudEndpoints.cs`'s own doc comment).
ARCH.md's API surface section documents both surfaces explicitly so this isn't misread as "no
REST writes exist" again.

---

## ADR-0003: The Plaid connector lives in Plenipo core, not the Networthy repo

- **Status**: superseded by ADR-0007
- **Date**: 2026-07-09
- **Deciders**: Architecture phase (this document)

### Context

PLAN.md already flagged this: bank-account linking via Plaid is not finance-vertical-specific
plumbing — any future Plenipo product needing linked financial accounts (a expense-reimbursement
tool, a lending-adjacent product) would need the identical connector shape (service/OAuth-style
auth, pull external transactions into platform records). Plenipo already hosts exactly this kind
of connector — Google Drive, S3, Documenso — each generically reusable and each following the
same `IConnector` contract.

### Decision

`PlaidConnector` is implemented in `Plenipo.Connectors` (the Plenipo repo), following the
`IConnectorSettings`/service-auth pattern the existing connectors use. `Networthy.Host` only
registers it (`AddPlenipoConnector<PlaidConnector>()`).

### Consequences

- **Positive**: Any future Plenipo vertical gets bank-linking for free; Networthy is not a
  bottleneck for a connector that isn't finance-specific.
- **Positive**: Keeps `Networthy.Finance` scoped to genuinely domain logic (accounts,
  transactions, budgets), matching the explicit platform/product boundary directive.
- **Negative**: A Networthy-specific Plaid behavior (if one ever emerges) requires a Plenipo
  change, not a Networthy-only change — acceptable, since none is anticipated for v1.

### Alternatives considered

- **Build Plaid integration inside `Networthy.Finance`** — rejected: violates the explicit
  "only core things go in Plenipo, product-specific work goes in its own repo" directive by
  putting a genuinely generic capability in the wrong repo (the connector is not finance logic;
  it's a data-source integration exactly like the others already in Plenipo core).

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
Plenipo already gives every module). Both paths land in the same review queue before anything
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
(metered) or household bring-your-own. Plenipo already has an answer for exactly this shape of
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

- **Positive**: No new pattern to build; this falls directly out of the connector SDK Plenipo
  already has, with zero new architecture.
- **Negative**: A hosted-tier household must obtain its own Plaid credentials in v1, which is
  friction the fast-follow removes later.

### Alternatives considered

- **Platform-managed Plaid app with per-household metering** (mirroring Plenipo's AI-connection
  billing pattern) — deferred, not rejected: genuinely valuable for the hosted tier, but adds
  scope v1 doesn't need when the existing per-tenant settings pattern already unblocks the
  bank-linking feature end to end.

---

## ADR-0007: The Plaid connector is a domain-specific connector in the Networthy repo

- **Status**: accepted
- **Date**: 2026-07-09
- **Deciders**: Product owner directive; supersedes ADR-0003

### Context

ADR-0003 placed the Plaid connector in Plenipo core, reasoning that bank-account linking is a
generic data-source integration like Google Drive / S3 / Documenso. The product owner overrode
that: Plaid is *specific to the financial domain*, not a generic platform data source the way a
file store is — a legal or healthcare vertical would never reach for it. Plenipo should instead
let a domain system add its **own** connector, keeping Plenipo core free of one product's
domain-specific integrations. This turned out to require no new platform mechanism: the
connector SDK's registration, catalog, per-tenant enable/settings, permission gating, and
agent-tool exposure are all DI-driven and never keyed to a connector's assembly — a connector
defined in a product's own assembly is already a first-class connector. Plenipo core gained only
a *proof + documentation* of this (a host-defined sample connector, keyless tests, and the
BUILDING_A_PRODUCT.md seam #2 update) — no product-specific code entered the platform.

### Decision

`PlaidConnector` is implemented in the **Networthy repo** (a `Networthy.Connectors.Plaid`
project, or alongside `Networthy.Finance` — a build-time detail), referencing
`Plenipo.Connectors.Sdk` + `Plenipo.Modules.Sdk`, and registered in `Networthy.Host` with the same
`AddPlenipoConnector<PlaidConnector>()` call a built-in uses. It follows the same
`IConnectorSettings`/service-auth pattern (ADR-0006 unchanged) as the built-in connectors.

### Consequences

- **Positive**: Plenipo core carries no finance-specific integration; the platform stays a clean
  base for every vertical, satisfying the "only core things for Plenipo" directive precisely —
  the *generic* capability (host-defined connectors) is core; the *specific* connector (Plaid)
  is not.
- **Positive**: Networthy owns its Plaid connector's release cadence and any finance-specific
  behavior outright — a Plaid change is a Networthy-only change, no Plenipo round-trip.
- **Positive**: The proof/documentation added to Plenipo benefits every future vertical that
  needs a domain-specific connector, not just Networthy.
- **Negative**: If a second Plenipo product ever genuinely needs Plaid, it would either depend on
  Networthy's connector package or re-implement it — acceptable, and a good problem to have
  (it would then be evidence Plaid is generic after all, and could graduate to core).

### Alternatives considered

- **Keep Plaid in Plenipo core (ADR-0003)** — rejected by the product owner: it puts a
  finance-domain-specific integration in a platform meant to serve many unrelated verticals.
- **A new plugin/marketplace mechanism for connectors** — rejected: unnecessary, since the
  existing `AddPlenipoConnector<T>()` + DI-driven discovery already makes any assembly's
  `IConnector` first-class. Building a heavier mechanism would be solving a problem that doesn't
  exist.

---

## ADR-0008: Networthy grows its own SPA entry for the v2 dashboard (moduleUi seam)

- **Status**: accepted
- **Date**: 2026-07-11
- **Deciders**: Architecture pass for v2 epic 8 (Household Command Center)

### Context

v1 embedded the stock, brand-agnostic `@plenipo/ui` app shell — every tab is the server-driven
generic table/chart, zero custom React (see ARCH.md's original SPA section). Epic 8's home
dashboard is a card-grid composition (hero safe-to-spend, net worth, budget snapshot, upcoming
bills, recent activity, goal progress) that the generic tab machinery deliberately does not
express. Plenipo's `docs/RICH_UI_KIT_PLAN.md` research settled the platform-vs-product boundary:
the platform ships the *pieces* (`StatTile`, `ProgressBar`, chart kinds, `useMediaQuery`,
released in v0.1.0-alpha.17) and the existing `<PlenipoApp moduleUi>` registry is the seam; the
dashboard *layout* is product-owned. No new platform seam was added for it, on purpose.

### Decision

Add `frontend/networthy-ui`: a minimal Vite app whose entry renders
`<PlenipoApp moduleUi={[finance]} />`, registering a custom **Overview** tab component for the
finance module (everything else keeps falling back to `GenericTab`). It depends on `@plenipo/ui`
from the sibling Plenipo checkout (`file:` dependency) — the same checkout `scripts/build-ui.ps1`
already requires — and that script now builds *this* app (which bakes the brand as a side
effect) instead of the stock shell. The built bundle stays committed under
`src/Networthy.Host/wwwroot/app`, so clone-and-run remains true.

Server side, the module adds one composed read endpoint (`/api/finance/overview`) so the
dashboard is one fetch, and the safe-to-spend figure is computed **server-side in one place** —
epic 11's conversational surface must interrogate the *same* number the dashboard shows, so the
formula lives in the module, never re-derived in the SPA.

### Consequences

- **Positive**: the dashboard composes released, tested platform primitives; per-tab custom UI
  is additive (any tab can upgrade later without touching the rest).
- **Positive**: one aggregate endpoint keeps the dashboard honest — every figure traces to the
  same queries the tabs use.
- **Negative**: the committed app bundle can no longer be refreshed from release assets alone —
  `update-platform.ps1 -WithUi` keeps working for the admin bundle, but the app bundle's
  canonical build becomes `build-ui.ps1` (checkout path).
- **Neutral**: solo/simple deployments notice nothing — the custom entry renders identically
  except for the new Overview tab.

### Alternatives considered

- **A server-declared "card-grid" tab kind in Plenipo** — rejected in the platform's own plan:
  dashboards are exactly where products differ; a generic grammar for them would grow without
  bound. The platform ships pieces, not layouts.
- **Compose the dashboard from multiple generic tabs** — rejected: a tab is a page, not a card;
  the result reads as navigation, not an overview.
- **Keep "opens on Chat"** — rejected by the v2 research: every competitor's home is a summary
  surface; chat stays one tap away (and the assistant panel is unaffected).

### Amendment (2026-07-11, same day — PR #44): the UI library vendors as a tarball, not a checkout

> **Superseded by the 2026-07-15 amendment below** — the tarball channel described here no longer
> exists. Kept as history. Identifiers in this block are deliberately the **pre-rename** ones
> (`@cortex/ui`, `cortex-ui-*.tgz`): they name artifacts that really existed at the time, and
> rewriting them to `@plenipo/*` would invent files that never shipped.

The Decision text above says `frontend/networthy-ui` depends on the UI library from **the sibling
checkout** (`file:` dependency) — true only for a few hours. Commit `45ceb8b` replaced it with a
**vendored tarball** dependency instead
(`"@cortex/ui": "file:../../.packages/cortex-ui-0.1.0-alpha.19.tgz"`): `build-ui.ps1` no longer
required a checkout to build the app bundle, and `update-platform.ps1` fetched and repointed the
tarball on every platform vendor. A sibling checkout became optional again — the dev harness still
prefers one for live HMR when present (README.md's "Developing against the Plenipo platform"
section), but a bare `git clone` + `docker compose up` needed none, restoring the "clone-and-run"
property this ADR's Consequences section already claimed. Read the Consequences bullet about
`build-ui.ps1` "(checkout path)" as "(vendored-tarball path)" for this period.

### Amendment (2026-07-15): the platform is renamed Plenipo, and `@plenipo/ui` installs from npm

Two changes land together, both forced by upstream.

**The rename.** The platform formerly called *Cortex* is now **Plenipo** — a pure identifier
rename (`Cortex.*` → `Plenipo.*`, `@cortex/ui` → `@plenipo/ui`, `AddCortexPlatform()` →
`AddPlenipoPlatform()`, `<CortexApp>` → `<PlenipoApp>`, the `cortex-platform`/`cortex-audit`
databases → `plenipo-*`), with no behavioural change. Every reference in this repo moved with it.
Releases are consumed from **`abrahamFerga/Plenipo`** — the original platform repo, renamed in
place (`abrahamFerga/Cortex` still redirects there).

**The UI channel.** The tarball this ADR's previous amendment depends on **no longer exists**.
Plenipo's `publish.yml` stopped attaching `<ui>-<version>.tgz` to releases and now publishes the
library to the **public npm registry** with provenance, naming this ADR explicitly as the reason
("product hosts that build their own app entry on the moduleUi seam … now install it with
`pnpm add @plenipo/ui`"). So `frontend/networthy-ui` takes a plain registry dependency
(`"@plenipo/ui": "0.1.0-alpha.23"`), and `update-platform.ps1` repoints the *version* rather than
downloading a tarball.

Consequences of the channel change, stated plainly because it narrows a property this ADR claimed:

- **The UI half of "clone-and-run offline" ends.** Building the app bundle now needs npm registry
  access. It is a normal public dependency, like `react` — but it is no longer *vendored*.
- **The .NET half is unchanged.** `Plenipo.*` nupkgs publish only to GitHub Packages, which
  requires a PAT even for public repos, so they stay vendored in `.packages/` and `dotnet restore`
  still works on a bare clone. `.packages/` is now nupkgs-only.
- **The committed bundles still make a *clone* runnable** — `wwwroot/app` and `wwwroot/admin` stay
  committed, so `docker compose up` on a bare clone needs neither npm nor a checkout. Only
  *rebuilding* the UI needs the registry.
- **pnpm's `minimumReleaseAge` quarantine is bypassed for this one package** (see
  `frontend/networthy-ui/pnpm-workspace.yaml`): it is our own first-party library published with
  provenance, and the quarantine would otherwise block same-day platform bumps. The exemption is
  pinned per exact version on purpose, so each bump is a deliberate edit.

**Rejected: keep vendoring via `npm pack`.** It would preserve the offline property, but it
fights upstream's stated direction, keeps a ~214 kB binary churning in git, and buys nothing the
committed `wwwroot/` bundles don't already give a bare clone.

**Operational note — one platform repo.** `abrahamFerga/Cortex` was renamed in place to
**`abrahamFerga/Plenipo`**, which is the single source for the platform: it holds the historical
`alpha.14`–`alpha.21` releases (with the old `Cortex.*` assets) and, from **`alpha.23`** onward, the
renamed `Plenipo.*` releases. `update-platform.ps1` defaults to it.

A short-lived `Plenipo/Plenipo` org mirror briefly carried `alpha.22` — the first release built from
renamed source — while the rename was being sorted out. The org was retired and its two fixes
(the package.json repo URLs and the `--tag` that lets a prerelease publish at all) are folded into
this repo's history. `alpha.23` re-homes those artifacts here and is otherwise identical to
`alpha.22`; the orphaned `@plenipo/ui@0.1.0-alpha.22` remains on npm with a provenance attestation
naming the deleted org, which is why nothing should depend on it.

---

## Open items not resolved by an ADR

- **Idempotency-Key guardrail for the Plaid webhook route** — `/api/connectors/plaid/webhook`
  is the one true inbound HTTP write surface in this system (everything else is a chat tool
  per ADR-0002). It should follow the same signed-webhook-plus-unique-event-id dedup pattern
  Plenipo's Stripe billing webhook already uses (`BillingEvent`'s unique `Provider`+`EventId`
  constraint) rather than a generic `Idempotency-Key` header — confirm this pattern is reused
  verbatim at build time; if Plenipo's webhook-dedup primitive isn't generically extracted yet,
  that extraction is a small Plenipo-core change (ADR-0003's territory), not a Networthy one.
- **v1 scale target** (households at hosted-tier launch) — carried over from PLAN.md/SPEC.md.
  Genuinely a business decision, not an architectural one; the chosen Postgres/Container
  compute topology below comfortably covers anything up to a few thousand households without
  re-architecture, so this does not block Stage 3.
