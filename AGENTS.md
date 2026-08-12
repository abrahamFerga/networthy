# Networthy

A free, open-source, AI-first personal finance assistant for households, built as a **thin product
host on the Plenipo platform**: auth, multi-tenancy, RBAC-before-the-model, approvals, audit, jobs,
chat transports, documents and RAG all come from platform packages vendored in `.packages/`. This
repo owns the `finance` domain module, two connectors, and the branded UI — nothing else.

## Build and test

```bash
dotnet run --project src/Networthy.AppHost     # run it (Aspire: Postgres, Redis, API, UI)
dotnet test Networthy.slnx                     # prove it
```

Needs the **.NET 10 SDK** (pinned in `global.json`) and **Docker Desktop running**. No AI key — the
assistant runs on Plenipo's deterministic `Mock` provider, which performs real, audited tool calls
including the approval gate.

Use **`aspire run`** instead of `dotnet run` when you need telemetry: an AppHost started with
`dotnet run` is invisible to the Aspire MCP, which is the whole agent-readable observability path.

Without Docker, the integration rung cannot run. Skip it **explicitly** and say so in your report:

```bash
dotnet test Networthy.slnx --filter "FullyQualifiedName!~IntegrationTests"
```

`dotnet build` proves nothing. Exercise the change through the API or the UI, then lock it in with a
test that fails without it. **[RUNBOOK.md](RUNBOOK.md) is the source of truth** for run modes,
dev-auth headers, endpoints, the test ladder, and the gotchas — read it before running anything, and
do not duplicate it elsewhere.

## Layout

```text
src/Networthy.AppHost        Aspire orchestration — deliberate choices, see RUNBOOK §2
src/Networthy.Host           thin host: builder.AddPlenipoPlatform() + module/connector wiring
src/Networthy.Finance        the finance module — all the real domain code lives here
src/Networthy.Connectors.*   product-owned Plaid and OCR connectors
tests/Networthy.Finance.Tests       unit + manifest guard (no Docker needed)
tests/Networthy.IntegrationTests    real host, real Postgres, real migrations (Testcontainers)
frontend/networthy-ui        branded React entry on @plenipo/ui, embedded into Host/wwwroot/app
.packages/                   vendored Plenipo nupkgs — the only source for Plenipo.*
```

## Rules most often broken

- **A tool must be registered in two places.** A `ToolDescriptor` in `FinanceModule`'s manifest *and*
  a `ModuleTool` in `FinanceToolSource`. Miss either and the tool is silently never callable. Verify
  with `GET /api/admin/security/catalog` — if it isn't listed there with its permission, it doesn't
  exist.
- **The approval gate is the union of two flags.** `RequiresApproval = true` on *either* the
  `ToolDescriptor` or the `ModuleTool` gates the tool, because `AuthorizedAgentRunner` unions both
  sets. Setting one and reviewing only that one will mislead you — set both and keep them in sync.
- **Permission strings must match across both places.** Always `Permissions.ForTool(FinanceModule.Id,
  "<tool_name>")`, never a hand-written string; a mismatch 403s even for `system_admin`.
- **`AuthorizedScopeAsync()` can never prove RBAC or the approval gate** — it deliberately bypasses
  both. A test asserting "this write is gated" through it passes while the gate is broken. Use
  `fixture.AdminClient()` for anything about RBAC, approvals, or the AG-UI protocol.
- **Never assert "a row appeared" in a Mock-driven chat test.** The Mock routes by name-token match
  and fills unfillable required strings with the literal `"example"`, which tools correctly reject.
  Assert the platform contract instead: routing, gating, the approval round trip, the audit record.
- **Never edit the Plenipo platform from this repo.** Climb the escalation ladder (is it already
  there? does a product seam cover it? can a local shim carry it?), apply the shim tagged
  `TODO(plenipo#N)` so you are never blocked, and only then file a platform request.
- **Never weaken an invariant to unblock yourself:** RBAC before the model, approval-first writes,
  tenant isolation, write-only secrets, append-only audit. If a screen is awkward because of a
  permission boundary, that boundary is the product.
- **The AI provider is configured per household at runtime** under Admin → AI Settings — never in
  deployment config, and never committed.
- **Bumping the platform version touches 11 places across 5 files** (four `.csproj` files plus
  `@plenipo/ui` in `frontend/networthy-ui/package.json`). There is no central package management, so
  a partial bump compiles and then fails at runtime on a contract mismatch.

