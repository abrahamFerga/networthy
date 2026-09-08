import { existsSync } from "node:fs";
import type { ClientRequest, IncomingMessage } from "node:http";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// An OPTIONAL sibling Plenipo checkout. @plenipo/ui installs from npm (ADR-0008, amended), so a
// checkout is only a dev convenience — see aliasToSource below.
const plenipoFrontend = fileURLToPath(new URL("../../../Plenipo/frontend", import.meta.url));
const plenipoUiSrc = `${plenipoFrontend}/plenipo-ui/src/index.ts`;

// The Networthy app entry (ADR-0008): the Plenipo shell + the custom Overview tab, built as a
// static bundle that Networthy.Host serves from wwwroot/app. Branding and API base bake in via
// VITE_BRAND_NAME / VITE_API_BASE, same contract as @plenipo/ui's own app build.
export default defineConfig(({ command }) => {
  process.env.VITE_BRAND_NAME ??= "Networthy";

  // Dev server only: compile @plenipo/ui from the checkout's SOURCE instead of its prebuilt dist.
  // The dist freezes import.meta.env at whatever it was when the LIBRARY was built (VITE_API_BASE
  // baked wrong = every API call goes to the wrong origin); source compiles against this dev
  // server's live env, and shell edits hot-reload without a dist rebuild. Builds and tests keep
  // consuming the dist — the same bytes that ship embedded in Networthy.Host.
  const aliasToSource = command === "serve" && !process.env.VITEST && existsSync(plenipoUiSrc);

  // Dev server, no checkout: the published @plenipo/ui dist is built with VITE_API_BASE="", and
  // that empty base is FROZEN into it — setting VITE_API_BASE here cannot move the library's own
  // calls. So the shell asks for /api/... and /hubs/... same-origin, which on this dev server is
  // Vite, not the API: every request 404s and the dashboard renders "Can't reach the Plenipo API".
  // Proxy those prefixes to the real API so the prebuilt library works with no checkout at all.
  // (When aliasToSource wins, the library compiles against the live env and calls the API
  // absolutely, so the proxy simply goes unused.)
  // Dev identity switching: there is no login/logout in dev — the published shells hard-code
  // the X-Dev-* headers (dev-user / dev / system_admin) and the API's Dev scheme trusts whatever
  // arrives. To use the app as someone else, set a `plenipo-dev-user` cookie shaped
  // "subject|roles|display name|email|tenant" (everything after subject optional; roles empty =
  // only what invites/grants assign; tenant defaults to "dev") and reload:
  //   document.cookie = "plenipo-dev-user=" + encodeURIComponent("maria|household-member|Maria") + "; path=/"
  // "Log out" back to Dev User (system_admin):
  //   document.cookie = "plenipo-dev-user=; Max-Age=0; path=/"
  //
  // The five fields are the five headers devIdentity.ts's HEADERS list stamps — they must stay in
  // step. #204: `tenant` was missing here while the client stamped it, so this rewrite overwrote
  // four headers and left X-Dev-Tenant at whatever the published shell hard-coded. The cookie-only
  // workflow documented directly above therefore could not express a second tenant at all, which
  // is precisely the tool you would reach for to reproduce a cross-tenant bug. It is the LAST
  // field so a cookie written before it existed still parses — `split("|")` just yields undefined.
  const rewriteDevIdentity = (proxyReq: ClientRequest, req: IncomingMessage) => {
    const match = /(?:^|;\s*)plenipo-dev-user=([^;]+)/.exec(req.headers.cookie ?? "");
    if (!match) return;
    const [subject, roles, name, email, tenant] = decodeURIComponent(match[1]).split("|");
    if (!subject) return;
    proxyReq.setHeader("X-Dev-Subject", subject);
    proxyReq.setHeader("X-Dev-Tenant", tenant || "dev");
    proxyReq.setHeader("X-Dev-Roles", roles ?? "");
    proxyReq.setHeader("X-Dev-Name", name || subject);
    proxyReq.setHeader("X-Dev-Email", email || `${subject}@dev.local`);
  };
  const withDevIdentity = {
    configure: (proxy: { on(event: string, listener: typeof rewriteDevIdentity): void }) => {
      proxy.on("proxyReq", rewriteDevIdentity);
      proxy.on("proxyReqWs", rewriteDevIdentity);
    },
  };

  const apiTarget = process.env.VITE_API_BASE?.trim();
  const proxy = apiTarget
    ? {
        "/api": { target: apiTarget, changeOrigin: true, secure: false, ...withDevIdentity },
        "/hubs": { target: apiTarget, changeOrigin: true, secure: false, ws: true, ...withDevIdentity },
        // The shell's "Admin ↗" link is frozen to same-origin "/admin" in the published dist.
        // With no Plenipo checkout there is no admin dev server, but the API serves the committed
        // admin bundle from wwwroot/admin — proxy the prefix (page + its /admin/assets) so the
        // link works from this dev server instead of 404ing on Vite.
        "/admin": { target: apiTarget, changeOrigin: true, secure: false, ...withDevIdentity },
      }
    : undefined;

  return {
    plugins: [react()],
    resolve: {
      alias: aliasToSource ? [{ find: /^@plenipo\/ui$/, replacement: plenipoUiSrc }] : [],
      // The aliased source imports these from the Plenipo checkout's node_modules — force one copy
      // (ours) so React hooks/context never see two instances.
      dedupe: ["react", "react-dom", "react-router-dom", "@tanstack/react-query", "@microsoft/signalr"],
    },
    server: {
      fs: { allow: [".", plenipoFrontend] },
      proxy,
    },
  };
});
