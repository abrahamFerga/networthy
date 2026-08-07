// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { IdentityBar } from "./DevIdentityUi";
import { makeIdentity } from "./devIdentity";

/**
 * One contract: the sign-out control must never sit on top of something the user
 * needs to click.
 *
 * It was `fixed bottom-3 right-3 z-50`, which put it over the chat composer's Send button —
 * measured at 1280x720, 61% of Send was covered and `document.elementFromPoint` at Send's centre
 * returned the badge, so the primary action of the primary screen was mostly dead. jsdom has no
 * layout engine, so these tests cannot re-measure that; they pin the two STRUCTURAL choices that
 * make the overlap impossible, which is what actually regressed and what a future edit would undo:
 *
 *   1. the bar is in normal flow — not `fixed`/`absolute`/`sticky`, so it can never cover anything;
 *   2. it is the FIRST track of the app column, ahead of the shell — because the shell's mobile
 *      BottomNav is `fixed inset-x-0 bottom-0`, which anchors to the viewport and would cover a
 *      bar placed in the last track.
 *
 * The real geometry is verified at runtime against the running shell, not here.
 */

const identity = makeIdentity({ subject: "ana", roles: "household-admin", name: "Ana" });

afterEach(cleanup);

describe("IdentityBar", () => {
  it("shows who you are, the role, and the way out", () => {
    render(<IdentityBar identity={identity} />);

    expect(screen.getByText("Ana")).toBeTruthy();
    expect(screen.getByText(/household-admin/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
  });

  it("says so plainly when a principal holds no roles at all", () => {
    render(<IdentityBar identity={makeIdentity({ subject: "newcomer", roles: "" })} />);
    expect(screen.getByText(/no roles/)).toBeTruthy();
  });

  /**
   * The regression itself. Any positioned value takes the bar out of flow and puts it back on top
   * of whatever happens to be underneath — which is how it landed on Send.
   */
  it("stays in normal flow so it cannot cover the shell", () => {
    const { container } = render(<IdentityBar identity={identity} />);
    const root = container.firstElementChild as HTMLElement;

    const classes = root.className.split(/\s+/);
    for (const positioned of ["fixed", "absolute", "sticky"]) {
      expect(classes).not.toContain(positioned);
    }
    // A bare z-index has no meaning in flow, and its presence is the tell that someone is
    // stacking this above the shell again rather than sitting beside it.
    expect(classes.some((c) => c.startsWith("z-"))).toBe(false);
  });
});

describe("App layout", () => {
  beforeEach(() => {
    localStorage.setItem("networthy.devIdentity", JSON.stringify(identity));
    // isDevAuthActive() probes /api/platform/me; branding is best-effort.
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes("/api/platform/branding")
          ? new Response(JSON.stringify({ name: "Networthy" }), {
              headers: { "content-type": "application/json" },
            })
          : new Response("{}", { status: 200, headers: { "content-type": "application/json" } }),
      ),
    );
  });

  afterEach(() => {
    localStorage.clear();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it("puts the identity bar ahead of the shell, never after it", async () => {
    // The shell is stubbed: this asserts the ORDER App composes, and rendering the real
    // @plenipo/ui shell would drag in its whole data layer without making the claim any stronger.
    vi.doMock("@plenipo/ui", () => ({
      PlenipoApp: () => <div data-testid="shell" />,
      defineModule: (id: string, config: unknown) => ({ id, ...(config as object) }),
      GenericTab: () => null,
      StatTile: () => null,
      ProgressBar: () => null,
      apiGet: async () => ({}),
    }));

    const { default: App } = await import("./App");
    const { container } = render(<App />);
    // isDevAuthActive resolves on a microtask; flush it before reading the tree.
    await act(async () => {});

    const shell = screen.getByTestId("shell");
    const signOut = screen.getByRole("button", { name: "Sign out" });
    const bar = signOut.parentElement!;

    // DOCUMENT_POSITION_FOLLOWING: the shell comes after the bar in document order, so the bar
    // occupies the first flex track and the shell's viewport-anchored BottomNav cannot reach it.
    expect(bar.compareDocumentPosition(shell) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    // Both are children of the same flex column — the bar is a sibling track, not an overlay.
    const column = container.firstElementChild as HTMLElement;
    expect(column.className).toContain("flex-col");
    expect(bar.parentElement).toBe(column);
  });
});