## How work is judged

State which level your evidence is at. Never report an L4 conclusion with L1 confidence:

| Level | Meaning |
|---|---|
| L1 | deterministic — a command's exit code decided it |
| L2 | rule/constraint — a linter, schema, or audit decided it |
| L3 | delayed field truth — the integration suite, a deploy, a real user |
| L4 | **model as judge — your opinion, not field truth** |
| L5 | human checkpoint — not automated verification at all |

**Prove the verifier.** A regression test must be seen **red before the fix and green after**. A test
never seen red is not a regression test — and make sure you broke the thing that actually enforces
the behaviour, not just one of the two flags above.

End work in exactly one **named** state: `Success`, `No-op`, `Blocked`, `Stalled`, `Exhausted`, or
`Approval-required`. An error or an exhausted budget never counts as success. If the same step fails
three times for three different reasons you are `Stalled` — the diagnosis is wrong, not the fix.
Escalate with the evidence rather than looping.

Never merge your own pull request. The maker is not the approver.

## Facts verified against source — do not contradict these

This repo's own docs are wrong in one place, so the trust ranking is
**source > tests > platform docs > product docs**.

- The host API is `builder.AddPlenipoPlatform()` (see `src/Networthy.Host/Program.cs`). Plenipo's
  `BUILDING_A_PRODUCT.md` documents `AddPlenipo()` / `UsePlenipo()` — **those do not exist.**
- **Golden-conversation evals are installed.** `RUNBOOK.md` §6 and §9 still claim rung 4 is "not
  installed yet"; `tests/Networthy.IntegrationTests/Evals/` has `GoldenConversationEvals.cs` and four
  cases under `Evals/cases/`. Run them for prompt-shaped changes — agent instructions, tool
  descriptions, tool names. **The runbook is stale here; fix it rather than trusting it.**
- **Plenipo packages are not on nuget.org.** They are vendored in `.packages/` and pinned there by
  `packageSourceMapping` in `nuget.config` to prevent dependency-confusion fallback. Never add a
  Plenipo package from a public feed.
- **Postgres must be `pgvector/pgvector`** — the platform's RAG migration creates a vector column at
  startup. Stock `postgres` fails on the `vector` type.
- The platform was renamed **Cortex → Plenipo**. A sibling checkout may still be called `Cortex`;
  identify it by `Plenipo.slnx`, never by folder name.
- The module id is `finance`. Networthy's roles are `household-admin` and `household-member`; a
  household is a tenant.
<!-- harness-gap: plenipo-agents#16 — remove when /plenipo:setup ships a CI reviewer that labels -->
- **There is no approval label any more.** `plenipo-agents#16` — the gap that made this repo diverge,
  because `/plenipo:setup` shipped a merger gating on `agent:approved` and nothing able to apply it —
  closed on 2026-08-10, and the reconciled assets are its resolution. Merge authority is now the
  author's identity in `workflow.json` → `autonomy.trustedAuthors`, checked by `merge-gate.mjs`, not a
  mutable label anyone with write access could add. An empty list means nothing merges, which is the
  intended fail-closed default. `agent-approval-reset.yml` is left in place but is now **vestigial**:
  nothing reads the label it expires, so it can be deleted in a follow-up.
- **Two claims that used to live here were wrong and are gone.** There is no Dependabot lane in
  `merge-gate.mjs` keyed on PR author — `grep -ri dependabot .github/` returns nothing outside test
  fixtures, and it is not clear the lane ever existed. A stale fact in this file is worse than a
  missing one, because agents trust it without checking.

## Where to look next

- [RUNBOOK.md](RUNBOOK.md) — run, observe, exercise, and prove. Start here.
- [SPEC.md](SPEC.md) — jobs to be done and personas. Judge UX against this, not against taste.
- [ARCH.md](ARCH.md) / [DECISIONS.md](DECISIONS.md) — the module boundary and the ADRs behind it.
- [SECURITY.md](SECURITY.md) — the security contract and disclosure process.
- [docs/SELF_HOSTING.md](docs/SELF_HOSTING.md) / [docs/HOSTED.md](docs/HOSTED.md) — deployment.
- `RUNBOOK.md` §9 lists the **known gaps** in this harness. Read it before assuming coverage exists.

Everything in this file is **advisory context**, not enforcement. What must be enforced lives in
`.github/workflows/ci.yml`.
