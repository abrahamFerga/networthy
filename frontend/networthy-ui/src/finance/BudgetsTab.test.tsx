// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BudgetsTab } from "./BudgetsTab";
import type { ModuleTabProps } from "@cortex/ui";

// The budgets tab as the server declares it — including the editor, whose affordances the
// custom component must NOT lose by composing GenericTab underneath the bars.
const tab: ModuleTabProps["tab"] = {
  id: "budgets",
  label: "Budgets",
  route: "/finance/budgets",
  dataEndpoint: "/api/finance/budgets",
  columns: [
    { field: "categoryName", header: "Category" },
    { field: "spent", header: "Spent" },
    { field: "target", header: "Target" },
    { field: "status", header: "Status" },
  ],
  placeholder: "No budgets for this month.",
  editor: {
    upsertEndpoint: "/api/finance/budgets",
    deleteEndpoint: "/api/finance/budgets/{id}",
    keyField: "categoryName",
    fields: [
      { field: "categoryName", label: "Category" },
      { field: "target", label: "Monthly target", numeric: true },
    ],
  },
};

const rows = [
  { id: "b1", categoryName: "Groceries", spent: 150, target: 400, remaining: 250, currencyCode: "USD", status: "on track" },
  { id: "b2", categoryName: "Dining", spent: 260, target: 200, remaining: -60, currencyCode: "USD", status: "OVER" },
];

function renderBudgets(body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(body) } as unknown as Response),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BudgetsTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
}

describe("BudgetsTab (progress bars above the generic editor table)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders one progress bar per budget, with explicit left/over text — never color alone", async () => {
    renderBudgets(rows);

    expect(await screen.findByText("Progress this month")).toBeTruthy();
    const statuses = screen.getAllByTestId("progress-status").map((s) => s.textContent);
    expect(statuses.some((s) => s?.includes("$250.00 left"))).toBe(true);
    expect(statuses.some((s) => s?.includes("Over by $60.00"))).toBe(true);
    expect(screen.getAllByRole("progressbar")).toHaveLength(2);
  });

  it("keeps the generic table's editor affordances underneath the bars", async () => {
    renderBudgets(rows);

    // Same fetch feeds both: the table renders the rows, and Add/Edit/Delete survive.
    expect(await screen.findByRole("button", { name: "Add" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Edit" })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "Delete" })).toHaveLength(2);
    expect(screen.getAllByText("Groceries").length).toBeGreaterThanOrEqual(2); // bar label + table cell
  });

  it("shows no bars and the tab's own placeholder when no budgets exist", async () => {
    renderBudgets([]);

    expect(await screen.findByText("No budgets for this month.")).toBeTruthy();
    expect(screen.queryByText("Progress this month")).toBeNull();
    expect(screen.queryAllByRole("progressbar")).toHaveLength(0);
  });
});
