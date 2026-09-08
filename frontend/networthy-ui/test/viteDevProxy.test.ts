// The dev-server proxy's HALF of the identity contract, tested by running it.
//
// `devIdentity.test.ts` pins the parse side by copying vite.config.ts's regex and asserting the
// literals still appear in its source. That was as far as round 3 went, on the stated grounds that
// "rewriteDevIdentity is closure-private inside defineConfig and is not callable from here". That
// reason was wrong: `defineConfig` is the identity function for its callback overload, so this
// module's default export IS the factory, and `server.proxy[...].configure` hands you the real
// listener. Everything below calls the real thing.
//
// Two deliberate placements:
//   * Default `node` environment, not jsdom — the factory reads process.env and node:fs.
//   * OUTSIDE `src/`, because tsconfig.json's `include` is `["src"]`. Importing vite.config.ts from
//     inside src would pull it into the tsc program, and its `node:fs` / `node:url` / `node:http`
//     imports would then demand @types/node. Adding a dependency so a dev-only shim's test can be
//     typechecked is the worse trade; vitest transpiles this file either way.
//
// TODO(plenipo#71): delete with devIdentity.ts when the platform ships a real sign-in seam.
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import viteConfig from "../vite.config";

const API_BASE = "http://localhost:5199";
const PROXIED_PREFIXES = ["/api", "/hubs", "/admin"];

type ProxyReqStub = { setHeader(name: string, value: string): void };
type ReqStub = { headers: { cookie?: string } };
type Listener = (proxyReq: ProxyReqStub, req: ReqStub) => void;

let savedApiBase: string | undefined;
let savedBrandName: string | undefined;

beforeEach(() => {
  savedApiBase = process.env.VITE_API_BASE;
  savedBrandName = process.env.VITE_BRAND_NAME;
  // Without a target the factory returns `proxy: undefined` and there is nothing to configure —
  // that branch is asserted on its own below.
  process.env.VITE_API_BASE = API_BASE;
});

afterEach(() => {
  // The factory does `process.env.VITE_BRAND_NAME ??= "Networthy"`, so it MUTATES the environment
  // this suite runs in. Put both back rather than leaking into whatever runs next.
  if (savedApiBase === undefined) delete process.env.VITE_API_BASE;
  else process.env.VITE_API_BASE = savedApiBase;
  if (savedBrandName === undefined) delete process.env.VITE_BRAND_NAME;
  else process.env.VITE_BRAND_NAME = savedBrandName;
});

/** The real config object, built the way `vite dev` builds it. */
const buildConfig = async () => {
  const factory = viteConfig as unknown as (env: {
    command: "serve" | "build";
    mode: string;
  }) => Promise<Record<string, never>> | Record<string, never>;
  return (await factory({ command: "serve", mode: "development" })) as unknown as {
    server?: { proxy?: Record<string, { configure?: (proxy: { on(event: string, listener: Listener): void }) => void }> };
  };
};

/** Runs the real `configure` for one prefix and reports which events it registered, with their listeners. */
const registeredListeners = async (prefix: string) => {
  const config = await buildConfig();
  const entry = config.server?.proxy?.[prefix];
  if (!entry?.configure) throw new Error(`proxy["${prefix}"] has no configure hook`);
  const events: { event: string; listener: Listener }[] = [];
  entry.configure({ on: (event, listener) => events.push({ event, listener }) });
  return events;
};

/** Feeds a cookie through the real rewrite listener and returns the headers it actually set. */
const headersFor = (listener: Listener, cookie: string | undefined) => {
  const headers: Record<string, string> = {};
  listener({ setHeader: (name, value) => (headers[name] = value) }, { headers: { cookie } });
  return headers;
};

const rewriteListener = async (prefix = "/api") => {
  const found = (await registeredListeners(prefix)).find((e) => e.event === "proxyReq");
  if (!found) throw new Error(`proxy["${prefix}"] registered no proxyReq listener`);
  return found.listener;
};

const cookie = (value: string) => `plenipo-dev-user=${encodeURIComponent(value)}`;

