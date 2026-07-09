<div align="center">

# 💰 Networthy

**The free, open-source AI-first personal finance assistant — for households that want the AI's help without giving up control.**

Statement upload · Budgets · Net worth · Household sharing · Bank linking (opt-in) · Chat-first

*Free to use. MIT licensed. Every AI action approval-gated and audited.*

</div>

---

## What it does

| You say | It does |
|---|---|
| *"Here's my October statement"* (attach the PDF/CSV) | Extracts every line item, suggests categories, and waits for **your review** before anything posts |
| *"How much did we spend on groceries last month?"* | A real, computed answer from your own transactions — never a made-up number |
| *"Set the dining budget to $400"* | Sets it — after you approve; over-budget flags follow automatically |
| *"Can I afford a $200 dinner this week?"* | A direct answer from what's left in the relevant budgets, not a lecture |
| *"Log $6.50 coffee"* | Captured instantly — your own spending entries are the one thing that never waits on approval |

The household's data is shared with the household — members see what the admin scopes to them,
and net worth trends across every account.

## Why it's different

Every AI-native finance app (Monarch, Copilot, Cleo) is closed-source SaaS that requires handing
over your bank credentials. Every open-source one (Firefly III, Actual Budget) deliberately
avoids AI. Networthy is the intersection nothing else occupies:

- **Open source and self-hostable** — your financial data lives where you decide.
- **Upload-first, bank-linking optional** — a statement PDF needs no credentials; Plaid is
  opt-in, never required.
- **AI actions are approval-gated** — an AI-suggested categorization or import never becomes
  financial fact until a human says so, and everything it does is in an append-only audit log.
- **A household is a tenant** — real multi-user sharing with per-member visibility, on the same
  RBAC that gates every tool the AI can call.

Built on [Cortex](https://github.com/abrahamFerga/Cortex), the open-source AI-first platform —
auth, multi-tenancy, RBAC-before-the-model, approvals, audit, jobs, and chat channels come from
the platform; this repo is the finance domain and nothing else.

## Quick start

```powershell
# Prereqs: .NET 10 SDK, Docker Desktop, pnpm (for the dev UI), a sibling Cortex checkout
git clone https://github.com/abrahamFerga/Cortex ../Cortex
dotnet run --project src/Networthy.AppHost   # Aspire: Postgres + Redis + API + branded UI
```

Zero configuration required — the assistant runs on the built-in Mock provider until you add a
real AI key (`dotnet user-secrets --project src/Networthy.AppHost set "Parameters:ai-provider" "OpenAI"` …).

## Repo layout

```text
src/Networthy.Host/             the product: a thin host on Cortex platform packages
src/Networthy.Finance/          the finance domain module (accounts, transactions, budgets, …)
src/Networthy.Connectors.Plaid/ the product-owned Plaid connector (ADR-0007)
src/Networthy.AppHost/          Aspire local orchestration
tests/                          module guard + domain-logic tests
```

Design history: [SPEC.md](SPEC.md) · [PLAN.md](PLAN.md) · [ARCH.md](ARCH.md) ·
[DECISIONS.md](DECISIONS.md) · [research/](research/)

## License

MIT — see [LICENSE](LICENSE). Not financial advice: Networthy organizes and reports your own
data; it does not recommend investments.
