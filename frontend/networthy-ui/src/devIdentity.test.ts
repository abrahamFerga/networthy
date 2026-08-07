// @vitest-environment jsdom
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
// `?raw` (typed by vite/client, already in tsconfig's `types`) rather than node:fs — it needs no
// @types/node, and no path resolution, which matters because beforeAll stubs the global
// `location` and jsdom's URL resolution reaches for it and throws.
import viteConfigSource from "../vite.config.ts?raw";
import {
  installIdentityInterceptor,
  isDevAuthActive,
  makeIdentity,
  readIdentity,
  signIn,
  signOut,
} from "./devIdentity";

// TODO(plenipo#71): delete with devIdentity.ts when the platform ships a real sign-in seam.

const STORAGE_KEY = "networthy.devIdentity";

// The interceptor captures window.fetch / window.WebSocket at install time and guards itself
// against double-patching, so the stubs have to exist BEFORE the single install, and every test
// then inspects the same spies rather than reinstalling.
// Typed to accept the forwarded arguments so a test can inspect them: the Request-object branch
// puts the headers on the Request itself rather than in an init bag, so it must read calls[0][0].
const baseFetch = vi.fn(async (..._args: unknown[]) => new Response("{}", { status: 200 }));
const socketUrls: string[] = [];

class FakeSocket {
  constructor(public url: string | URL) {
    socketUrls.push(url.toString());
  }
}

beforeAll(() => {
  vi.stubGlobal("fetch", baseFetch);
  vi.stubGlobal("WebSocket", FakeSocket as unknown as typeof WebSocket);
  // signIn/signOut reload the page; jsdom refuses to navigate, so keep the real origin/href
  // (the interceptor resolves relative URLs against them) and stub only reload.
  vi.stubGlobal("location", {
    ...window.location,
    href: window.location.href,
    origin: window.location.origin,
    reload: vi.fn(),
  });
  installIdentityInterceptor();
});

beforeEach(() => {
  window.localStorage.clear();
  document.cookie = "plenipo-dev-user=; Max-Age=0; path=/";
  baseFetch.mockClear();
  socketUrls.length = 0;
});

const headersOf = (call: unknown[]) => new Headers((call[1] as RequestInit)?.headers);
const urlOf = (call: unknown[]) => String(call[0]);

describe("makeIdentity", () => {
  it("fills the defaults a picker leaves blank", () => {
    expect(makeIdentity({ subject: "  ana  " })).toEqual({
      subject: "ana",
      tenant: "dev",
      roles: "",
      name: "ana",
      email: "ana@dev.local",
    });
  });

  it("keeps an explicitly empty roles string rather than inventing one", () => {
    // A role-less principal is a real case — it is what someone looks like before an invite or an
    // explicit grant lands, and it is the only way to exercise that path from the UI. A `||`
    // fallback here would silently hand every newcomer a role the product never granted.
    expect(makeIdentity({ subject: "newcomer", roles: "" }).roles).toBe("");
    expect(makeIdentity({ subject: "sam", roles: " household-member " }).roles).toBe("household-member");
  });
});

describe("readIdentity", () => {
  it("round-trips what signIn stored", () => {
    signIn(makeIdentity({ subject: "sam", roles: "household-member", name: "Sam" }));
    expect(readIdentity()).toMatchObject({ subject: "sam", roles: "household-member", tenant: "dev" });
  });

  it("reads as signed out rather than throwing when storage is corrupt", () => {
    window.localStorage.setItem(STORAGE_KEY, "{ not json");
    expect(readIdentity()).toBeNull();
  });

  it("rejects a stored object with no usable subject", () => {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ tenant: "dev" }));
    expect(readIdentity()).toBeNull();
  });
});

