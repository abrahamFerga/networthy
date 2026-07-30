# Copilot instructions — Networthy

**The full contract is [`AGENTS.md`](../AGENTS.md) at the repo root. Read it.** This file exists
because Copilot Chat on github.com reads *only* this file — it deliberately carries the minimum a
reviewer in the browser needs standalone, and **does not duplicate `AGENTS.md`**. Do not "helpfully"
sync them; add new rules to `AGENTS.md` and leave this short.

## What this repo is

A free, open-source AI-first personal finance assistant for households, built as a **thin host on the
Plenipo platform**. Auth, multi-tenancy, RBAC-before-the-model, approvals, audit, jobs, chat, and RAG
come from platform packages vendored in `.packages/`. This repo owns the `finance` module, two
connectors, and the branded UI.

## Verification

```bash
dotnet build Networthy.slnx -c Release
dotnet test  Networthy.slnx -c Release
```

Needs .NET 10 and Docker running. CI (`.github/workflows/ci.yml`) additionally runs a **.NET
vulnerability audit that fails on any hit**, plus `pnpm audit`, test, and build for
`frontend/networthy-ui`. Green CI is the floor, not the proof — it cannot tell you the feature does
what was asked.

## The three rules that catch most mistakes

1. **A tool needs registering in two places** — a `ToolDescriptor` in `FinanceModule`'s manifest *and*
   a `ModuleTool` in `FinanceToolSource`. Miss either and it is silently never callable. The approval
   gate is the **union** of the `RequiresApproval` flags on both, so a PR that sets only one has not
   changed the gate it appears to change.
2. **A change is done when a test that fails without it passes with it** — seen red first. `dotnet
   build` succeeding proves nothing. Tests using `AuthorizedScopeAsync()` bypass RBAC and the
   approval gate by design and can never prove either; those need `fixture.AdminClient()`.
3. **Never weaken an invariant to unblock a change**: RBAC before the model, approval-first writes,
   tenant isolation, write-only secrets, append-only audit. And never edit the Plenipo platform from
   this repo — shim locally with a `TODO(plenipo#N)` tag and file a platform request.

## When reviewing

Say which level your evidence is at: L1 deterministic (a command's exit code) · L2 rule/linter · L3
field truth (the integration suite) · L4 **your opinion from reading the diff** · L5 human judgement.
Most code review is L4 — say so rather than implying something ran.
