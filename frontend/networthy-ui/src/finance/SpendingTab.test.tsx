// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SpendingTab } from "./SpendingTab";
import type { ModuleTabProps } from "@plenipo/ui";

const tab: ModuleTabProps["tab"] = {
  id: "spending",
  label: "Spending",
  route: "/finance/spending",
  dataEndpoint: "/api/finance/spending",
  chart: { kind: "donut", xField: "category", yField: "amount", yLabel: "Spent" },
};

/** Routes fetch by URL substring — first matching key wins, so put specific paths first. */
function stubFetch(routes: Record<string, unknown>) {
  const mock = vi.fn().mockImplementation((input: unknown) => {
    const url = String(input);
    const match = Object.entries(routes).find(([path]) => url.includes(path));
    return Promise.resolve({
      ok: match !== undefined,
      status: match ? 200 : 404,
      json: () => Promise.resolve(match ? match[1] : { error: "not stubbed" }),
    } as unknown as Response);
  });
  vi.stubGlobal("fetch", mock);
  return mock;
}

function renderSpending(routes: Record<string, unknown>) {
  const mock = stubFetch(routes);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SpendingTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
  return mock;
}

/** The same yyyy-MM the component derives for N months before the browser's current one. */
function monthValue(monthsBack: number): string {
  const now = new Date();
  const d = new Date(now.getFullYear(), now.getMonth() - monthsBack, 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

describe("SpendingTab (category breakdown for a selected period)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders the shell donut for the household's current month by default, tail capped into Other", async () => {
    const mock = renderSpending({
      "/api/finance/spending": [
        { category: "Groceries", amount: 300 },
        { category: "Dining", amount: 200 },
        { category: "Transportation", amount: 100 },
        { category: "Health", amount: 80 },
        { category: "Entertainment", amount: 60 },
        { category: "Shopping", amount: 40 },
      ],
    });

    // Named segments up to the palette; the rest rolls into "Other" (shell behavior we rely on).
    expect(await screen.findByText("Groceries")).toBeTruthy();
    expect(screen.getByText("Other")).toBeTruthy();
    // The default fetch carries NO month param — the server's household month decides.
    expect(String(mock.mock.calls[0][0])).not.toContain("month=");
    expect((screen.getByLabelText("Month") as HTMLSelectElement).value).toBe("");
  });

  it("re-fetches the endpoint with ?month= when a previous month is selected", async () => {
    const lastMonth = monthValue(1);
    const mock = renderSpending({
      [`month=${lastMonth}`]: [{ category: "Rent", amount: 900 }],
      "/api/finance/spending": [{ category: "Groceries", amount: 300 }],
    });
    await screen.findByText("Groceries");

    fireEvent.change(screen.getByLabelText("Month"), { target: { value: lastMonth } });

    expect(await screen.findByText("Rent")).toBeTruthy();
    const urls = mock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes(`/api/finance/spending?month=${lastMonth}`))).toBe(true);
  });

  it("shows the donut's empty state when the selected month has no expenses", async () => {
    renderSpending({ "/api/finance/spending": [] });

    expect(await screen.findByText(/No data points yet/)).toBeTruthy();
  });
});