describe("the cookie contract with vite.config.ts", () => {
  // This is the one cross-FILE contract in the switcher: the dev-server proxy parses the cookie
  // and rewrites it into the X-Dev-* headers. Nothing else would catch the two sides drifting —
  // the app would simply keep running as whoever the bundle was built as. The regex and the split
  // below are copied from vite.config.ts's rewriteDevIdentity.
  //
  // A copy pins only THIS side. The "proxy still parses what we write" test below pins the other,
  // which is what #145 asks for: "either side can drift".
  const parseAsViteProxyDoes = () => {
    const match = /(?:^|;\s*)plenipo-dev-user=([^;]+)/.exec(document.cookie);
    if (!match) return null;
    const [subject, roles, name, email] = decodeURIComponent(match[1]).split("|");
    return { subject, roles, name, email };
  };

  it("writes the pipe-delimited shape the proxy expects", () => {
    signIn(makeIdentity({ subject: "ana", roles: "household-admin", name: "Ana" }));
    expect(parseAsViteProxyDoes()).toEqual({
      subject: "ana",
      roles: "household-admin",
      name: "Ana",
      email: "ana@dev.local",
    });
  });

  it("survives a display name containing the cookie delimiter", () => {
    // This is what makes encodeURIComponent in writeCookie load-bearing. Unencoded, the browser
    // ends the cookie value at the ';' — the name is truncated AND the email disappears entirely,
    // so the proxy would stamp a wrong X-Dev-Name and invent an X-Dev-Email. The comma in roles
    // pins the same for ',', which is what this case used to claim and never actually exercised
    // (the old fixture put the comma in roles and the name was plain "Ana Ruiz").
    signIn(makeIdentity({ subject: "ana", roles: "household-admin,household-member", name: "Ana; Ruiz" }));
    expect(parseAsViteProxyDoes()).toEqual({
      subject: "ana",
      roles: "household-admin,household-member",
      name: "Ana; Ruiz",
      email: "ana@dev.local",
    });
  });

  it("proxy still parses what we write — the other half of the contract", () => {
    // `parseAsViteProxyDoes` is a COPY, so on its own it goes green even if vite.config.ts is
    // rewritten to expect a different cookie name or delimiter. Assert the proxy still contains
    // the exact literals the copy was made from, so drift on THAT side fails here too. Crude,
    // but the coupling is real and nothing else in the repo connects these two files.
    expect(viteConfigSource).toContain(String.raw`/(?:^|;\s*)plenipo-dev-user=([^;]+)/`);
    expect(viteConfigSource).toContain(`decodeURIComponent(match[1]).split("|")`);
  });

  it("proxy still rewrites what it parsed into the X-Dev-* headers", () => {
    // Parsing the cookie is only half of #145's first criterion; the other half is "rewrites it
    // into the X-Dev-* headers", and nothing pinned that. Rename X-Dev-Subject to X-Dev-User in
    // rewriteDevIdentity and every test here stayed green — while under `pnpm dev` the proxy then
    // never overwrites the shell's own header, so the app silently keeps running as the bundle's
    // hard-coded dev-user / system_admin holding `*`. Exactly the silent break #145 exists to catch.
    //
    // Asserted on the header NAME and the setHeader call, not the whole expression: a rename or a
    // deleted call is the failure mode, and pinning `roles ?? ""` too would red on a harmless
    // refactor. A behavioural test is not available — rewriteDevIdentity is closure-private inside
    // defineConfig and is not callable from here.
    for (const header of ["X-Dev-Subject", "X-Dev-Roles", "X-Dev-Name", "X-Dev-Email"]) {
      expect(viteConfigSource).toContain(`proxyReq.setHeader("${header}",`);
    }
  });

  it("clears storage and expires the cookie on sign out", () => {
    signIn(makeIdentity({ subject: "ana", roles: "household-admin" }));
    signOut();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(parseAsViteProxyDoes()).toBeNull();
    expect(readIdentity()).toBeNull();
  });
});

describe("the fetch interceptor", () => {
  it("stamps the chosen identity onto a platform call", async () => {
    signIn(makeIdentity({ subject: "sam", roles: "household-member", name: "Sam" }));
    await fetch("/api/finance/accounts");

    const headers = headersOf(baseFetch.mock.calls[0]);
    expect(headers.get("X-Dev-Subject")).toBe("sam");
    expect(headers.get("X-Dev-Roles")).toBe("household-member");
    expect(headers.get("X-Dev-Tenant")).toBe("dev");
    expect(headers.get("X-Dev-Name")).toBe("Sam");
    expect(headers.get("X-Dev-Email")).toBe("sam@dev.local");
  });

  it("preserves headers the caller already set", async () => {
    signIn(makeIdentity({ subject: "sam" }));
    await fetch("/api/finance/accounts", { headers: { "Content-Type": "application/json" } });

    expect(headersOf(baseFetch.mock.calls[0]).get("Content-Type")).toBe("application/json");
  });

  it("does nothing at all when signed out", async () => {
    await fetch("/api/finance/accounts");

    expect(headersOf(baseFetch.mock.calls[0]).get("X-Dev-Subject")).toBeNull();
  });

  it("never leaks the identity to another origin", async () => {
    // The headers name a user and their roles. A same-origin guard is the difference between a
    // dev affordance and quietly announcing who you are to every third party the app talks to.
    signIn(makeIdentity({ subject: "sam", roles: "household-member" }));
    await fetch("https://example.invalid/api/anything");

    expect(headersOf(baseFetch.mock.calls[0]).get("X-Dev-Subject")).toBeNull();
  });

  it("never leaks the identity to another origin on a Request object either", async () => {
    // The test above passes a STRING, so it only ever exercised the string branch. Delete
    // `!isPlatformCall(url)` from the Request branch and every other test stayed green — while
    // fetch(new Request("https://third-party.example/api/x")) then leaves with X-Dev-Subject and
    // X-Dev-Roles naming the user and their roles to a third party. This is the branch the PR
    // itself argued was the dangerous one (signalr and the shell can both call fetch that way),
    // and the consequence is a leak rather than a half-working state.
    signIn(makeIdentity({ subject: "sam", roles: "household-member" }));
    await fetch(new Request("https://example.invalid/api/anything"));

    const forwarded = baseFetch.mock.calls[0][0] as unknown as Request;
    expect(forwarded.headers.get("X-Dev-Subject")).toBeNull();
    expect(forwarded.headers.get("X-Dev-Roles")).toBeNull();
  });

  it("leaves same-origin paths outside /api and /hubs alone", async () => {
    signIn(makeIdentity({ subject: "sam" }));
    await fetch("/assets/index.js");

    expect(headersOf(baseFetch.mock.calls[0]).get("X-Dev-Subject")).toBeNull();
  });

  it("stamps a Request object, not only a URL string", async () => {
    // Every other test here passes a string, so this branch — which rebuilds the Request rather
    // than mutating it — was uncovered. @microsoft/signalr and the shell can both call
    // fetch(new Request(...)), and an unstamped one goes out as whoever the bundle was built as
    // while every string-URL call correctly becomes someone else: the same silent half-working
    // state #145 exists to prevent.
    signIn(makeIdentity({ subject: "sam", roles: "household-member", name: "Sam" }));
    await fetch(new Request(`${window.location.origin}/api/finance/accounts`));

    const forwarded = baseFetch.mock.calls[0][0] as unknown as Request;
    expect(forwarded.headers.get("X-Dev-Subject")).toBe("sam");
    expect(forwarded.headers.get("X-Dev-Roles")).toBe("household-member");
    expect(forwarded.headers.get("X-Dev-Tenant")).toBe("dev");
  });
});

