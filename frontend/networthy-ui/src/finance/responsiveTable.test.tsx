// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TransactionsTab } from "./TransactionsTab";
import type { ModuleTabProps } from "@plenipo/ui";

/**
 * Issue #152. Every test in this file is about ONE screen contract: a household member must be
 * able to reach every column of a ledger the shell rendered, at any viewport width.
 *
 * The rendering is entirely @plenipo/ui's (our tabs only choose a `dataEndpoint` and compose
 * `GenericTab`), so these tests assert against the shell's real DOM on purpose.
 *
 * This used to be a shim guard. @plenipo/ui wrapped its tables in `overflow-hidden`, which clips
 * both axes, and `src/table-scroll-shim.css` re-opened the horizontal one. plenipo#112 fixed it at
 * the source in @plenipo/ui 0.1.0-alpha.29 and the shim is deleted, so what is asserted now is the
 * contract the product actually cares about rather than the presence of our patch: with no
 * stylesheet of ours installed at all, the box the shell wraps its table in scrolls itself.
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
 * Tailwind's overflow utilities, longhand: jsdom's CSSOM does not expand the `overflow` shorthand
 * into overflow-x/overflow-y, and it ships no utility CSS of its own, so the class names the shell
 * renders mean nothing until the matching declarations exist. Tailwind generates these into the app
 * bundle because tailwind.config.js scans @plenipo/ui's dist.
 */
const TAILWIND_OVERFLOW: Record<string, string> = {
  "overflow-auto": "overflow-x:auto;overflow-y:auto",
  "overflow-hidden": "overflow-x:hidden;overflow-y:hidden",
  "overflow-clip": "overflow-x:clip;overflow-y:clip",
  "overflow-visible": "overflow-x:visible;overflow-y:visible",
  "overflow-scroll": "overflow-x:scroll;overflow-y:scroll",
  "overflow-x-auto": "overflow-x:auto",
  "overflow-x-hidden": "overflow-x:hidden",
  "overflow-x-clip": "overflow-x:clip",
  "overflow-x-scroll": "overflow-x:scroll",
  "overflow-y-auto": "overflow-y:auto",
  "overflow-y-hidden": "overflow-y:hidden",
  "overflow-y-clip": "overflow-y:clip",
  "overflow-y-scroll": "overflow-y:scroll",
};

/**
 * Installs the Tailwind overflow rules for exactly the utilities the given element carries, and
 * nothing else: no shim, no stylesheet of ours. That asymmetry is the point. If @plenipo/ui ever
 * goes back to clipping, this emits `overflow-x: hidden` from the shell's own class list and the
 * assertion below goes red, instead of passing on a rule the shell no longer asks for.
 */
function installTailwindOverflowRules(element: Element) {
  const style = document.createElement("style");
  style.textContent = Array.from(element.classList)
    .filter((name) => name in TAILWIND_OVERFLOW)
    .map((name) => `.${name}{${TAILWIND_OVERFLOW[name]}}`)
    .join("\n");
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
    installViewport(1680);
    renderTransactions();
    await screen.findByText("AMZN Mktp US*2K4LM9XY3");

    const table = document.querySelector("table");
    const wrapper = table?.parentElement;
    expect(wrapper).toBeTruthy();

    injected.push(installTailwindOverflowRules(wrapper!));

    // The whole contract in one line: the table's own box scrolls, so the column past its right
    // edge is reachable. `hidden` clips with no scrollbar and no page-level scroll to fall back
    // on, and the columns are simply gone at every width the card layout does not cover.
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
