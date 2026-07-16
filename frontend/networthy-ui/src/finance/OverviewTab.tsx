import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { apiGet, ProgressBar, StatTile, type ModuleTabProps } from "@plenipo/ui";

/**
 * The household command center (epic 8, ADR-0008) — one composed fetch, rendered with the
 * platform's dashboard primitives. Summary at the top (safe-to-spend hero, net worth with its
 * trend, month spending), working detail underneath (budget progress, upcoming bills, recent
 * activity, goals). Every number comes from the server payload; nothing is derived client-side,
 * so the dashboard can never disagree with the tabs (or the assistant) it summarizes.
 */

interface Overview {
  asOf: string;
  currencyCode: string;
  safeToSpend: {
    amount: number;
    currencyCode: string;
    month: string;
    budgetCount: number;
    totalTarget: number;
    totalSpent: number;
  } | null;
  netWorth: { total: number; currencyCode: string; trend: number[] };
  budgets: { categoryName: string; spent: number; target: number; currencyCode: string }[];
  upcomingBills: { name: string; expectedOn: string; amount: number }[];
  recentTransactions: {
    occurredOn: string;
    description: string;
    amount: number;
    currencyCode: string;
    direction: string;
  }[];
  goals: { name: string; saved: number | null; target: number; currencyCode: string }[];
}

const money = (value: number, currency: string) =>
  value.toLocaleString(undefined, { style: "currency", currency, maximumFractionDigits: 2 });

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <h2 className="mb-3 text-sm font-semibold text-slate-900 dark:text-slate-100">{title}</h2>
      {children}
    </section>
  );
}

const EmptyNote = ({ children }: { children: ReactNode }) => (
  <p className="text-sm text-slate-400 dark:text-slate-500">{children}</p>
);

export function OverviewTab({ tab }: ModuleTabProps) {
  const endpoint = tab.dataEndpoint ?? "/api/finance/overview";
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["finance-overview", endpoint],
    queryFn: () => apiGet<Overview>(endpoint),
  });

  if (isLoading) return <p className="text-sm text-slate-500">Loading…</p>;
  if (isError) return <p className="text-sm text-red-600">{(error as Error).message}</p>;

  const o = data!;
  const monthSpent = o.safeToSpend?.totalSpent;

  return (
    <div className="space-y-4">
      {/* Summary row: the hero number first, context beside it. */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {o.safeToSpend ? (
          <StatTile
            label="Safe to spend"
            value={money(o.safeToSpend.amount, o.safeToSpend.currencyCode)}
            caption={`left across ${o.safeToSpend.budgetCount} budget${o.safeToSpend.budgetCount === 1 ? "" : "s"} this month`}
          />
        ) : (
          <StatTile
            label="Safe to spend"
            value="—"
            caption="Set a budget and this becomes a real number, not a guess."
          />
        )}
        <StatTile
          label="Net worth"
          value={money(o.netWorth.total, o.netWorth.currencyCode)}
          caption={`as of ${o.asOf}`}
          trend={o.netWorth.trend}
        />
        <StatTile
          label="Spent this month"
          value={monthSpent != null ? money(monthSpent, o.currencyCode) : "—"}
          caption={monthSpent != null ? `of ${money(o.safeToSpend!.totalTarget, o.currencyCode)} budgeted` : "No budgets yet this month."}
        />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="Budgets this month">
          {o.budgets.length === 0 ? (
            <EmptyNote>No budgets for this month yet — ask the assistant to set one, or use the Budgets tab.</EmptyNote>
          ) : (
            <div className="space-y-3">
              {o.budgets.map((b) => (
                <ProgressBar
                  key={b.categoryName}
                  label={b.categoryName}
                  value={b.spent}
                  max={b.target}
                  text={
                    b.spent > b.target
                      ? `Over by ${money(b.spent - b.target, b.currencyCode)}`
                      : `${money(b.target - b.spent, b.currencyCode)} left`
                  }
                />
              ))}
            </div>
          )}
        </Section>

        <Section title="Upcoming bills">
          {o.upcomingBills.length === 0 ? (
            <EmptyNote>Nothing detected in the next few weeks — recurring charges appear here as they're recognized.</EmptyNote>
          ) : (
            <ul className="space-y-2">
              {o.upcomingBills.map((b) => (
                <li key={`${b.name}-${b.expectedOn}`} className="flex items-baseline justify-between gap-4 text-sm">
                  <span className="min-w-0 truncate text-slate-700 dark:text-slate-200">{b.name}</span>
                  <span className="flex shrink-0 items-baseline gap-3">
                    <span className="text-xs text-slate-400 dark:text-slate-500">{b.expectedOn}</span>
                    <span className="font-medium tabular-nums text-slate-900 dark:text-slate-100">
                      {money(b.amount, o.currencyCode)}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Section>

        <Section title="Recent activity">
          {o.recentTransactions.length === 0 ? (
            <EmptyNote>No transactions yet — upload a statement or log one in chat.</EmptyNote>
          ) : (
            <ul className="space-y-2">
              {o.recentTransactions.map((t, i) => (
                <li key={i} className="flex items-baseline justify-between gap-4 text-sm">
                  <span className="min-w-0 truncate text-slate-700 dark:text-slate-200">{t.description}</span>
                  <span className="flex shrink-0 items-baseline gap-3">
                    <span className="text-xs text-slate-400 dark:text-slate-500">{t.occurredOn}</span>
                    <span
                      className={`font-medium tabular-nums ${
                        t.direction === "income"
                          ? "text-emerald-700 dark:text-emerald-400"
                          : "text-slate-900 dark:text-slate-100"
                      }`}
                    >
                      {t.direction === "income" ? "+" : "−"}
                      {money(Math.abs(t.amount), t.currencyCode)}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Section>

        <Section title="Goals">
          {o.goals.length === 0 ? (
            <EmptyNote>No goals yet — tell the assistant what you're saving for.</EmptyNote>
          ) : (
            <div className="space-y-3">
              {o.goals.map((g) => (
                <ProgressBar
                  key={g.name}
                  label={g.name}
                  // Clamped + warnAt above 1: a goal bar only ever reads healthy — funding past
                  // target is success, not the budget bars' over-limit alarm. Text has the reals.
                  value={Math.min(g.saved ?? 0, g.target)}
                  max={g.target}
                  warnAt={2}
                  text={
                    g.saved == null
                      ? "(private account)"
                      : `${money(g.saved, g.currencyCode)} of ${money(g.target, g.currencyCode)}`
                  }
                />
              ))}
            </div>
          )}
        </Section>
      </div>
    </div>
  );
}