describe("the hub URL", () => {
  // A WebSocket upgrade cannot carry custom headers, so the shell puts the same values in the
  // query string. Stamping only the headers would leave chat running as whoever the bundle was
  // built as, while every REST call correctly became someone else — the most confusing possible
  // half-working state.
  it("rewrites X-Dev-* query parameters the shell already placed", async () => {
    signIn(makeIdentity({ subject: "sam", roles: "household-member", name: "Sam" }));
    await fetch("/hubs/agent?X-Dev-Subject=dev-user&X-Dev-Roles=system_admin&negotiateVersion=1");

    const url = new URL(urlOf(baseFetch.mock.calls[0]), window.location.origin);
    expect(url.searchParams.get("X-Dev-Subject")).toBe("sam");
    expect(url.searchParams.get("X-Dev-Roles")).toBe("household-member");
    expect(url.searchParams.get("negotiateVersion")).toBe("1");
  });

  it("does not invent parameters the shell did not put there", async () => {
    signIn(makeIdentity({ subject: "sam" }));
    await fetch("/hubs/agent?negotiateVersion=1");

    const url = new URL(urlOf(baseFetch.mock.calls[0]), window.location.origin);
    expect(url.searchParams.has("X-Dev-Subject")).toBe(false);
  });

  it("rewrites the query on a Request object too, surviving the rebuild", () => {
    // The Request branch stamps the query BEFORE constructing the replacement Request, so this
    // pins that the rewritten URL is the one actually forwarded rather than the original.
    signIn(makeIdentity({ subject: "sam", roles: "household-member" }));
    return fetch(
      new Request(`${window.location.origin}/hubs/agent?X-Dev-Subject=dev-user&negotiateVersion=1`),
    ).then(() => {
      const url = new URL((baseFetch.mock.calls[0][0] as unknown as Request).url);
      expect(url.searchParams.get("X-Dev-Subject")).toBe("sam");
      expect(url.searchParams.get("negotiateVersion")).toBe("1");
    });
  });

  it("stamps the WebSocket handshake URL too", () => {
    signIn(makeIdentity({ subject: "sam", roles: "household-member" }));
    new WebSocket(`${window.location.origin}/hubs/agent?X-Dev-Subject=dev-user`);

    expect(new URL(socketUrls[0]).searchParams.get("X-Dev-Subject")).toBe("sam");
  });
});

describe("isDevAuthActive", () => {
  it("is false when the API rejects an unauthenticated call", async () => {
    // With a real IdP configured the platform registers JwtBearer instead of the Dev scheme, and
    // the picker must disappear rather than pretend to be authentication.
    baseFetch.mockResolvedValueOnce(new Response("", { status: 401 }));
    await expect(isDevAuthActive()).resolves.toBe(false);
  });

  it("is false when the API cannot be reached at all", async () => {
    baseFetch.mockRejectedValueOnce(new Error("connection refused"));
    await expect(isDevAuthActive()).resolves.toBe(false);
  });

  it("is true when the Dev scheme answers", async () => {
    baseFetch.mockResolvedValueOnce(new Response("{}", { status: 200 }));
    await expect(isDevAuthActive()).resolves.toBe(true);
  });
});
