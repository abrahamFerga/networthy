import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The Networthy app entry (ADR-0008): the Cortex shell + the custom Overview tab, built as a
// static bundle that Networthy.Host serves from wwwroot/app. Branding and API base bake in via
// VITE_BRAND_NAME / VITE_API_BASE, same contract as @cortex/ui's own app build.
export default defineConfig(() => {
  process.env.VITE_BRAND_NAME ??= "Networthy";
  return {
    plugins: [react()],
  };
});
