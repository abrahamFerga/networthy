// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RecurringTab } from "./RecurringTab";
import type { ModuleTabProps } from "@cortex/ui";

const tab: ModuleTabProps["tab"] = {
  id: "recurring",
  label: "Recurring",
  route: "/finance/recurring",
  dataEndpoint: "/api/finance/recurring",
  columns: [
    { field: "name", header: "Charge" },
    { field: "cadence", header: "Cadence" },
    { field: "nextExpected", header: "Next expected" },
  ],
  placeholder: "No recurring charges detected yet.",
};

const calendarPayload = {
  today: "2026-07-11",
  until: "2026-09-09",
  currencyCode: "USD",
  bills: [
    { name: "Netflix", dueOn: "2026-07-15", amount: 15.99 },
    { name: "Netflix", dueOn: "2026-08-15", amount: 15.99 },
    { name: "Gym", dueOn: "2026-07-15", amount: 60 },
  ],
};

const detectionRows = [
  { id: "NETFLIX", name: "Netflix", cadence: "monthly", nextExpected: "2026-07-15" },
];

/** Routes fetch by URL substring — first matching key wins. */
function stubFetch(routes: Record<string, unknown>) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation((input: unknown) => {
      const url = String(input);
      const match = Object.entries(routes).find(([path]) => url.includes(path));
      return Promise.resolve({
        ok: match !== undefined,
        status: match ? 200 : 404,
        json: () => Promise.resolve(match ? match[1] : { error: "not stubbed" }),
      } as unknown as Response);
    }),
  );
}

function renderRecurring(routes: Record<string, unknown>) {
  stubFetch(routes);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <RecurringTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
}

describe("RecurringTab (bills calendar above the detection table)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("places each bill in the day cell it is due, as text with its amount", async () => {
    renderRecurring({
      "/api/finance/bills/upcoming": calendarPayload,
      "/api/finance/recurring": detectionRows,
    });

    const netflix = await screen.findAllByText(/Netflix \$15\.99/);
    expect(netflix).toHaveLength(2); // the Jul 15 and Aug 15 day cells (the table row carries no amount)
    const julyCell = netflix[0].closest("td")!;
    expect(within(julyCell).getByText("15")).toBeTruthy();
    expect(within(julyCell).getByText(/Gym \$60\.00/)).toBeTruthy(); // two bills share the day

    // A month-grid per month in the window, with weekday column headers.
    expect(screen.getByText("July 2026")).toBeTruthy();
    expect(screen.getByText("August 2026")).toBeTruthy();
    expect(screen.getByText("September 2026")).toBeTruthy();
    expect(screen.getAllByRole("columnheader", { name: /Sun/ }).length).toBeGreaterThanOrEqual(3);
  });

  it("marks the household's today, not the browser's", async () => {
    const { container } = renderRecurring({
      "/api/finance/bills/upcoming": calendarPayload,
      "/api/finance/recurring": detectionRows,
    });
    await screen.findByText("July 2026");

    const today = container.querySelector('[aria-current="date"]');
    expect(today).not.toBeNull();
    expect(within(today as HTMLElement).getByText(/11/)).toBeTruthy();
    expect(today!.textContent).toContain("(today)");
  });

  it("keeps the server-driven detection table rendering underneath", async () => {
    renderRecurring({
      "/api/finance/bills/upcoming": calendarPayload,
      "/api/finance/recurring": detectionRows,
    });

    expect(await screen.findByRole("heading", { name: "Recurring" })).toBeTruthy();
    expect(await screen.findByText("monthly")).toBeTruthy(); // the detection row's cadence cell
  });

  it("says so in plain text when nothing is due in the window", async () => {
    renderRecurring({
      "/api/finance/bills/upcoming": { ...calendarPayload, bills: [] },
      "/api/finance/recurring": [],
    });

    expect(await screen.findByText(/Nothing due in the next 60 days/)).toBeTruthy();
    expect(screen.getByText("No recurring charges detected yet.")).toBeTruthy();
  });
});
