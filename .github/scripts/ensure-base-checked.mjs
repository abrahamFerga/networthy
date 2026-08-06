#!/usr/bin/env node
// Keep the default branch verifiable after an unattended merge (verification ladder L1 — the
// check-run count decides it). No dependencies beyond `gh`, node >= 18.
//
//   node .github/scripts/ensure-base-checked.mjs             evaluate and dispatch if needed
//   node .github/scripts/ensure-base-checked.mjs --dry-run   say what it would do, dispatch nothing
//   node .github/scripts/ensure-base-checked.mjs --fixture f.json   evaluate fixture data (tests)
//
// ── Why this exists ──────────────────────────────────────────────────────────
// `agent-merge.yml` merges with `secrets.GITHUB_TOKEN`, and GitHub's anti-recursion rule says an
// event raised by that token does not start a new workflow run. `ci.yml` triggers on `push:
// branches: [main]`, so a bot merge produces a `main` commit carrying ZERO checks — and
// `merge-gate.mjs`'s `main_is_green` fails closed on exactly that, by design:
//
//     main_is_green: no checks have run on main — green would mean nothing
//
// Nothing else pushes to `main`, so every later merge is blocked forever. The merger locks itself
// out after exactly one merge. That is networthy#181.
//
// The fix is not to weaken `main_is_green` — failing closed on an unverified base is correct, and it
// is what caught this. The fix is to stop producing unverified bases: after the merge step, ask
// whether `main`'s head commit has any checks at all, and if it has none, dispatch `ci.yml` on it.
// `workflow_dispatch` and `repository_dispatch` are the two documented exceptions to the
// anti-recursion rule, which is why this works with GITHUB_TOKEN alone and needs no new secret.
//
// ── Two properties worth keeping ─────────────────────────────────────────────
// It is keyed on "has the head commit any checks", NOT on "did we just merge". That makes it
// self-healing: a `main` already stranded with no checks by an earlier bot merge is repaired on the
// next scheduled tick, rather than needing a human to push something. It is also idempotent — once
// a run exists on that commit it does nothing, so it cannot spam CI on the fifteen-minute schedule
// or duplicate the run a human's own push already started.
//
// It NEVER exits non-zero. A failed step in a scheduled workflow attaches a `failure` check-run to
// `main`'s head commit, which `main_is_green` then counts — so an exception escaping this script
// would deadlock the merge queue in precisely the way the script exists to prevent. Observed live
// once already on this repo: a non-required agentic job failed on `main` and froze every open PR.
// Loud on stdout, quiet in the exit code.

import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const i = argv.indexOf(name);
  return i === -1 ? undefined : argv[i + 1];
};

const FIXTURE = value('--fixture');
const DRY_RUN = flag('--dry-run') || Boolean(FIXTURE);
const WORKFLOW = value('--workflow') ?? 'ci.yml';

const gh = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  if (r.status !== 0) throw new Error(`gh ${args.join(' ')} failed:\n${r.stderr || r.stdout}`);
  return r.stdout;
};

// Exit code 0 always — see the header. `reason` is the machine-readable first token of every line
// this prints, so the workflow log and the tests read the same vocabulary.
const say = (reason, detail) => console.log(`${reason}: ${detail}`);

try {
  const ref =
    value('--ref') ??
    (FIXTURE ? 'main' : JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef'])).defaultBranchRef.name);

  // A fixture is the raw `check-runs` API shape, so the tests exercise the same parsing the live
  // call goes through rather than a re-implementation of it.
  const payload = FIXTURE
    ? JSON.parse(readFileSync(FIXTURE, 'utf8'))
    : JSON.parse(gh(['api', `repos/{owner}/{repo}/commits/${ref}/check-runs?per_page=100`]));
  const checks = payload.check_runs ?? [];

  if (checks.length > 0) {
    // Deliberately counts checks of ANY conclusion, including failures. "Is this base green?" is
    // `merge-gate.mjs`'s question and it owns it; the only question here is whether anything ran,
    // because a red base is a diagnosis and an unchecked one is a dead end.
    say('base_checked', `${checks.length} check-run(s) on ${ref} — nothing to dispatch`);
  } else if (DRY_RUN) {
    say('base_unchecked', `0 check-runs on ${ref} — would dispatch ${WORKFLOW} (dry run)`);
  } else {
    say('base_unchecked', `0 check-runs on ${ref} — dispatching ${WORKFLOW}`);
    gh(['workflow', 'run', WORKFLOW, '--ref', ref]);
    say('dispatched', `${WORKFLOW} on ${ref}`);
  }
} catch (err) {
  // Includes the dispatch itself failing — a missing `actions: write` scope is the likely cause, and
  // the next tick will retry because the condition is still true.
  say('dispatch_failed', `${err.message.split('\n')[0]} — the base stays unverified, retrying next tick`);
}

process.exit(0);
