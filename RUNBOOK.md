# Running and testing Networthy

Everything an agent (or a human) needs to take Networthy from a cold clone to a **proven** change.
Nothing here requires an API key, a cloud account, or a Plenipo checkout.

Networthy is a **thin product host on the Plenipo platform**: auth, multi-tenancy,
RBAC-before-the-model, approvals, audit, jobs, chat transports, documents and RAG come from platform
packages vendored in `.packages/`. This repo owns the `finance` domain module and nothing else.

## 0. The one-screen version

```bash
dotnet run --project src/Networthy.AppHost     # run it
dotnet test Networthy.slnx                      # prove it
```

If both are green **and** you exercised the change through the API or the UI, you are done. If you
only ran `dotnet build`, you are not.

## 1. Prerequisites

| Need | Why | Check |
|---|---|---|
| **.NET 10 SDK** | everything targets `net10.0` (`global.json` pins the SDK) | `dotnet --version` |
| **Docker Desktop, running** | Postgres/Redis containers; Testcontainers for E2E | `docker ps` |
| **Node 20+ / pnpm** | *only* if you touch `frontend/networthy-ui` | `corepack enable && pnpm -v` |

No AI key. The assistant runs on Plenipo's dependency-free **`Mock` provider**, which streams
deterministic replies **and performs real, audited tool calls including triggering the approval
gate**. That is what makes the whole security pipeline testable on a fresh clone and in CI. A real
provider is configured per household at runtime under **Admin → AI Settings** — never in deployment
config.

## 2. Run

### Mode A — Aspire AppHost (the default)

```bash
dotnet run --project src/Networthy.AppHost
```

Brings up Postgres (`plenipo-platform` + `plenipo-audit`), Redis, the API (`networthy-api`), and —
when `pnpm` is on PATH — the `networthy-ui` Vite dev server, then opens the Aspire dashboard. Take
the API's external HTTP endpoint from the `networthy-api` resource; that base URL is what every call
below targets.

**`dotnet run` and `aspire run` are not equivalent.** Both start the stack, but an AppHost launched
with `dotnet run` is **invisible to the Aspire MCP**, which is the entire agent-readable
observability path. Use `aspire run` when you intend to read telemetry through tooling.

Deliberate AppHost choices — **do not "clean these up"**, each was learned by losing a dev database:

| Choice | Why |
|---|---|
| **Fixed** dev Postgres password (`networthy-dev-only`) | Postgres bakes the password into the data volume at first init and never re-reads it. With Aspire's *generated* password, regenerating user-secrets leaves the volume unopenable, health checks never pass, and `WaitFor` blocks the API forever with nothing in the console |
| **Pinned** host port `15432` | The data volume is shared by every AppHost instance; two at once destroy the cluster. Pinned, the second run dies at bind time with a clear error instead |
| `pgvector/pgvector:pg17` | the platform's RAG migration creates a vector column at startup |
| Tika is `WithExplicitStart()` and **unproxied** | DCP binds proxy ports eagerly even for explicit-start resources, so a proxied 9998 accepts and never answers — hanging every OCR call *and* shadowing any Tika you run by hand |
| `Cors__Origins__N` indices are **gapless** | `IConfiguration` binds them as an array and stops at the first missing index |

### Mode B — headless (scripted verification, CI, no dashboard)

```powershell
dotnet build Networthy.slnx
docker rm -f networthy-pg-test 2>$null
docker run -d --name networthy-pg-test -e POSTGRES_PASSWORD=postgres -p 5432:5432 pgvector/pgvector:pg17

$bin = "src/Networthy.Host/bin/Debug/net10.0"
$pg  = "Host=127.0.0.1;Port=5432;Database={0};Username=postgres;Password=postgres"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$api = Start-Process dotnet -WorkingDirectory $bin -PassThru -ArgumentList @(
  "$PWD/$bin/Networthy.Host.dll",
  "--ConnectionStrings:plenipo-platform=$($pg -f 'networthy_platform')",
  "--ConnectionStrings:plenipo-audit=$($pg -f 'networthy_audit')",
  "--urls=http://127.0.0.1:8094")

1..60 | ForEach-Object { Start-Sleep 2; try { if ((iwr http://127.0.0.1:8094/alive -UseBasicParsing).StatusCode -eq 200) { "ready"; break } } catch {} }

# ... exercise it (§4) ...

Stop-Process -Id $api.Id -Force; docker rm -f networthy-pg-test
```

