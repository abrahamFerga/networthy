import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { GenericTab, type ModuleTabProps } from "@plenipo/ui";

/**
 * The Transactions tab (SPEC v2.1, same ADR-0008 shape as SpendingTab): the shell's
 * server-driven ledger table pointed at a selectable account. This component owns exactly ONE
 * thing — which account the dataEndpoint is asked for ("" = all accounts). Rendering stays
 * GenericTab's, so the table, its editor, and the "Link detected transfers" action can never
 * drift from the shell; the account filter itself is applied server-side (`?account=`), so the
 * row caps never hide a quiet account's history.
 */

type AccountOption = { id: string; name: string };

export function TransactionsTab({ tab }: ModuleTabProps) {
  const [account, setAccount] = useState("");
  const endpoint = tab.dataEndpoint ?? "/api/finance/transactions";
  // The same visibility-scoped list the Accounts tab renders — a member never sees (or filters
  // by) another member's private account.
  const { data: accounts } = useQuery<AccountOption[]>({
    queryKey: ["finance", "accounts", "transactions-picker"],
    queryFn: async () => {
      const res = await fetch("/api/finance/accounts");
      return res.ok ? ((await res.json()) as AccountOption[]) : [];
    },
  });
  const scopedTab =
    account === "" ? tab : { ...tab, dataEndpoint: `${endpoint}?account=${encodeURIComponent(account)}` };

  return (
    <div>
      <div className="mb-2 flex items-center justify-end gap-2">
        <label
          htmlFor="transactions-account"
          className="text-sm font-medium text-slate-700 dark:text-slate-200"
        >
          Account
        </label>
        <select
          id="transactions-account"
          value={account}
          onChange={(e) => setAccount(e.target.value)}
          className="focus-ring rounded border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700 dark:border-slate-600 dark:bg-slate-900 dark:text-slate-200"
        >
          <option value="">All accounts</option>
          {(accounts ?? []).map((a) => (
            <option key={a.id} value={a.name}>
              {a.name}
            </option>
          ))}
        </select>
      </div>
      <GenericTab tab={scopedTab} />
    </div>
  );
}
