import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The sibling Plenipo checkout — same location the package.json `file:` dependency points at.
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
    },
  };
});
