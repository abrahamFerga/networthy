import { existsSync } from "node:fs";
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
  const apiTarget = process.env.VITE_API_BASE?.trim();
  const proxy = apiTarget
    ? {
        "/api": { target: apiTarget, changeOrigin: true, secure: false },
        "/hubs": { target: apiTarget, changeOrigin: true, secure: false, ws: true },
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
