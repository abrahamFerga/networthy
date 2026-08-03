#!/usr/bin/env node --test
// Tests for the merger's gate list (verification ladder L1 — an exit code decides it).
//
//   node --test .github/scripts/
//
// It drives the real `merge-gate.mjs` as a subprocess against committed fixtures, so what is proven
// here is the same code path the scheduled workflow runs — not a re-implementation of it that can
// drift. Autonomy level is supplied by writing a `workflow.json` into a temp cwd, because the script
// reads policy from the repo and must never take it from an env var an agent could set.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const GATE = resolve(HERE, 'merge-gate.mjs');
const CASES = resolve(HERE, 'fixtures', 'merge-gate-cases.json');
const BASE_RED = resolve(HERE, 'fixtures', 'merge-gate-base-red.json');
const BASE_UNCHECKED = resolve(HERE, 'fixtures', 'merge-gate-base-unchecked.json');

// Run the gate at a given autonomy level and return { 900: { verdict, reasons }, ... }.
function run(level, fixture = CASES) {
  const cwd = mkdtempSync(join(tmpdir(), 'merge-gate-'));
  writeFileSync(join(cwd, 'workflow.json'), JSON.stringify({ autonomy: { level, maxMergesPerTick: 2 } }));

  const r = spawnSync(process.execPath, [GATE, '--fixture', fixture], { cwd, encoding: 'utf8' });
  assert.equal(r.status, 0, `merge-gate exited ${r.status}:\n${r.stderr}`);

  const verdicts = {};
  let current = null;
  for (const line of r.stdout.split('\n')) {
    const head = /^\s*(READY|BLOCK|MERGED|HELD)\s+#(\d+)/.exec(line);
    if (head) {
      current = { verdict: head[1], reasons: [] };
      verdicts[Number(head[2])] = current;
      continue;
    }
    const reason = /^\s*-\s+(\S+):/.exec(line);
    if (reason && current) current.reasons.push(reason[1]);
  }
  return verdicts;
}

// `fail` asserts the PR is blocked AND names the gate that blocked it. Asserting only "blocked"
// would pass for the right reason and the wrong one alike, which is how a gate rots.
const ready = (v, n) => assert.equal(v[n]?.verdict, 'READY', `#${n}: ${JSON.stringify(v[n])}`);
const fail = (v, n, gate) => {
  assert.equal(v[n]?.verdict, 'BLOCK', `#${n} should be blocked, got ${JSON.stringify(v[n])}`);
  assert.ok(v[n].reasons.includes(gate), `#${n} blocked, but not on ${gate}: ${v[n].reasons}`);
};

test('level 3: a dependabot patch or minor bump merges on green checks alone', () => {
  const v = run(3);
  ready(v, 900); // patch  — 5.101.2 -> 5.101.4
  ready(v, 901); // minor  — 18.7.0  -> 18.8.1
});

test('level 3: a major bump still needs a review', () => {
  fail(run(3), 902, 'agent_approved'); // 5.4.0 -> 6.0.0
});

test('level 3: a pre-1.0 minor bump counts as major — 0.x promises nothing', () => {
  fail(run(3), 912, 'agent_approved'); // 0.83.4 -> 0.84.1
});

test('level 3: a multi-package bump carries no versions, so it is not auto-mergeable', () => {
  fail(run(3), 903, 'agent_approved');
});

test('the lane is keyed on the author, not the branch name', () => {
  // A human can push any branch name they like. If `dependabot/` alone opened the lane, the
  // envelope and review gates would be bypassable by anyone with push access.
  fail(run(3), 904, 'is_loop_pr');
});

test('every other gate still applies to a dependency bump', () => {
  const v = run(3);
  fail(v, 905, 'checks_green');  // red build
  fail(v, 906, 'no_human_hold'); // human-hold label
  fail(v, 910, 'not_draft');     // draft
  fail(v, 911, 'checks_exist');  // no checks ran at all
});

test('a major bump merges once a review has labelled it', () => {
  // "Bump actions/checkout from 5 to 7" — unparseable version, so it lands in the review lane
  // rather than the automatic one, and the `agent:approved` label is what releases it.
  ready(run(3), 909);
});

test('level 1 permits docs and tests only — never a dependency bump', () => {
  const v = run(1);
  fail(v, 900, 'level_permits');
  fail(v, 901, 'level_permits');
});

test('level 0 merges nothing at all', () => {
  const v = run(0);
  for (const n of [900, 901, 908]) fail(v, n, 'level_permits');
});

test('a loop PR is unchanged by the dependabot lane', () => {
  const v = run(3);
  fail(v, 907, 'agent_approved'); // the PR #165 case: green everywhere, never reviewed
  ready(v, 908); // the same PR once a review labelled it
});

test('nothing merges onto a red base, even when a later workflow went green', () => {
  // The defect this pins: sampling the most recent run of ANY workflow on the base branch reported
  // green because a scheduled agentic workflow happened to finish last, while `Build & test` on the
  // same commit had failed. Both PRs here are otherwise perfectly mergeable.
  const v = run(3, BASE_RED);
  fail(v, 950, 'main_is_green');
  fail(v, 951, 'main_is_green');
});

test('an unverified base is not a green one', () => {
  // The query ran and came back empty. Reading that as green is the same mistake as trusting a PR
  // with no checks, which `checks_exist` already refuses.
  const v = run(3, BASE_UNCHECKED);
  fail(v, 950, 'main_is_green');
  fail(v, 951, 'main_is_green');
});
