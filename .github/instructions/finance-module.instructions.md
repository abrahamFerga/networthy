---
description: 'Rules for the finance domain module, host wiring, and connectors — tool registration, permissions, tenant isolation, and migrations. Applies to production C# only; test rules live in tests.instructions.md.'
applyTo: 'src/**/*.cs'
---

# Writing C# in Networthy

The full contract is [`AGENTS.md`](../../AGENTS.md); these are the file-level rules for production
code. Almost everything belongs in `src/Networthy.Finance` — `Networthy.Host` stays thin.

## Extend the platform, do not rebuild it

Auth, tenancy, RBAC, approvals, audit, jobs, chat transports, documents and RAG are already provided
by the vendored `Plenipo.*` packages. Before adding infrastructure, check whether a platform seam
already covers it. Never edit the platform from this repo — shim locally with a `TODO(plenipo#N)` tag
and file a platform request.

## Adding or changing a tool

1. Declare a `ToolDescriptor` in `FinanceModule`'s manifest.
2. Declare the matching `ModuleTool` in `FinanceToolSource`.

Both are required. One alone means the tool silently never gets called, with no error.

- Permission strings: always `Permissions.ForTool(FinanceModule.Id, "<tool_name>")` in **both**
  places. A hand-written string that drifts produces a 403 even for `system_admin`.
- `RequiresApproval` is **unioned across both declarations** by `AuthorizedAgentRunner`. To gate a
  write, set it in both and keep them in sync; to verify a gate, check both.
- Any tool that mutates financial fact is approval-gated. The single exception by design is the
  caller's own quick-capture write (`log_own_transaction`).
- Tool names are matched by the Mock provider's name-token routing, so renaming a tool changes which
  prompts reach it — re-run the golden evals in `tests/Networthy.IntegrationTests/Evals/`.

## Persistence

- Every tenant-scoped entity gets its own `b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId)`
  in `FinanceDbContext`. The filter is **per-entity** — a new entity without one leaks across
  households, and no compiler error will tell you.
- Migrations live in `Persistence/Migrations` and run on startup. Postgres must be
  `pgvector/pgvector`.
- Money is decimal. Never float.

## Never

- Weaken RBAC, tenant isolation, the audit trail, or an approval gate to make a change simpler.
- Change what a user's financial data *means* to make a screen nicer — a confusing but correct number
  gets a better label, not a different calculation.
- Read AI provider configuration from deployment config; it is per-household at runtime.
- Invent a domain rule. If you cannot tell whether a behaviour is wrong or merely unfamiliar, ask.
