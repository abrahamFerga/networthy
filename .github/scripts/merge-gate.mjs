#!/usr/bin/env node
// The merger. Deterministic (verification ladder L1/L2) — no dependencies beyond `gh`, node >= 18.
//
//   node .github/scripts/merge-gate.mjs                 evaluate every open PR, print verdicts
//   node .github/scripts/merge-gate.mjs --pr 131         evaluate one
//   node .github/scripts/merge-gate.mjs --merge          merge what passes, up to the cap
//   node .github/scripts/merge-gate.mjs --fixture f.json evaluate fixture data (used to test itself)
//
// Two lanes. A feature PR needs a loop branch, the plenipo-agent envelope and the `agent:approved`
// label a review adds. A dependabot PR has none of those by construction, so it is judged on the
// bump instead: patch and minor merge on green checks alone, major and anything unparseable fall
// back to the review lane. `merge-gate.test.mjs` is the executable statement of both.
//
// This file is the ONE implementation of the merge gates. `/plenipo:ship` runs it rather than
// re-deriving the list in prose, and `agent-merge.yml` runs it on a schedule so merging keeps
// working when the machine that wrote the code is off. An agent can be argued out of a judgement;
// an exit code cannot.
//
// It deliberately does NOT re-check the PR body or the diff. Those are `pr-gates.mjs`, running as a
// REQUIRED status check — so `checks_green` already subsumes them. If `pr-gates` is not required on
// the default branch, this gate is weaker than it looks: `checks_exist` is the only thing standing
// between you and a vacuous green.

import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const i = argv.indexOf(name);
  return i === -1 ? undefined : argv[i + 1];
};

const DO_MERGE = flag('--merge');
const ONE_PR = value('--pr');
const FIXTURE = value('--fixture');

const gh = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  if (r.status !== 0) throw new Error(`gh ${args.join(' ')} failed:\n${r.stderr || r.stdout}`);
  return r.stdout;
};

// ── Policy, read from the repo — never inferred ───────────────────────────────
// An agent that decides it has earned autonomy is the self-approving loop wearing a different hat.
// Absent config means level 0: review and label, merge nothing.
const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const autonomy = cfg.autonomy ?? {};
const LEVEL = Number.isInteger(autonomy.level) ? autonomy.level : 0;
const MAX_MERGES = autonomy.maxMergesPerTick ?? 2;

