import { useState } from "react";
import { makeIdentity, signIn, signOut, type DevIdentity } from "./devIdentity";

/**
 * The sign-in / sign-out surface for personal mode. **Not authentication** — see devIdentity.ts.
 *
 * TODO(plenipo#71): delete alongside devIdentity.ts when @plenipo/ui ships real sign-in.
 *
 * Kept deliberately plain, and it says what it is on screen: this picks which X-Dev-* headers the
 * app sends, which the API trusts because no identity provider is configured. It is a way to *use*
 * the product as a household member instead of as an omnipotent dev user — which is the thing that
 * was impossible before, and the reason the household roles had never been exercised through the UI.
 */

interface Preset {
  key: string;
  label: string;
  detail: string;
  identity: DevIdentity;
}

const PRESETS: Preset[] = [
  {
    key: "admin",
    label: "Ana — household admin",
    detail: "Runs the household: accounts, budgets, imports, integrations, and clearing approvals.",
    identity: makeIdentity({ subject: "ana", roles: "household-admin", name: "Ana" }),
  },
  {
    key: "member",
    label: "Sam — household member",
    detail: "Everyday use: sees the household, logs their own spending, proposes goal contributions.",
    identity: makeIdentity({ subject: "sam", roles: "household-member", name: "Sam" }),
  },
  {
    key: "operator",
    label: "Dev User — system admin",
    detail: "The identity the shell hard-codes. Holds '*', so it can never show you a permission boundary.",
    identity: makeIdentity({ subject: "dev-user", roles: "system_admin", name: "Dev User" }),
  },
  {
    key: "roleless",
    label: "Newcomer — no roles",
    detail: "A principal with no roles at all: what someone looks like before an invite or grant lands.",
    identity: makeIdentity({ subject: "newcomer", roles: "", name: "Newcomer" }),
  },
];

export function SignInScreen({ brandName }: { brandName: string }) {
  const [custom, setCustom] = useState({ subject: "", tenant: "dev", roles: "", name: "" });
  const canSubmitCustom = custom.subject.trim().length > 0;

  return (
    <div className="min-h-full bg-slate-50 px-4 py-10 dark:bg-slate-950">
      <div className="mx-auto w-full max-w-2xl space-y-6">
        <header className="space-y-2">
          <h1 className="text-2xl font-semibold text-slate-900 dark:text-slate-100">
            Sign in to {brandName}
          </h1>
          <p className="rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-700/60 dark:bg-amber-950/40 dark:text-amber-200">
            <strong>This is not authentication.</strong> No identity provider is configured, so the
            app simply tells the API who to treat you as. Anyone who can reach this page can pick any
            identity here. It is for local and single-household use only.
          </p>
        </header>

        <section className="space-y-3">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-slate-100">Continue as</h2>
          <ul className="space-y-2">
            {PRESETS.map((preset) => (
              <li key={preset.key}>
                <button
                  type="button"
                  onClick={() => signIn(preset.identity)}
                  className="focus-ring w-full rounded-lg border border-slate-200 bg-white p-4 text-left transition hover:border-brand-400 hover:bg-brand-50 dark:border-slate-700 dark:bg-slate-900 dark:hover:border-brand-500 dark:hover:bg-slate-800"
                >
                  <span className="block text-sm font-medium text-slate-900 dark:text-slate-100">
                    {preset.label}
                  </span>
                  <span className="mt-0.5 block text-sm text-slate-600 dark:text-slate-400">
                    {preset.detail}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </section>

        <details className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
          <summary className="focus-ring cursor-pointer text-sm font-semibold text-slate-900 dark:text-slate-100">
            Someone else
          </summary>
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            {(
              [
                ["subject", "Subject", "e.g. alex — the stable user id"],
                ["name", "Display name", "optional"],
                ["tenant", "Household (tenant slug)", "dev"],
                ["roles", "Roles", "comma-separated; blank for none"],
              ] as const
            ).map(([field, label, placeholder]) => (
              <label key={field} className="block text-sm">
                <span className="mb-1 block font-medium text-slate-700 dark:text-slate-200">{label}</span>
                <input
                  value={custom[field]}
                  placeholder={placeholder}
                  onChange={(event) => setCustom((prev) => ({ ...prev, [field]: event.target.value }))}
                  className="focus-ring w-full rounded border border-slate-300 bg-white px-2 py-1 text-slate-900 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
                />
              </label>
            ))}
          </div>
          <button
            type="button"
            disabled={!canSubmitCustom}
            onClick={() => signIn(makeIdentity(custom))}
            className="focus-ring mt-3 rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Continue
          </button>
          <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
            An unknown subject is provisioned on first request, so any value works. A household that
            does not exist resolves to no tenant, and then every permission-checked call is refused.
          </p>
        </details>
      </div>
    </div>
  );
}

/**
 * Who you are, and the way back out — a **row in the layout**, never an overlay.
 *
 * It used to be `fixed bottom-3 right-3`, which parked it on top of the chat composer's Send
 * button: 61% of Send was covered, and `elementFromPoint` at Send's centre returned this pill, so
 * the primary action of the product's primary screen was mostly unclickable. Moving it to another
 * corner only moves the collision — every corner of the shell is occupied by something (sidebar
 * nav, onboarding banner, top-bar controls), and which one depends on the route.
 *
 * So it does not float at all. `App.tsx` gives the shell a flex column and this bar the FIRST
 * track in it; the shell sizes itself with `h-full`, so it takes the remaining height and nothing
 * can ever be underneath this. First rather than last because the shell's mobile BottomNav is
 * `fixed`, which anchors it to the viewport and over a bottom track. Read the layout note in
 * App.tsx before making this `fixed` again, or moving it.
 */
export function IdentityBar({ identity }: { identity: DevIdentity }) {
  return (
    <div className="flex shrink-0 items-center gap-3 border-b border-slate-200 bg-slate-100 px-4 py-1.5 text-xs dark:border-slate-700 dark:bg-slate-800">
      <span className="min-w-0 truncate text-slate-600 dark:text-slate-300">
        Signed in as <span className="font-medium text-slate-900 dark:text-slate-100">{identity.name}</span>
        <span className="text-slate-500 dark:text-slate-400">
          {" · "}
          {identity.roles || "no roles"}
        </span>
      </span>
      <button
        type="button"
        onClick={signOut}
        className="focus-ring ml-auto shrink-0 rounded-full bg-slate-800 px-2.5 py-0.5 font-medium text-white hover:bg-slate-700 dark:bg-slate-200 dark:text-slate-900 dark:hover:bg-white"
      >
        Sign out
      </button>
    </div>
  );
}
