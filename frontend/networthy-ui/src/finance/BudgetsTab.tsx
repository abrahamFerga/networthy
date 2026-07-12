import { useQuery } from "@tanstack/react-query";
import { apiGet, GenericTab, ProgressBar, type ModuleTabProps } from "@cortex/ui";

/**
 * The Budgets tab (issue #46, ADR-0008): every budgeted category as a ProgressBar — color plus
 * icon plus explicit "left"/"Over by" text, never color alone — ABOVE the generic editor table,
 * which keeps Add/Edit/Delete exactly as the server declared them. The bars read the same
 * endpoint rows the table renders (spent/target computed server-side, budgets-tab query), so
 * the two views cannot disagree.
 */

interface BudgetRow {
  id: string;
  categoryName: string;
  spent: number;
  target: number;
  remaining: number;
  currencyCode: string;
  status: string;
}

const money = (value: number, currency: string) =>
  value.toLocaleString(undefined, { style: "currency", currency, maximumFractionDigits: 2 });

export function BudgetsTab({ tab }: ModuleTabProps) {
  const endpoint = tab.dataEndpoint ?? "/api/finance/budgets";
  // Deliberately the SAME query key GenericTab's table uses: one fetch feeds bars and table,
  // and the generic editor's invalidation refreshes both after every add/edit/delete.
  const { data } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<BudgetRow[]>(endpoint),
  });
  const budgets = data ?? [];

  return (
    <div className="space-y-4">
      {budgets.length > 0 && (
        <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
          <h2 className="mb-3 text-sm font-semibold text-slate-900 dark:text-slate-100">Progress this month</h2>
          <div className="space-y-3">
            {budgets.map((b) => (
              <ProgressBar
                key={b.id}
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
        </section>
      )}
      <GenericTab tab={tab} />
    </div>
  );
}
