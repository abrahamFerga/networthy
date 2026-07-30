---
description: 'Rules for the branded Networthy React UI — what belongs in the manifest versus custom React, the @plenipo/ui shell, API base wiring, and the build/embed path. Applies to frontend TypeScript only.'
applyTo: 'frontend/**/*.ts,frontend/**/*.tsx'
---

# Writing UI in Networthy

The full contract is [`AGENTS.md`](../../AGENTS.md). `frontend/networthy-ui` is the **branded app
entry**: the `@plenipo/ui` shell plus the custom finance Overview dashboard (ADR-0008). It is built by
`scripts/build-ui.ps1` and embedded into `Networthy.Host/wwwroot/app`.

## Prefer the manifest over React

A tab, an editor, a chart, a row action, a home tab, a suggested prompt, and the agent instructions
are all **declarative** — they are declared in `FinanceModule`'s manifest and rendered by the platform
shell. Custom React is a maintenance cost forever. Only reach for hand-written components when the
manifest genuinely cannot express the screen, and say why in the PR.

Manifest tab fields bind **camelCase**. A field name that does not match renders empty with no error.

## Wiring

- The API base URL comes from `VITE_API_BASE`, injected by the AppHost under Mode A and set by hand
  when running standalone. Never hardcode a host or port.
- `Cors:Origins` indices in host config must be **gapless** — `IConfiguration` binds them as an array
  and stops at the first missing index. A UI that reaches the API under Aspire but not standalone is
  usually this.
- Data fetching goes through `@tanstack/react-query`; chat streams over AG-UI. Do not invent a second
  transport.

## Constraints

- **react-router is pinned to 6.x** with three GHSAs waived in `pnpm-workspace.yaml`
  (`auditConfig.ignoreGhsas`), each with a written rationale, because no fixed 6.x release exists. Do
  not add waivers casually, and remove these as part of the react-router 7 migration — not before.
- `pnpm install --frozen-lockfile` is what CI runs. Commit the lockfile with any dependency change.
- Tailwind 3 and TypeScript are already configured; follow the existing component conventions rather
  than introducing a second styling approach.

## Verify

```bash
pnpm -C frontend/networthy-ui test
pnpm -C frontend/networthy-ui build
```

Neither proves the screen works. A UI change is proven by loading the app and looking at it — and no
backend test can see a stale CSP hash, which is a known gap (`RUNBOOK.md` §9). A change that works
under the Vite dev server but not under `docker compose up` is usually a UI-embedding problem, not a
domain bug.
