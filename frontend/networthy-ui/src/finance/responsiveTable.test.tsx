// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TransactionsTab } from "./TransactionsTab";
import type { ModuleTabProps } from "@plenipo/ui";

/**
 * The shipped shim itself, not a copy of it — editing or deleting the file moves these tests.
 * Read from disk rather than imported: vitest runs with CSS processing off, so a `?raw` import
 * of a stylesheet resolves to an empty string and would silently assert nothing.
 */
const shimCss = readFileSync(join(process.cwd(), "src", "table-scroll-shim.css"), "utf8");

/**
 * Issue #152. Both halves of this file are about ONE screen contract: a household member must be
 * able to reach every column of a ledger the shell rendered, at any viewport width.
 *
 * The rendering is entirely @plenipo/ui's — our tabs only choose a `dataEndpoint` and compose
 * `GenericTab` — so these tests assert against the shell's real DOM on purpose. If the shell
 * changes the wrapper it stops matching, and that is the signal to unwind the shim rather than a
 * false green.
 */

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

type Listener = (e: { matches: boolean; media: string }) => void;

/**
 * jsdom's own `matchMedia` reports `matches: false` for everything and never fires `change`, so a
 * viewport-driven layout cannot be exercised through it at all. This stub evaluates the width
 * forms the shell uses against a settable width and dispatches `change` to every live
 * MediaQueryList when that width moves — a spec-conformant resize, with no remount.
 */
function installViewport(initialWidth: number) {
  const live: { query: string; listeners: Set<Listener> }[] = [];
  let width = initialWidth;

  const evaluate = (query: string) => {
    const max = /\(max-width:\s*(\d+)px\)/.exec(query);
    if (max) return width <= Number(max[1]);
    const min = /\(min-width:\s*(\d+)px\)/.exec(query);
    if (min) return width >= Number(min[1]);
    return false;
  };

  vi.stubGlobal("matchMedia", (query: string) => {
    const listeners = new Set<Listener>();
    live.push({ query, listeners });
    return {
      media: query,
      get matches() {
        return evaluate(query);
      },
      addEventListener: (_type: string, listener: Listener) => listeners.add(listener),
      removeEventListener: (_type: string, listener: Listener) => listeners.delete(listener),
      addListener: (listener: Listener) => listeners.add(listener),
      removeListener: (listener: Listener) => listeners.delete(listener),
      dispatchEvent: () => true,
      onchange: null,
    };
  });

  return {
    resizeTo(next: number) {
      width = next;
      act(() => {
        for (const entry of live) {
          for (const listener of entry.listeners) listener({ matches: evaluate(entry.query), media: entry.query });
        }
      });
    },
    listenerCount: () => live.reduce((n, entry) => n + entry.listeners.size, 0),
  };
}

/**
 * The two rules that decide whether an overflowing column is reachable, in the order they ship:
 * Tailwind's own `.overflow-hidden` utility (generated into the app bundle because
 * tailwind.config.js scans @plenipo/ui's dist), then our shim, read from the file that is
 * actually imported by main.tsx — so deleting or weakening it turns these tests red.
 */
function installStylesheet() {
  const style = document.createElement("style");
  style.textContent = [
    // Tailwind ships `.overflow-hidden{overflow:hidden}`; written out longhand because jsdom's
    // CSSOM does not expand the shorthand into overflow-x/overflow-y.
    ".overflow-hidden{overflow-x:hidden;overflow-y:hidden}",
    shimCss,
  ].join("\n");
  document.head.appendChild(style);
  return style;
}

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

const rows = [
  {
    occurredOn: "2026-01-04",
    description: "AMZN Mktp US*2K4LM9XY3",
    amount: -84.2,
    accountName: "BBVA Tax MXN",
    transferWith: "⇄ Payoneer USD",
  },
];

function renderTransactions() {
  stubFetch({
    "/api/finance/accounts": [{ id: "a1", name: "BBVA Tax MXN" }],
    "/api/finance/transactions": rows,
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TransactionsTab moduleId="finance" tab={tab} />
    </QueryClientProvider>,
  );
}

describe("the shell's ledger table stays reachable (#152)", () => {
  const injected: HTMLStyleElement[] = [];

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    for (const style of injected.splice(0)) style.remove();
  });

  it("lets the table's own box scroll, so a column past its right edge is never unreachable", async () => {
    injected.push(installStylesheet());
    installViewport(1680);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");

    const table = document.querySelector("table");
    const wrapper = table?.parentElement;
    // Pins the shell DOM the shim targets: if GenericTab stops wrapping its table this way, the
    // shim is dead code and this fails loudly instead of silently protecting nothing.
    expect(wrapper?.classList.contains("overflow-hidden")).toBe(true);

    // `hidden` clips with no scrollbar and no page-level scroll — the columns are simply gone.
    expect(getComputedStyle(wrapper!).overflowX).toBe("auto");
  });

  it("flips a mounted table to the card list when the viewport narrows, with no remount", async () => {
    const viewport = installViewport(1680);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");

    expect(document.querySelectorAll("table").length).toBe(1);
    expect(screen.queryByTestId("card-list")).toBeNull();

    viewport.resizeTo(456);

    expect(screen.queryByTestId("card-list")).not.toBeNull();
    expect(document.querySelectorAll("table").length).toBe(0);
  });

  it("flips the card list back to a table when the viewport widens, with no remount", async () => {
    const viewport = installViewport(456);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");

    expect(screen.queryByTestId("card-list")).not.toBeNull();
    expect(document.querySelectorAll("table").length).toBe(0);

    viewport.resizeTo(1680);

    expect(document.querySelectorAll("table").length).toBe(1);
    expect(screen.queryByTestId("card-list")).toBeNull();
  });

  it("still reads the breakpoint at mount, so a reload at either width renders the right layout", async () => {
    installViewport(456);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");
    expect(screen.queryByTestId("card-list")).not.toBeNull();

    cleanup();
    vi.unstubAllGlobals();

    installViewport(1680);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");
    expect(document.querySelectorAll("table").length).toBe(1);
  });

  it("drops its media-query listener on unmount, so nothing sets state on a dead component", async () => {
    const viewport = installViewport(1680);
    const { unmount } = renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");
    expect(viewport.listenerCount()).toBeGreaterThan(0);

    unmount();

    expect(viewport.listenerCount()).toBe(0);
  });
});
