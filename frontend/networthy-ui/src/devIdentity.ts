/**
 * Dev identity switching — sign in / sign out for personal mode. **This is not authentication.**
 *
 * TODO(plenipo#71): delete this whole file, SignInScreen.tsx, and their wiring once @plenipo/ui
 * ships a real sign-in seam. Nothing here should outlive that.
 *
 * Why it exists: the published @plenipo/ui shell inlines the X-Dev-* headers as a module-private
 * constant (dev-user / dev / system_admin) and exports no way to change them, so the app has no
 * login, no logout, and no way to be used as anyone else. The API's Dev scheme trusts whatever
 * X-Dev-* headers arrive, so *choosing* an identity is the whole job. Two delivery paths, because
 * the app runs behind two different things:
 *
 *   1. `pnpm dev` (Vite, :5173) — vite.config.ts's proxy already rewrites a `plenipo-dev-user`
 *      cookie into the headers, for HTTP *and* the WebSocket upgrade. We set that cookie.
 *   2. The built bundle served by Networthy.Host from wwwroot/app — no proxy in between, so the
 *      browser talks to the API directly and the cookie means nothing. We patch fetch/WebSocket.
 *
 * Both read the same stored identity, so they cannot disagree. Path 1 wins where both apply (the
 * proxy overwrites request headers), which is fine because the source is identical.
 *
 * Deliberately kept blunt and obvious — a nicer version of this gets adopted and then nobody
 * wants to remove it.
 */

export interface DevIdentity {
  subject: string;
  tenant: string;
  /** Comma-separated, or "" for a role-less principal (exercises invites / explicit grants). */
  roles: string;
  name: string;
  email: string;
}

const STORAGE_KEY = "networthy.devIdentity";
const COOKIE_NAME = "plenipo-dev-user";

/** The header names the platform's DevAuthenticationHandler reads. */
const HEADERS: ReadonlyArray<[string, (id: DevIdentity) => string]> = [
  ["X-Dev-Subject", (id) => id.subject],
  ["X-Dev-Tenant", (id) => id.tenant],
  ["X-Dev-Roles", (id) => id.roles],
  ["X-Dev-Name", (id) => id.name],
  ["X-Dev-Email", (id) => id.email],
];

export function makeIdentity(partial: Partial<DevIdentity> & { subject: string }): DevIdentity {
  const subject = partial.subject.trim();
  return {
    subject,
    tenant: partial.tenant?.trim() || "dev",
    // Empty string is meaningful (a role-less principal) and must survive, so only fall back
    // when the caller said nothing at all.
    roles: partial.roles === undefined ? "" : partial.roles.trim(),
    name: partial.name?.trim() || subject,
    email: partial.email?.trim() || `${subject}@dev.local`,
  };
}

export function readIdentity(): DevIdentity | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<DevIdentity>;
    return typeof parsed?.subject === "string" && parsed.subject.length > 0
      ? makeIdentity(parsed as DevIdentity)
      : null;
  } catch {
    // Private mode, corrupt JSON, storage disabled — treat as signed out rather than crashing boot.
    return null;
  }
}

/**
 * The cookie shape vite.config.ts's proxy parses: "subject|roles|name|email|tenant".
 *
 * `tenant` is last because it was added after the other four (#204) and a cookie already sitting
 * in someone's browser has to keep parsing — `split("|")` yields undefined for a missing trailing
 * field, and the proxy falls back to the same "dev" default makeIdentity uses.
 */
function writeCookie(identity: DevIdentity | null) {
  if (!identity) {
    document.cookie = `${COOKIE_NAME}=; Max-Age=0; path=/`;
    return;
  }
  const value = encodeURIComponent(
    [identity.subject, identity.roles, identity.name, identity.email, identity.tenant].join("|"),
  );
  document.cookie = `${COOKIE_NAME}=${value}; path=/; SameSite=Lax`;
}

