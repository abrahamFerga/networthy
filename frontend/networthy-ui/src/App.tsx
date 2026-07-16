import { useEffect, useState } from "react";
import { PlenipoApp, defineModule } from "@plenipo/ui";
import { BudgetsTab } from "./finance/BudgetsTab";
import { OverviewTab } from "./finance/OverviewTab";
import { RecurringTab } from "./finance/RecurringTab";
import { SpendingTab } from "./finance/SpendingTab";

// Networthy's app entry (ADR-0008): the stock Plenipo shell plus the finance tabs that need
// more than the generic rendering — the Overview dashboard, and issue #46's month-picking
// Spending donut, budget progress bars, and bills calendar (each of which still composes
// GenericTab for the server-driven part). Every other tab stays fully server-driven.
const finance = defineModule("finance", {
  tabs: {
    overview: OverviewTab,
    spending: SpendingTab,
    budgets: BudgetsTab,
    recurring: RecurringTab,
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

  return <PlenipoApp moduleUi={[finance]} branding={{ name: brandName }} />;
}