> **The gotcha that costs an hour:** `Start-Process` must set **`-WorkingDirectory` to the bin
> folder**. Otherwise ASP.NET's ContentRoot never finds `appsettings.Development.json`, the chat
> provider silently falls back to `None`, and every turn answers
> `RUN_ERROR "AI provider is not configured"`. Or pass `--Ai:Provider=Mock` explicitly.

### Mode C — the released image (what a self-hoster runs)

```bash
docker compose up -d          # → http://localhost:8080
```

Verifies the **shipping artifact**: the GHCR image with the branded UI embedded in `wwwroot/`. A
change that works under Mode A but not Mode C is usually a UI-embedding or migration-on-startup
problem, not a domain bug.

### Ready signals

| Signal | Meaning |
|---|---|
| `GET /alive` → 200 | process up. **Never calls the LLM** — safe to poll |
| `GET /health` → 200 | dependencies reachable |
| `GET /api/platform/modules` contains `finance` | the module loaded and its manifest parsed |

## 3. Dev authentication

Development with no IdP configured uses the dev-auth fallback. Send these on **every** call:

```http
X-Dev-Subject: dev-user
X-Dev-Tenant:  dev
X-Dev-Roles:   system_admin
```

`system_admin` holds `*`. Networthy's own roles are **`household-admin`** and **`household-member`**
(a household is a tenant). To test RBAC, send the narrower role and assert the 403 — that is the
point of the header being per-request.

## 4. Exercise it

### A chat turn over AG-UI

```powershell
$h = @{ "X-Dev-Subject"="dev-user"; "X-Dev-Tenant"="dev"; "X-Dev-Roles"="system_admin" }
$body = @{ messages = @(@{ id="m1"; role="user"; content="List our accounts" }) } | ConvertTo-Json
$r = Invoke-WebRequest "$base/api/agui/finance" -Method Post -Headers $h `
       -ContentType "application/json" -Body $body -UseBasicParsing
