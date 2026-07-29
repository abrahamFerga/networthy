// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TransactionsTab } from "./TransactionsTab";
import type { ModuleTabProps } from "@plenipo/ui";

const tab: ModuleTabProps["tab"] = {
  id: "transactions",
  label: "Transactions",
  route: "/finance/transactions",
  dataEndpoint: "/api/finance/transactions",
  columns: [
    { field: "occurredOn", header: "Date" },
    { field: "description", header: "Description" },
    { field: "amount", header: "Amount" },
    { field: "accountName", header: "Account" },
    { field: "transferWith", header: "Transfer" },
  ],
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

const accounts = [
  { id: "a1", name: "Payoneer USD" },
  { id: "a2", name: "BBVA Tax MXN" },
];

function renderTransactions(routes: Record<string, unknown>) {
  const mock = stubFetch({ "/api/finance/accounts": accounts, ...routes });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <TransactionsTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
  return mock;
}

describe("TransactionsTab (the ledger, scopable to one account)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders every account's rows by default, with the transfer counterpart column", async () => {
    const mock = renderTransactions({
      "/api/finance/transactions": [
        {
          occurredOn: "2026-07-01",
          description: "Withdrawal to bank",
          amount: 1000,
          accountName: "Payoneer USD",
          transferWith: "⇄ BBVA Tax MXN",
        },
        {
          occurredOn: "2026-07-02",
          description: "Groceries",
          amount: 250,
          accountName: "BBVA Tax MXN",
          transferWith: null,
        },
      ],
    });

    expect(await screen.findByText("Withdrawal to bank")).toBeTruthy();
    expect(screen.getByText("⇄ BBVA Tax MXN")).toBeTruthy();
    // The default fetch carries NO account param — all visible accounts' ledger.
    const transactionUrls = mock.mock.calls
      .map((c) => String(c[0]))
      .filter((u) => u.includes("/api/finance/transactions"));
    expect(transactionUrls.every((u) => !u.includes("account="))).toBe(true);
    expect((screen.getByLabelText("Account") as HTMLSelectElement).value).toBe("");
  });

  it("re-points the endpoint at ?account= when one account is picked", async () => {
    const mock = renderTransactions({
      "account=Payoneer%20USD": [
        {
          occurredOn: "2026-07-01",
          description: "ACME payout",
          amount: 2500,
          accountName: "Payoneer USD",
          transferWith: null,
        },
      ],
      "/api/finance/transactions": [
        {
          occurredOn: "2026-07-02",
          description: "Groceries",
          amount: 250,
          accountName: "BBVA Tax MXN",
          transferWith: null,
        },
      ],
    });
    await screen.findByText("Groceries");
    // The picker offers the visibility-scoped account names.
    expect(await screen.findByText("Payoneer USD")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "Payoneer USD" } });

    expect(await screen.findByText("ACME payout")).toBeTruthy();
    const urls = mock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes("/api/finance/transactions?account=Payoneer%20USD"))).toBe(true);
  });
});