describe("the dev-server proxy's identity rewrite", () => {
  it("registers the rewrite on BOTH the request and the WebSocket-upgrade event", async () => {
    // #145's second criterion is the hub path: "a WebSocket upgrade cannot carry custom headers".
    // The upgrade is proxied through `proxyReqWs`, a SEPARATE event from `proxyReq`. Deleting
    // `proxy.on("proxyReqWs", rewriteDevIdentity)` from vite.config.ts left every other test in
    // this repo green while chat silently kept running as the bundle's hard-coded identity.
    for (const prefix of PROXIED_PREFIXES) {
      const events = (await registeredListeners(prefix)).map((e) => e.event);
      expect(events, `proxy["${prefix}"]`).toContain("proxyReq");
      expect(events, `proxy["${prefix}"]`).toContain("proxyReqWs");
    }
  });

  it("registers the SAME rewrite on both events, so the two paths cannot drift apart", async () => {
    const events = await registeredListeners("/hubs");
    const byEvent = new Map(events.map((e) => [e.event, e.listener]));
    expect(byEvent.get("proxyReqWs")).toBe(byEvent.get("proxyReq"));
  });

  it("rewrites a full cookie into all five X-Dev-* header VALUES", async () => {
    // Round 3 pinned only the four header NAMES, via `toContain('proxyReq.setHeader("X-Dev-…",')`
    // over the config's source text. That stayed green under a value swap — setHeader("X-Dev-Email",
    // name) — which is the same silent break as a rename. These are the values.
    //
    // #204 made this an exact-set assertion over FIVE headers rather than four: the set is the
    // contract, and `toEqual` is what makes a header quietly disappearing fail here.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("maria|household-member|Maria Ruiz|maria@example.test|acme"))).toEqual({
      "X-Dev-Subject": "maria",
      "X-Dev-Tenant": "acme",
      "X-Dev-Roles": "household-member",
      "X-Dev-Name": "Maria Ruiz",
      "X-Dev-Email": "maria@example.test",
    });
  });

  it("rewrites the tenant field into X-Dev-Tenant", async () => {
    // #204. `devIdentity.ts`'s HEADERS list stamps FIVE headers; this proxy rewrote four, and
    // there was no fifth cookie field for it to read. The consequence is narrow but real: the
    // cookie-only workflow documented in vite.config.ts's own header comment — set the cookie in
    // the console, reload — cannot express a tenant at all, so X-Dev-Tenant stays whatever the
    // published @plenipo/ui shell hard-codes ("dev") and every request lands in that one tenant.
    // Tenant isolation is a platform invariant, so the dev tool that cannot say "the other tenant"
    // is exactly the tool you reach for to reproduce a cross-tenant bug.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("ana|household-admin|Ana Ruiz|ana@example.test|acme"))["X-Dev-Tenant"]).toBe(
      "acme",
    );
  });

  it("defaults X-Dev-Tenant to dev for a cookie written before the tenant field existed", async () => {
    // The field is APPENDED, so `split("|")` yields `undefined` for a cookie already in someone's
    // browser. That must land on the same default the client's makeIdentity uses ("dev") rather
    // than stamping the literal string "undefined" — which the API's Dev scheme would trust as a
    // tenant name and silently isolate the session into a tenant that does not exist.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("maria|household-member|Maria|maria@example.test"))["X-Dev-Tenant"]).toBe("dev");
    expect(headersFor(rewrite, cookie("newcomer"))["X-Dev-Tenant"]).toBe("dev");
  });

  it("keeps an empty roles field empty instead of substituting one", async () => {
    // #145's fourth criterion, on the PROXY side: `roles: ""` is a real principal, and the proxy is
    // the last place the value passes through before the API's Dev scheme trusts it. `roles ?? ""`
    // preserves an explicitly empty field; a `||` here would hand every role-less newcomer a role
    // the product never granted, and the API could not tell the difference.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("newcomer||Newcomer|"))["X-Dev-Roles"]).toBe("");
  });

  it("fills name and email from the subject when the cookie omits them", async () => {
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("newcomer"))).toEqual({
      "X-Dev-Subject": "newcomer",
      "X-Dev-Tenant": "dev",
      "X-Dev-Roles": "",
      "X-Dev-Name": "newcomer",
      "X-Dev-Email": "newcomer@dev.local",
    });
  });

  it("survives the delimiter appearing inside a display name", async () => {
    // encodeURIComponent on the write side is what makes this safe; the proxy decodes the WHOLE
    // value once and then splits, so an encoded ";" cannot end the cookie early.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("ana|household-admin|Ana; Ruiz|ana@example.test"))["X-Dev-Name"]).toBe(
      "Ana; Ruiz",
    );
  });

  it("sets nothing at all when no identity cookie is present", async () => {
    // The absence of a cookie must leave the shell's own headers untouched — that is how "log out
    // back to Dev User" works. Setting X-Dev-Subject to "" here would lock dev out entirely.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, undefined)).toEqual({});
    expect(headersFor(rewrite, "other-cookie=1")).toEqual({});
  });

  it("sets nothing when the cookie carries roles but no subject", async () => {
    // `if (!subject) return` — a malformed cookie must not produce a principal with no name but
    // real roles attached.
    const rewrite = await rewriteListener();
    expect(headersFor(rewrite, cookie("|system_admin"))).toEqual({});
  });

  it("reads the cookie out of a header list that has other cookies before it", async () => {
    const rewrite = await rewriteListener();
    const raw = `theme=dark; ${cookie("sam|household-member")}; tz=UTC`;
    expect(headersFor(rewrite, raw)["X-Dev-Subject"]).toBe("sam");
  });

  it("configures no proxy at all when VITE_API_BASE is unset", async () => {
    delete process.env.VITE_API_BASE;
    const config = await buildConfig();
    expect(config.server?.proxy).toBeUndefined();
  });
});
