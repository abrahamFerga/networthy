import { useQuery } from "@tanstack/react-query";
import { apiGet, GenericTab, type ModuleTabProps } from "@plenipo/ui";

/**
 * The Recurring tab (issue #46, ADR-0008): the next 60 days of expected bills on a month-grid
 * calendar, ABOVE the generic detection table the server already declares (same detection,
 * two readings). The grid is a real `<table>` — weekday column headers, a caption per month,
 * bills as plain text inside day cells, today carrying `aria-current` plus a visible mark —
 * so a screen reader walks it as data, not as decoration.
 */

interface UpcomingBills {
  today: string;
  until: string;
  currencyCode: string;
  bills: { name: string; dueOn: string; amount: number }[];
}

const WEEKDAYS = [
  { short: "Sun", long: "Sunday" },
  { short: "Mon", long: "Monday" },
  { short: "Tue", long: "Tuesday" },
  { short: "Wed", long: "Wednesday" },
  { short: "Thu", long: "Thursday" },
  { short: "Fri", long: "Friday" },
  { short: "Sat", long: "Saturday" },
] as const;

const money = (value: number, currency: string) =>
  value.toLocaleString(undefined, { style: "currency", currency, maximumFractionDigits: 2 });

/** "yyyy-MM-dd" → numbers, without Date's UTC-vs-local parsing ambiguity. */
function parseIso(iso: string): { year: number; month: number; day: number } {
  const [year, month, day] = iso.split("-").map(Number);
  return { year, month, day };
}

/** Every (year, month) from the month of `fromIso` through the month of `toIso`, inclusive. */
function monthsSpanned(fromIso: string, toIso: string): { year: number; month: number }[] {
  const from = parseIso(fromIso);
  const to = parseIso(toIso);
  const months: { year: number; month: number }[] = [];
  for (let y = from.year, m = from.month; y < to.year || (y === to.year && m <= to.month); ) {
    months.push({ year: y, month: m });
    m += 1;
    if (m === 13) {
      m = 1;
      y += 1;
    }
  }
  return months;
}

/** A month as Sunday-first weeks of day numbers, null-padded at the edges. */
function monthWeeks(year: number, month: number): (number | null)[][] {
  const leadingBlanks = new Date(year, month - 1, 1).getDay(); // 0 = Sunday
  const dayCount = new Date(year, month, 0).getDate();
  const cells: (number | null)[] = [
    ...Array.from({ length: leadingBlanks }, () => null),
    ...Array.from({ length: dayCount }, (_, i) => i + 1),
  ];
  while (cells.length % 7 !== 0) cells.push(null);
  return Array.from({ length: cells.length / 7 }, (_, w) => cells.slice(w * 7, w * 7 + 7));
}

const MONTH_LABELS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
] as const;

function BillsCalendar({ payload }: { payload: UpcomingBills }) {
  if (payload.bills.length === 0) {
    return (
      <p className="text-sm text-slate-400 dark:text-slate-500">
        Nothing due in the next 60 days — detected recurring charges appear here as they're recognized.
      </p>
    );
  }

  const today = parseIso(payload.today);
  const byDate = new Map<string, UpcomingBills["bills"]>();
  for (const bill of payload.bills) {
    byDate.set(bill.dueOn, [...(byDate.get(bill.dueOn) ?? []), bill]);
  }

  return (
    <div className="space-y-6">
      {monthsSpanned(payload.today, payload.until).map(({ year, month }) => (
        <table key={`${year}-${month}`} className="w-full table-fixed border-collapse text-sm">
          <caption className="mb-2 text-left text-sm font-semibold text-slate-900 dark:text-slate-100">
            {MONTH_LABELS[month - 1]} {year}
          </caption>
          <thead>
            <tr>
              {WEEKDAYS.map((d) => (
                <th
                  key={d.short}
                  scope="col"
                  className="border border-slate-200 px-1 py-1 text-center text-xs font-medium text-slate-500 dark:border-slate-700 dark:text-slate-400"
                >
                  <abbr title={d.long} className="no-underline">
                    {d.short}
                  </abbr>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {monthWeeks(year, month).map((week, w) => (
              <tr key={w}>
                {week.map((day, i) => {
                  if (day === null) {
                    return <td key={i} className="border border-slate-200 dark:border-slate-700" />;
                  }
                  const iso = `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
                  const isToday = year === today.year && month === today.month && day === today.day;
                  return (
                    <td
                      key={i}
                      aria-current={isToday ? "date" : undefined}
                      className="h-14 border border-slate-200 p-1 align-top dark:border-slate-700"
                    >
                      <span
                        className={`inline-flex h-5 w-5 items-center justify-center rounded-full text-xs ${
                          isToday
                            ? "bg-brand-600 font-semibold text-white"
                            : "text-slate-500 dark:text-slate-400"
                        }`}
                      >
                        {day}
                        {isToday && <span className="sr-only"> (today)</span>}
                      </span>
                      {(byDate.get(iso) ?? []).map((bill, b) => (
                        <span
                          key={b}
                          className="block truncate text-xs font-medium text-slate-900 dark:text-slate-100"
                          title={`${bill.name} — ${money(bill.amount, payload.currencyCode)}`}
                        >
                          {bill.name} {money(bill.amount, payload.currencyCode)}
                        </span>
                      ))}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      ))}
    </div>
  );
}

export function RecurringTab({ tab }: ModuleTabProps) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["finance-upcoming-bills"],
    queryFn: () => apiGet<UpcomingBills>("/api/finance/bills/upcoming"),
  });

  return (
    <div className="space-y-4">
      <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
        <h2 className="mb-3 text-sm font-semibold text-slate-900 dark:text-slate-100">
          Upcoming bills (next 60 days)
        </h2>
        {isLoading ? (
          <p className="text-sm text-slate-500">Loading…</p>
        ) : isError ? (
          <p className="text-sm text-red-600">{(error as Error).message}</p>
        ) : (
          <BillsCalendar payload={data!} />
        )}
      </section>
      {/* The server-declared detection table keeps its columns and placeholder untouched. */}
      <GenericTab tab={tab} />
    </div>
  );
}
