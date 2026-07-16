// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { OverviewTab } from "./OverviewTab";
import type { ModuleTabProps } from "@plenipo/ui";

const tab: ModuleTabProps["tab"] = {
  id: "overview",
  label: "Overview",
  route: "/finance/overview",
  dataEndpoint: "/api/finance/overview",
};

const payload = {
  asOf: "2026-07-11",
  currencyCode: "USD",
  safeToSpend: {
    amount: 340.5,
    currencyCode: "USD",
    month: "2026-07",
    budgetCount: 3,
    totalTarget: 900,
    totalSpent: 559.5,
  },
  netWorth: { total: 12400, currencyCode: "USD", trend: [11000, 11800, 12400] },
  budgets: [
    { categoryName: "Groceries", spent: 150, target: 400, currencyCode: "USD" },
    { categoryName: "Dining", spent: 260, target: 200, currencyCode: "USD" },
  ],
  upcomingBills: [{ name: "Netflix", expectedOn: "2026-07-15", amount: 15.99 }],
  recentTransactions: [
    { occurredOn: "2026-07-10", description: "Coffee", amount: 6.5, currencyCode: "USD", direction: "expense" },
    { occurredOn: "2026-07-09", description: "Paycheck", amount: 2500, currencyCode: "USD", direction: "income" },
  ],
  goals: [{ name: "Emergency fund", saved: 3400, target: 5000, currencyCode: "USD" }],
};

function renderOverview(body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(body) } as unknown as Response),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <OverviewTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
}

describe("OverviewTab (household command center)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders every dashboard section from the one composed payload", async () => {
    renderOverview(payload);

    // Hero + summary tiles.
    expect(await screen.findByText("Safe to spend")).toBeTruthy();
    expect(screen.getByText("$340.50")).toBeTruthy();
    expect(screen.getByText("$12,400.00")).toBeTruthy();
    // Budget bars with explicit over/left text — never color alone.
    expect(screen.getByText("Groceries")).toBeTruthy();
    expect(screen.getByText(/Over by \$60\.00/)).toBeTruthy();
    // Bills, activity (income signed positive), goals.
    expect(screen.getByText("Netflix")).toBeTruthy();
    expect(screen.getByText(/\+\$2,500\.00/)).toBeTruthy();
    expect(screen.getByText("Emergency fund")).toBeTruthy();
  });

  it("renders guidance, not a fabricated zero, when no budgets exist", async () => {
    renderOverview({
      ...payload,
      safeToSpend: null,
      budgets: [],
    });

    expect(await screen.findByText("Safe to spend")).toBeTruthy();
    expect(screen.getByText(/Set a budget and this becomes a real number/)).toBeTruthy();
    expect(screen.getByText(/No budgets for this month yet/)).toBeTruthy();
  });

  it("keeps a fully-funded goal reading as success, never as the over-budget alarm", async () => {
    renderOverview({
      ...payload,
      goals: [{ name: "Vacation", saved: 6000, target: 5000, currencyCode: "USD" }],
    });

    await screen.findByText("Vacation");
    const statuses = screen.getAllByTestId("progress-status");
    const status = statuses[statuses.length - 1];
    expect(status.textContent).toContain("$6,000.00 of $5,000.00");
    expect(status.className).not.toContain("red");
  });
});
