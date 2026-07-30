---
description: 'Rules for Networthy unit, integration, and golden-eval tests — which fixture entry point can prove what, how the Mock AI provider constrains assertions, and the red-before-green requirement. Applies to test C# only.'
applyTo: 'tests/**/*.cs'
---

# Writing tests in Networthy

The full contract is [`AGENTS.md`](../../AGENTS.md); `RUNBOOK.md` §6–§7 has the ladder and the loop.

## Red before green

A regression test that has never been seen failing is not a regression test. Run it against the
unfixed code and watch it fail first — and confirm you broke the thing that actually enforces the
behaviour, not just one of the two `RequiresApproval` flags.

Put the test on the **lowest rung that would have caught the bug**:

- `tests/Networthy.Finance.Tests` — domain math, manifest integrity, parsing. No Docker.
- `tests/Networthy.IntegrationTests` — the real host, real Postgres, real migrations, real approvals.
- `tests/Networthy.IntegrationTests/Evals` — golden conversations: routing, gating, protocol. Add a
  case here for any prompt-shaped change (agent instructions, tool names, tool descriptions).

## The fixture choice is load-bearing

- **`fixture.AdminClient()`** goes through the real pipeline. It is the **only** way to prove RBAC, the
  approval gate, or the AG-UI protocol. Prefer it.
- **`fixture.AuthorizedScopeAsync()`** resolves tool classes directly and **deliberately bypasses RBAC
  and the approval gate**. A test asserting "this write is gated" through it passes while the gate is
  broken. Use it only for logic that has nothing to do with authorization.

To prove a permission boundary, send the narrower role (`household-member`) via the dev-auth headers
and assert the 403.

## What the Mock provider lets you assert

The Mock AI provider routes to a tool by **name-token match against the message**, and fills any
required string it cannot infer with the literal `"example"` — which well-written tools reject. So:

- A prompt must contain the tool's name words or nothing routes and there is no `TOOL_CALL_START`.
- **"A row appeared" is not an available assertion** in a Mock-driven chat test. Assert the platform
  contract instead: routing, gating, the approval round trip, the audit record.
- In the audit log, filter on `success == true && error == null` for real executions — blocked calls
  are recorded too, carrying `error = "Blocked: tool requires human approval"`. Approval
  re-execution is recorded on the approval record, not as a second tool-call audit entry.

## Reporting

Say which rung ran. If you skipped the integration rung because Docker was unavailable, say that
explicitly rather than implying the suite passed.
