import { useMemo, useState } from "react";
import { GenericTab, type ModuleTabProps } from "@cortex/ui";

/**
 * The Spending tab (issue #46, ADR-0008): the shell's server-driven donut — capped segments,
 * "Other" roll-up, directly-labeled legend — pointed at a selectable month. This component
 * owns exactly ONE thing: which month the dataEndpoint is asked for. Rendering stays
 * GenericTab's, so the donut here can never drift from the shell's other charts.
 */

/** The `count` months before the browser's current one, newest first, as yyyy-MM + label. */
function previousMonths(count: number): { value: string; label: string }[] {
  const now = new Date();
  return Array.from({ length: count }, (_, i) => {
    const d = new Date(now.getFullYear(), now.getMonth() - (i + 1), 1);
    return {
      value: `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`,
      label: d.toLocaleDateString(undefined, { month: "long", year: "numeric" }),
    };
  });
}

export function SpendingTab({ tab }: ModuleTabProps) {
  // "" = the household's current month (the endpoint's default) — deliberately NOT the
  // browser's month, which can disagree with the household's time zone around midnight.
  const [month, setMonth] = useState("");
  const months = useMemo(() => previousMonths(11), []);
  const endpoint = tab.dataEndpoint ?? "/api/finance/spending";
  const monthTab = month === "" ? tab : { ...tab, dataEndpoint: `${endpoint}?month=${month}` };

  return (
    <div>
      <div className="mb-2 flex items-center justify-end gap-2">
        <label htmlFor="spending-month" className="text-sm font-medium text-slate-700 dark:text-slate-200">
          Month
        </label>
        <select
          id="spending-month"
          value={month}
          onChange={(e) => setMonth(e.target.value)}
          className="focus-ring rounded border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700 dark:border-slate-600 dark:bg-slate-900 dark:text-slate-200"
        >
          <option value="">This month</option>
          {months.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </select>
      </div>
      <GenericTab tab={monthTab} />
    </div>
  );
}
