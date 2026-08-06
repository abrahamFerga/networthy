import { useEffect, useState } from "react";
import { PlenipoApp, defineModule } from "@plenipo/ui";
import { IdentityBar, SignInScreen } from "./DevIdentityUi";
import { isDevAuthActive, readIdentity } from "./devIdentity";
import { BudgetsTab } from "./finance/BudgetsTab";
import { OverviewTab } from "./finance/OverviewTab";
import { RecurringTab } from "./finance/RecurringTab";
import { SpendingTab } from "./finance/SpendingTab";
import { TransactionsTab } from "./finance/TransactionsTab";

// Networthy's app entry (ADR-0008): the stock Plenipo shell plus the finance tabs that need
// more than the generic rendering — the Overview dashboard, issue #46's month-picking
// Spending donut, budget progress bars, and bills calendar, and SPEC v2.1's account-scopable
// Transactions ledger (each of which still composes GenericTab for the server-driven part).
// Every other tab stays fully server-driven.
const finance = defineModule("finance", {
  tabs: {
    overview: OverviewTab,
    spending: SpendingTab,
    budgets: BudgetsTab,
    recurring: RecurringTab,
    transactions: TransactionsTab,
  },
});

// Brand: baked at build (VITE_BRAND_NAME) for the first paint, superseded at runtime by the
// host's Branding:ProductName — the same contract as the stock shell.
const buildTimeBrand = (import.meta.env.VITE_BRAND_NAME as string | undefined) ?? "Networthy";

export default function App() {
  const [brandName, setBrandName] = useState(buildTimeBrand);
  // Sign-in gate (TODO(plenipo#71): remove with devIdentity.ts). `null` = still probing, so the
  // shell never flashes before we know whether a real identity provider is in play.
  const [devAuth, setDevAuth] = useState<boolean | null>(null);
  // Read once: signIn/signOut reload the page rather than mutating state, so there is no setter.
  const [identity] = useState(() => readIdentity());

  useEffect(() => {
    isDevAuthActive().then(setDevAuth);
  }, []);

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

  // With a real identity provider configured the API rejects the X-Dev-* scheme outright, so the
  // picker would be a lie — fall straight through to the shell (which is then unauthenticated;
  // that is plenipo#71, not something this gate can fix).
  if (devAuth === null) return null;
  if (devAuth && !identity) return <SignInScreen brandName={brandName} />;

  const shell = <PlenipoApp moduleUi={[finance]} branding={{ name: brandName }} />;
  if (!devAuth || !identity) return shell;

  // The identity bar gets its OWN track rather than floating over the shell. It used
  // to be `fixed bottom-3 right-3`, which parked it on the chat composer's Send button — 61% of
  // Send covered, and the topmost element at Send's centre was the badge. Re-cornering it only
  // moves the collision: the shell fills every corner with something (sidebar nav, onboarding
  // banner, top-bar controls) and which one depends on the route.
  //
  // ABOVE the shell, not below it, and that is the whole trick. The shell's mobile BottomNav is
  // `fixed inset-x-0 bottom-0`, so it anchors to the VIEWPORT and not to this column — a bar in
  // the last track sits under it and is unreachable below 768px. The first track is the one piece
  // of the layout no shell chrome can reach into.
  //
  // The shell's root is `h-full` (not `h-screen`), so it takes the height left over and its own
  // content area shrinks to match; `min-h-0` is load-bearing, since without it the flex item
  // refuses to shrink below its content and pushes the shell off-screen.
  return (
    <div className="flex h-full flex-col">
      <IdentityBar identity={identity} />
      <div className="min-h-0 flex-1">{shell}</div>
    </div>
  );
}