const LOOP_BRANCH = /^(feat|fix|chore)\//;
const HOLD_LABELS = ['human-hold', 'needs-human', 'agent:blocked'];
// Docs, tests and the runbook are the only class a level-1 product may land on its own.
const LOW_RISK = [/\.md$/i, /^tests\//, /\.http$/i, /^\.http$/i];

// ── The dependency lane ──────────────────────────────────────────────────────
// Dependabot writes its own branch names and its own body, so the loop envelope cannot apply to it.
// What stands in for that envelope is the bump itself: for a patch or minor release of a stable
// package, a green `Build & test` IS the proof — there is no behaviour to exercise and no regression
// test to have seen red. A major bump has no such promise, so it falls back to the review lane.
//
// Keyed on the AUTHOR, never on the branch name alone: `dependabot/**` is a string anyone with push
// access can type, and if the prefix alone opened the lane it would be a way around both the
// envelope gate and the review gate.
const DEPENDABOT_BRANCH = /^dependabot\//;
const DEPENDABOT_LOGINS = new Set(['app/dependabot', 'dependabot[bot]', 'dependabot']);
const DEPENDABOT_MIN_LEVEL = 2;

// 'patch' | 'minor' | 'major' | 'unknown', read from dependabot's own title format.
// Anything this cannot parse — a multi-package bump ("Bump react, react-dom and @types/react"), or
// an action pinned by major only ("from 5 to 7") — comes back 'unknown' and is treated as a major.
// Guessing low on an unreadable title is the one error here that merges something unreviewed.
function bumpClass(title) {
  const m = /\bfrom v?(\d+)\.(\d+)\.[\w.+-]+ to v?(\d+)\.(\d+)\.[\w.+-]+/i.exec(title ?? '');
  if (!m) return 'unknown';
  const [fromMajor, fromMinor, toMajor, toMinor] = m.slice(1).map(Number);
  if (fromMajor !== toMajor) return 'major';
  // Below 1.0 semver promises nothing across a minor, so 0.83 -> 0.84 is a breaking change.
  if (fromMajor === 0) return fromMinor === toMinor ? 'patch' : 'major';
  return fromMinor === toMinor ? 'patch' : 'minor';
}

const PR_FIELDS = [
  'number', 'title', 'body', 'isDraft', 'headRefName', 'baseRefName', 'labels',
  'mergeable', 'mergeStateStatus', 'reviewDecision', 'statusCheckRollup', 'files', 'author',
].join(',');

// ── Load the pull requests ───────────────────────────────────────────────────
let prs;
if (FIXTURE) {
  prs = JSON.parse(readFileSync(FIXTURE, 'utf8'));
} else if (ONE_PR) {
  prs = [JSON.parse(gh(['pr', 'view', ONE_PR, '--json', PR_FIELDS]))];
} else {
  prs = JSON.parse(gh(['pr', 'list', '--state', 'open', '--limit', '50', '--json', PR_FIELDS]));
}

// ── The default branch must be green before anything lands on it ─────────────
// Merging onto a red base multiplies one failure into N, and the next agent cannot tell which
// change broke what.
let mainGreen = true;
let mainWhy = '';
if (!FIXTURE) {
  const base = prs[0]?.baseRefName ?? JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef']))
    .defaultBranchRef.name;
  const runs = JSON.parse(gh(['run', 'list', '--branch', base, '--limit', '1', '--json', 'conclusion,name']));
  if (runs.length && runs[0].conclusion && runs[0].conclusion !== 'success') {
    mainGreen = false;
    mainWhy = `the last run on ${base} concluded "${runs[0].conclusion}"`;
  }
}

// ── Gates ────────────────────────────────────────────────────────────────────
function evaluate(pr) {
  const fail = [];
  const labels = (pr.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name).toLowerCase());
  const checks = pr.statusCheckRollup ?? [];
  const files = (pr.files ?? []).map((f) => f.path ?? f.filename ?? '');

  const state = (c) => (c.conclusion || c.state || c.status || '').toUpperCase();
  const pending = checks.filter((c) => ['PENDING', 'IN_PROGRESS', 'QUEUED', 'WAITING', ''].includes(state(c)));
  const broken = checks.filter((c) => ['FAILURE', 'ERROR', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED'].includes(state(c)));

  const isLowRisk = files.length > 0 && files.every((f) => LOW_RISK.some((re) => re.test(f)));

  const isDependabot =
    DEPENDABOT_BRANCH.test(pr.headRefName) &&
    DEPENDABOT_LOGINS.has((pr.author?.login ?? '').toLowerCase());
  const bump = isDependabot ? bumpClass(pr.title) : null;
  // The only class that merges without anything having reviewed it.
  const selfMergeable = bump === 'patch' || bump === 'minor';

  const changeClass = isDependabot ? `dependency:${bump}` : isLowRisk ? 'low-risk' : 'feature';

  if (!isDependabot) {
    if (!LOOP_BRANCH.test(pr.headRefName)) fail.push(`is_loop_pr: "${pr.headRefName}" is not a loop branch — not ours to merge`);
    if (!/plenipo-agent/.test(pr.body ?? '')) fail.push('is_loop_pr: the body carries no plenipo-agent envelope');
  }
  if (pr.isDraft) fail.push('not_draft: the PR is a draft');
  if (checks.length === 0) fail.push('checks_exist: no status checks ran — green would mean nothing');
  if (pending.length) fail.push(`checks_green: ${pending.length} check(s) still running`);
  if (broken.length) fail.push(`checks_green: ${broken.map((c) => c.name || c.context).join(', ')} not passing`);
  if (pr.mergeable && pr.mergeable !== 'MERGEABLE') fail.push(`mergeable: mergeable=${pr.mergeable}`);
  if (['DIRTY', 'BLOCKED', 'BEHIND'].includes(pr.mergeStateStatus)) fail.push(`mergeable: mergeStateStatus=${pr.mergeStateStatus}`);
  if (pr.reviewDecision === 'CHANGES_REQUESTED') fail.push('no_blocking_review: a review requested changes');
  if (!selfMergeable && !labels.includes('agent:approved')) {
    fail.push(
      !isDependabot
        ? 'agent_approved: no `agent:approved` label — nothing has reviewed this'
        : bump === 'unknown'
          ? 'agent_approved: no readable version pair in the title, so this counts as a major bump — no `agent:approved` label'
          : `agent_approved: a ${bump} bump is not self-mergeable — no \`agent:approved\` label`
    );
  }
  if (labels.includes('agent:changes-requested')) fail.push('agent_approved: `agent:changes-requested` is still set');
  for (const h of HOLD_LABELS) if (labels.includes(h)) fail.push(`no_human_hold: \`${h}\` is set`);
  if (!mainGreen) fail.push(`main_is_green: ${mainWhy}`);

  if (LEVEL === 0) fail.push('level_permits: autonomy level 0 merges nothing — a human decides');
  else if (isDependabot) {
    if (LEVEL < DEPENDABOT_MIN_LEVEL) {
      fail.push(`level_permits: a dependency bump needs autonomy level ${DEPENDABOT_MIN_LEVEL}; this repo is at ${LEVEL}`);
    }
  } else if (LEVEL === 1 && changeClass !== 'low-risk') {
    fail.push('level_permits: level 1 may merge docs, tests and the runbook only');
  }

  return { pr, fail, changeClass };
}

const results = prs.map(evaluate).sort((a, b) => a.pr.number - b.pr.number);

// ── Report, then act ─────────────────────────────────────────────────────────
console.log(`autonomy level ${LEVEL} · ${results.length} open PR(s) · cap ${MAX_MERGES}/run\n`);

let merged = 0;
for (const { pr, fail, changeClass } of results) {
  if (fail.length === 0) {
    if (!DO_MERGE) {
      console.log(`  READY  #${pr.number} ${pr.title} [${changeClass}]`);
    } else if (merged >= MAX_MERGES) {
      console.log(`  HELD   #${pr.number} — under_cap: ${MAX_MERGES} already merged this run`);
    } else {
      gh(['pr', 'merge', String(pr.number), '--squash', '--delete-branch']);
      merged++;
      console.log(`  MERGED #${pr.number} ${pr.title} [${changeClass}]`);
    }
  } else {
    console.log(`  BLOCK  #${pr.number} ${pr.title} [${changeClass}]`);
    for (const f of fail) console.log(`         - ${f}`);
  }
}

const blocked = results.filter((r) => r.fail.length).length;
console.log(`\n${results.length - blocked} ready · ${blocked} blocked · ${merged} merged\n`);
// Blocked PRs are the normal state of a healthy queue, not an error — a non-zero exit here would
// turn "waiting for CI" into a failed scheduled run every fifteen minutes.
process.exit(0);
