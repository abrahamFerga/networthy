---
name: run-networthy
description: >
  Run, observe, and test Networthy locally — and prove a change works at runtime rather than merely
  compiling. Covers the Aspire AppHost, headless mode, the docker-compose image, dev-auth headers,
  exercising the assistant over AG-UI, the approval round trip, reading Aspire telemetry, and the
  test ladder from build to Testcontainers E2E. Zero API keys required.
  USE FOR: starting Networthy, reproducing a bug, calling its API, driving its UI, adding or running
  tests, verifying a feature before opening a PR. DO NOT USE FOR: platform-level Plenipo development
  (that lives in the Plenipo repo's own run skill).
license: MIT
---

# Run & test Networthy

**[RUNBOOK.md](../../../RUNBOOK.md) is the source of truth.** This skill is the index — read the
runbook section you need rather than guessing.

Networthy is a thin product host on the **Plenipo platform**. Auth, multi-tenancy,
RBAC-before-the-model, approvals, audit, jobs, chat transports, documents and RAG come from platform
packages vendored in `.packages/`. This repo owns the `finance` domain module. **Do not rebuild
platform concerns here** — if you find yourself writing a permission checker, an audit log, or a
tenant filter, stop and use the platform's.

## The two commands

```bash
dotnet run --project src/Networthy.AppHost     # run it   (Aspire: Postgres, Redis, API, UI)
dotnet test Networthy.slnx                      # prove it (unit + Testcontainers E2E)
```

Docker Desktop must be running. No AI key: the assistant uses Plenipo's `Mock` provider, which still
performs **real, audited tool calls and triggers the approval gate**.

## Where to look

| I need to… | RUNBOOK section |
|---|---|
| start it (Aspire / headless / compose) | §2 Run |
| know when it's ready | §2 Ready signals |
| authenticate a request | §3 — `X-Dev-Subject` / `X-Dev-Tenant` / `X-Dev-Roles` |
| fire a ready-made request at any endpoint | [`networthy.http`](../../../networthy.http) — the committed catalog |
| send a chat turn and read the event stream | §4 AG-UI |
| **understand why a chat-driven test behaves oddly** | §4 *How the Mock provider behaves* |
| drive the approval round trip | §4 Approvals |
| check a tool's permission wiring | §4 — `/api/admin/security/catalog` |
| read logs, traces, metrics | §5 Observe |
| decide which tests to write and run | §6 The test ladder |
| debug a failure methodically | §7 The verification loop |
| a symptom I've seen before | §8 Gotchas |
| what this harness still lacks | §9 Known gaps |

## Non-negotiables

- **Prove it at runtime.** `dotnet build` proves nothing. Exercise the change through a real request
  or the UI, then lock it in with a test that fails without the fix.
- **Use `AdminClient()` for anything security-shaped.** `AuthorizedScopeAsync()` bypasses RBAC and
  the approval gate by design, so it can never prove they work. `ChatAndApprovalTests` is the suite
  that actually proves the gate.
- **The approval gate is the union of two flags** — `RequiresApproval` on the `ToolDescriptor` in
  `FinanceModule` *and* on the `ModuleTool` in `FinanceToolSource`. Set both; flipping one alone
  changes nothing, which will mislead you when testing.
- **A new tool needs three things**: the `ToolDescriptor`, the `ModuleTool`, and the same permission
  string in both. `/api/admin/security/catalog` shows the gap.
- **A household is a tenant.** Roles are `household-admin` and `household-member`; nothing crosses a
  household boundary.
- **Never commit a secret.** Provider keys are per-household runtime settings entered under
  **Admin → AI Settings** and stored write-only in the vault.
