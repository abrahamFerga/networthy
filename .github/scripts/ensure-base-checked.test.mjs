#!/usr/bin/env node --test
// Tests for the base-verification step (verification ladder L1 — an exit code decides it).
//
//   node --test .github/scripts/
//
// Like `merge-gate.test.mjs`, it drives the real script as a subprocess against committed fixtures,
// so what is proven is the code path the scheduled workflow runs rather than a re-implementation of
// it that can drift.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SCRIPT = resolve(HERE, 'ensure-base-checked.mjs');
const NONE = resolve(HERE, 'fixtures', 'base-checks-none.json');
const PRESENT = resolve(HERE, 'fixtures', 'base-checks-present.json');
const AGENT_MERGE = resolve(HERE, '..', 'workflows', 'agent-merge.yml');

// A fixture run never dispatches, so these tests need no `gh` on PATH and no token.
function run(...args) {
  const r = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });
  return { status: r.status, out: r.stdout, err: r.stderr };
}

// Assert on the leading token of a line rather than on prose: the message is for a human reading the
// workflow log and may be reworded; the token is the contract.
const reason = (out) => out.split('\n').map((l) => l.split(':')[0].trim()).filter(Boolean);

test('a base with no checks at all is the condition to dispatch on', () => {
  // The state a GITHUB_TOKEN merge leaves behind, and the one `main_is_green` fails closed on.
  const r = run('--fixture', NONE);
  assert.equal(r.status, 0);
  assert.ok(reason(r.out).includes('base_unchecked'), r.out);
});

test('a base that already has checks is left alone, even when one of them failed', () => {
  // Whether the base is GREEN is `merge-gate.mjs`'s question. Confusing the two would re-run CI on
  // every tick forever against a base that is red for a real reason.
  const r = run('--fixture', PRESENT);
  assert.equal(r.status, 0);
  assert.ok(reason(r.out).includes('base_checked'), r.out);
  assert.ok(!reason(r.out).includes('base_unchecked'), r.out);
});

test('--dry-run decides but never dispatches', () => {
  const r = run('--fixture', NONE, '--dry-run');
  assert.equal(r.status, 0);
  assert.ok(reason(r.out).includes('base_unchecked'), r.out);
  assert.ok(!reason(r.out).includes('dispatched'), r.out);
});

test('it never exits non-zero, because its own failure would deadlock the queue', () => {
  // This is the property, not a nicety. A failed step in a scheduled workflow attaches a `failure`
  // check-run to `main`'s head commit; `main_is_green` counts every failing check-run on that
  // commit, so a throw here blocks every open PR — the exact deadlock this script exists to
  // prevent, re-created by the cure. Observed live on this repo once already.
  const r = run('--fixture', resolve(HERE, 'fixtures', 'does-not-exist.json'));
  assert.equal(r.status, 0, `expected exit 0, got ${r.status}: ${r.err}`);
  assert.ok(reason(r.out).includes('dispatch_failed'), r.out);
});

test('the merger actually runs it, and is granted the scope to dispatch', () => {
  // The behaviour above is worthless if nothing calls it. `workflow_dispatch` needs `actions:
  // write`; without that scope the dispatch 403s and the base stays unverified.
  const yml = readFileSync(AGENT_MERGE, 'utf8');

  assert.match(yml, /^\s*actions:\s*write\s*$/m, 'agent-merge.yml does not grant `actions: write`');

  // Matched on the `run:` line, not on the bare filename: the header comments name both scripts, and
  // an assertion that a comment satisfies is an assertion about nothing.
  const merge = yml.search(/^\s*run:\s*node \.github\/scripts\/merge-gate\.mjs --merge\s*$/m);
  const ensure = yml.search(/^\s*run:\s*node \.github\/scripts\/ensure-base-checked\.mjs\s*$/m);
  assert.notEqual(merge, -1, 'agent-merge.yml no longer runs the merger');
  assert.notEqual(ensure, -1, 'agent-merge.yml does not run ensure-base-checked.mjs');
  assert.ok(ensure > merge, 'ensure-base-checked.mjs must run AFTER the merge, not before it');

  // `if: always()` — the merge step can fail after landing a commit (the cap, a delete-branch
  // error), and that is precisely when the base is left unverified.
  const tail = yml.slice(merge);
  assert.match(tail, /if:\s*always\(\)/, 'the ensure step must run even when the merge step failed');
});