($r.Content -split "`n") | Where-Object { $_ -like "data:*" }
```

A healthy turn streams:

```text
RUN_STARTED → TEXT_MESSAGE_START → TEXT_MESSAGE_CONTENT (many) →
CUSTOM(token_usage) → TEXT_MESSAGE_END → RUN_FINISHED
```

A tool call adds `TOOL_CALL_START`/`TOOL_CALL_END`. An **approval-gated** tool emits
`CUSTOM(approval_required)` and the reply must *not* claim the write happened. `RUN_ERROR` is always
a failure — usually the Mode B WorkingDirectory trap, or a module id other than `finance`.

### How the Mock provider behaves — this shapes every chat-driven test

Verified empirically against this repo:

- **It selects a tool by name-token match against your message.** `"Create a checking account…"`
  reaches `create_account`; `"Log own transaction 4.25 coffee"` reaches `log_own_transaction`. A
  prompt that does not contain the tool's name tokens routes nowhere and you get no
  `TOOL_CALL_START`.
- **It fills the first string parameter with the whole user message**, and any other required string
  it cannot infer with the literal `"example"`. So `create_account` arrives as
  `{ "name": "Create a checking account called …", "type": "example" }` — and the tool correctly
  rejects `example`, so **no account is created**.
- Therefore **"a row appeared" is not an available assertion in a Mock-driven chat test.** Assert
  the platform contract instead: routing, gating, the approval round trip, the audit record.

### Approvals

```powershell
Invoke-RestMethod "$base/api/chat/approvals" -Headers $h                       # pending
Invoke-RestMethod "$base/api/chat/approvals/$id/approve" -Method Post -Headers $h
Invoke-RestMethod "$base/api/chat/approvals/$id/reject"  -Method Post -Headers $h
```

Approve **re-executes the exact recorded call** and returns `{ status: "Executed", result }`, where
`result` is the tool's own return value. Reject returns `status: "Rejected"` and never invokes the
executor.

> **The approval gate is the union of two flags.** A tool is gated if `RequiresApproval = true` on
> *either* its `ToolDescriptor` in `FinanceModule` **or** its `ModuleTool` in `FinanceToolSource` —
> `AuthorizedAgentRunner` unions both sets. Flipping only one leaves the gate intact, so reviewing
> only one place can mislead you. Set both, and keep them in sync.

### Admin, RBAC, audit, usage

```powershell
Invoke-RestMethod "$base/api/platform/modules"       -Headers $h
Invoke-RestMethod "$base/api/platform/me"            -Headers $h
Invoke-RestMethod "$base/api/admin/security/catalog" -Headers $h   # every tool + its permission
Invoke-RestMethod "$base/api/admin/audit/tool-calls" -Headers $h   # every invocation, append-only
Invoke-RestMethod "$base/api/admin/usage?days=30"    -Headers $h   # populated only after a chat turn
Invoke-RestMethod "$base/api/finance/accounts"       -Headers $h
Invoke-RestMethod "$base/api/finance/transactions"   -Headers $h
```

After adding a tool, `security/catalog` **must** list it with its permission. If it does not, the
manifest and the tool source disagree and the tool will never be callable.

**Reading the audit log.** A blocked call is recorded too, carrying
`error = "Blocked: tool requires human approval"`. So a plain count of a tool's entries does not
distinguish *intercepted* from *executed* — filter on `success == true && error == null` for real
executions. Note that approval **re-execution is recorded on the approval record, not as a second
tool-call audit entry** (`ApprovalExecutor` does not write to the audit log).

### The UI

Under Mode A the AppHost launches `networthy-ui` on the pinned port **5173** and injects
`VITE_API_BASE` plus the CORS origins. Standalone:

```powershell
corepack enable
pnpm -C frontend/networthy-ui install
$env:VITE_API_BASE = "$base"
pnpm -C frontend/networthy-ui dev
```

The admin console is served at `/admin` from the API's committed `wwwroot/admin` unless a sibling
Plenipo checkout is present, in which case the AppHost runs it as its own dev server.

## 5. Observe

The **Aspire dashboard** shows console logs, structured logs, distributed traces, and metrics per
resource. A trace shows the tool call, the approval interception, and the DB round-trips on one
timeline — read it before reading source.

The **Aspire MCP/CLI** is the agent-readable view of the same OpenTelemetry. When it reports "No
Aspire AppHost is currently running":

| Cause | Fix |
|---|---|
| started with `dotnet run` | relaunch with **`aspire run`** — only the CLI opens the backchannel |
| CLI/AppHost SDK version mismatch | update the CLI from the official installer |
| stale zero-byte `~/.aspire/cli/backchannels/aux.sock.*` | delete them |
| just started | discovery is push-based; wait a few seconds |

Resources named `*-installer` (run to completion) and `*-rebuilder` (stay `NotStarted`) are helpers,
not failures.

## 6. The test ladder

| Rung | What it proves | Command |
|---|---|---|
| **1. Build** | it compiles | `dotnet build Networthy.slnx` |
| **2. Unit / module guard** | domain logic, manifest integrity, the pinned tool list | `dotnet test tests/Networthy.Finance.Tests` |
| **3. Integration (E2E)** | the real host, real Postgres, real migrations, real jobs, real approvals | `dotnet test tests/Networthy.IntegrationTests` |
| **4. Golden evals** | agent *behaviour*: routing, gating, protocol | **not installed yet** — see §9 |
| **5. Frontend** | the UI builds and its units pass | `pnpm -C frontend/networthy-ui test && pnpm -C frontend/networthy-ui build` |

Everything at once, as CI runs it:

```bash
dotnet build Networthy.slnx -c Release
dotnet test  Networthy.slnx -c Release
```

**Without Docker**, rung 3 cannot run. Skip it explicitly rather than staring at a wall of red — and
say in your report that you skipped it:

```bash
dotnet test Networthy.slnx --filter "FullyQualifiedName!~IntegrationTests"
```

### Rung 3 — how the E2E host is built

`tests/Networthy.IntegrationTests/IntegrationFixture.cs` boots the **real** `Networthy.Host` via
`WebApplicationFactory<Program>` against a **Testcontainers** Postgres. Platform *and* finance
migrations run, the dev tenant and category taxonomy seed, hosted services start. The **Mock AI
provider is the only stand-in**.

Two entry points, and the choice is load-bearing:

- **`fixture.AdminClient()`** — an `HttpClient` with dev-auth headers, through the *real* pipeline.
  The **only** way to prove RBAC, the approval gate, or the AG-UI protocol. *Prefer it.*
- **`fixture.AuthorizedScopeAsync()`** — a DI scope with tenant/user/permissions set, so you can
  resolve tool classes and call them directly. Deliberately **bypasses** RBAC and the approval gate,
  so it can never prove either.

> A test asserting "this write is approval-gated" that runs through `AuthorizedScopeAsync()` will
> pass while the gate is broken. `ChatAndApprovalTests` is the suite that actually proves the gate,
> and it goes through `AdminClient()` throughout.

## 7. The verification loop

A change is done when a test that **fails without it** passes with it.

1. **Reproduce** through the narrowest surface that still shows it: an API request, an AG-UI turn, a
   UI click. Write down the exact input and the exact wrong output.
2. **Observe** the trace and logs — not the source. Find the first point where reality diverges.
3. **Diagnose** in one sentence. If you can't, you're still at step 2.
4. **Fix** — the smallest change addressing that cause, one variable at a time.
5. **Lock in** at the lowest rung that would have caught it. Run it against the **unfixed** code and
   watch it fail first. *A regression test never seen red is not a regression test* — and make sure
   you broke the thing that actually enforces the behaviour (see the two-flag note in §4).
6. **Re-run** the ladder as far as the change reaches.

Escalate instead of looping if the same rung fails three times for three different reasons — that
means the diagnosis is wrong, not the fix.

## 8. Gotchas

| Symptom | Cause / fix |
|---|---|
| `RUN_ERROR "AI provider is not configured"` | ContentRoot didn't load dev appsettings — set `-WorkingDirectory` to the bin folder, or pass `--Ai:Provider=Mock` |
| `RUN_ERROR "Unknown module"` | the module id is `finance` |
| No `TOOL_CALL_START` in a chat test | the Mock routes by name token — your prompt must contain the tool's name words |
| A chat-driven write "didn't happen" | the Mock filled a required string with `"example"` and the tool rejected it. Assert the platform contract, not the row |
| Aspire: containers up, API never starts, stack hangs after the banner | stale Postgres data volume with a different baked-in password. `docker volume ls`, then `docker volume rm <name>` — dev data is throwaway |
| Corrupted data after running two AppHosts | both mounted the same volume; the host port is pinned so the second now fails fast — don't unpin it |
| Migration fails on the `vector` type | the image must be **pgvector**, not stock `postgres` |
| `DLL is locked by .NET Host` on rebuild | a previous API process is still running — stop it first |
| Admin/usage endpoints empty | token usage exists only after a chat turn |
| New tool never called, no error | missing from the manifest **or** from `FinanceToolSource` — both are required, and `security/catalog` shows the gap |
| Tool 403s for `system_admin` | manifest and tool source disagree on the permission string — use `Permissions.ForTool(Id, name)` in both |
| UI reaches the API under Aspire but not standalone | `Cors:Origins` indices must be **gapless** |

## 9. Known gaps in this harness

Recorded rather than hidden, so the next agent doesn't mistake absence for coverage:

- **No golden-conversation evals (rung 4).** The platform has an eval runner
  (`GoldenConversationEvals` + `Evals/cases/*.json`) that gives prompt-shaped changes — agent
  instructions, tool descriptions, agent profiles — the same regression net code has. Networthy has
  neither the runner nor the cases. Porting it is the highest-value next step for this repo.
- **No `.mcp.json`.** The Aspire MCP has to be started by hand rather than being registered for the
  session.
- **No committed `.http` request catalog.** The platform ships `plenipo.http`; this repo has no
  equivalent, so every endpoint has to be reconstructed from source.
- **No Playwright smoke.** Nothing catches a stale CSP hash, which no backend test can see.
- **Postgres major drifts between run and test.** The AppHost and Mode B use `pg17`; the test fixture
  pins `pg16`. They should match — a product should not test against a Postgres it does not ship.

## 10. CI

`.github/workflows/ci.yml` gates every PR: restore → **vulnerability audit**
(`dotnet list package --vulnerable --include-transitive`, failing on any hit) → Release build →
Release test. Docker is available on the runner, so rung 3 runs there too.

Green CI is the floor, not the proof. CI cannot tell you the feature does what was asked — only §7
can.
