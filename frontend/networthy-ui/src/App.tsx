import { useEffect, useState } from "react";
import { CortexApp, defineModule } from "@cortex/ui";
import { OverviewTab } from "./finance/OverviewTab";

// Networthy's app entry (ADR-0008): the stock Cortex shell plus ONE custom tab — the finance
// Overview dashboard. Every other tab keeps the server-driven generic rendering, so this file
// stays this small on purpose.
const finance = defineModule("finance", {
  tabs: {
    overview: OverviewTab,
  },
});

// Brand: baked at build (VITE_BRAND_NAME) for the first paint, superseded at runtime by the
// host's Branding:ProductName — the same contract as the stock shell.
const buildTimeBrand = (import.meta.env.VITE_BRAND_NAME as string | undefined) ?? "Networthy";

export default function App() {
  const [brandName, setBrandName] = useState(buildTimeBrand);

  useEffect(() => {
    fetch("/api/platform/branding")
      .then((res) => (res.ok ? (res.json() as Promise<{ name?: string }>) : null))
      .then((body) => {
        if (body?.name) setBrandName(body.name);
      })
      .catch(() => {
        // API not up yet — the baked brand stands.
      });
  }, []);

  useEffect(() => {
    document.title = brandName;
  }, [brandName]);

  return <CortexApp moduleUi={[finance]} branding={{ name: brandName }} />;
}