export function signIn(identity: DevIdentity) {
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
  writeCookie(identity);
  // Full reload rather than a state update: react-query holds the previous identity's data, and
  // an open SignalR/AG-UI stream keeps its original headers. Reloading is the honest reset.
  window.location.reload();
}

export function signOut() {
  window.localStorage.removeItem(STORAGE_KEY);
  writeCookie(null);
  window.location.reload();
}

/**
 * True when the API is running the Dev auth scheme (no IdP configured), which is the only
 * situation where any of this is legitimate. Probed rather than assumed: with a real authority
 * the platform registers JwtBearer instead and an unauthenticated call is rejected, so this
 * switcher must disappear rather than pretend.
 */
export async function isDevAuthActive(): Promise<boolean> {
  try {
    const response = await fetch("/api/platform/me", { headers: { "X-Networthy-Auth-Probe": "1" } });
    return response.status !== 401 && response.status !== 403;
  } catch {
    return false;
  }
}

const PATCHED = Symbol.for("networthy.devIdentity.patched");
const SAME_ORIGIN_PREFIXES = ["/api/", "/hubs/"];

function isPlatformCall(url: URL): boolean {
  return url.origin === window.location.origin
    && SAME_ORIGIN_PREFIXES.some((prefix) => url.pathname.startsWith(prefix));
}

function resolve(input: string | URL): URL | null {
  try {
    return new URL(input.toString(), window.location.href);
  } catch {
    return null;
  }
}

/**
 * The hub URL carries the same X-Dev-* values as query parameters (a WebSocket upgrade cannot
 * carry custom headers), so an identity change has to reach both places or chat silently stays
 * whoever the bundle was built as.
 */
function stampQuery(url: URL, identity: DevIdentity): URL {
  for (const [header, get] of HEADERS) {
    if (url.searchParams.has(header)) url.searchParams.set(header, get(identity));
  }
  return url;
}

/**
 * Stamp the chosen identity onto every platform call the shell makes. Install BEFORE React
 * renders — the shell fires its first requests during mount.
 */
export function installIdentityInterceptor() {
  const win = window as unknown as Record<PropertyKey, unknown>;
  if (win[PATCHED]) return;
  win[PATCHED] = true;

  const originalFetch = window.fetch.bind(window);
  window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
    const identity = readIdentity();
    if (!identity) return originalFetch(input as RequestInfo, init);

    // A Request object carries its own URL and headers; rebuild it rather than mutating.
    if (typeof Request !== "undefined" && input instanceof Request) {
      const url = resolve(input.url);
      if (!url || !isPlatformCall(url)) return originalFetch(input, init);
      const request = new Request(stampQuery(url, identity).toString(), input);
      for (const [header, get] of HEADERS) request.headers.set(header, get(identity));
      return originalFetch(request, init);
    }

    const url = resolve(input as string | URL);
    if (!url || !isPlatformCall(url)) return originalFetch(input as RequestInfo, init);

    const headers = new Headers(init?.headers);
    for (const [header, get] of HEADERS) headers.set(header, get(identity));
    return originalFetch(stampQuery(url, identity).toString(), { ...init, headers });
  };

  // SignalR's WebSocket transport bypasses fetch entirely; its identity rides in the query string.
  const OriginalWebSocket = window.WebSocket;
  const PatchedWebSocket = function (this: unknown, url: string | URL, protocols?: string | string[]) {
    const identity = readIdentity();
    const resolved = identity ? resolve(url) : null;
    const target = resolved && isPlatformCall(resolved)
      ? stampQuery(resolved, identity!).toString()
      : url;
    return new OriginalWebSocket(target, protocols);
  } as unknown as typeof WebSocket;
  PatchedWebSocket.prototype = OriginalWebSocket.prototype;
  Object.setPrototypeOf(PatchedWebSocket, OriginalWebSocket);
  window.WebSocket = PatchedWebSocket;
}
