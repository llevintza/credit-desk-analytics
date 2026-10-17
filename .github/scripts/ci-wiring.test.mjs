import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { JOBS } from "./ci-jobs.mjs";

// ADR-0026 (#451): the parse-only ci.yml wiring tests, moved out of ci-changes.test.mjs so
// `ci-wiring (base)` can run the base copy of this one file against a PR's ci.yml. These
// tests only PARSE ci.yml: no process, worker, vm, dynamic-import or eval route, and no
// import beyond the four above (`./ci-jobs.mjs` has no imports at all) (workflow-hardening.test.mjs checks it, and the protected
// job greps the base copy and runs it under node --permission). A test that executes a
// ci.yml step belongs in ci-changes.test.mjs.

// ci.yml must wire every JOBS flag to its job, keep always-run jobs unconditional,
// and never use workflow-level paths filters (required checks must always report).
const CI = readFileSync(new URL("../workflows/ci.yml", import.meta.url), "utf8");
const jobBlock = (id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|(?![\\s\\S]))`, "m").exec(CI);
  assert.ok(m, `job ${id} not found in ci.yml`);
  return m[1];
};
const JOB_IDS = {
  run_api: "api",
  run_web: "web",
  run_db_tools: "db-tools",
  run_compose_smoke: "compose-smoke",
  run_e2e: "e2e",
  run_budgets: "budgets",
  run_coverage: "coverage",
};

test("ci.yml: each heavy job needs changes and skips only on its own flag being 'false'", () => {
  assert.deepEqual(Object.keys(JOB_IDS).sort(), Object.keys(JOBS).sort());
  for (const [flag, id] of Object.entries(JOB_IDS)) {
    const block = jobBlock(id);
    assert.match(block, /^    needs: (changes|\[changes, api, web\])$/m, id);
    assert.match(block, /^    if: /m, id);
    assert.ok(block.includes("!cancelled()"), `${id} keeps reporting when a needed job is skipped`);
    assert.ok(block.includes(`needs.changes.outputs.${flag} != 'false'`), `${id} uses ${flag}`);
    if (block.includes("needs: [changes, api, web]")) {
      assert.ok(block.includes(`contains(fromJSON('["success", "skipped"]'), needs.api.result)`), id);
      assert.ok(block.includes(`contains(fromJSON('["success", "skipped"]'), needs.web.result)`), id);
    }
    assert.match(jobBlock("changes"), new RegExp(`^      ${flag}: \\$\\{\\{ steps\\.classify\\.outputs\\.${flag} \\}\\}$`, "m"));
  }
});

test("ci.yml: always-run jobs have no needs and no job-level if", () => {
  for (const id of ["secrets", "workflows", "gate-tests", "perf-deps"]) {
    const block = jobBlock(id);
    assert.doesNotMatch(block, /^    (needs|if):/m, id);
  }
  assert.ok(jobBlock("gate-tests").includes(".github/scripts/ci-changes.test.mjs"));
  assert.ok(jobBlock("gate-tests").includes('node --permission --allow-fs-read="$GITHUB_WORKSPACE" .github/scripts/ci-wiring.test.mjs'));
});

test("ci.yml: no workflow-level paths filters; classifier runs from the base checkout on PRs only", () => {
  const on = CI.slice(CI.indexOf("\non:"), CI.indexOf("\nconcurrency:"));
  assert.doesNotMatch(on, /paths(-ignore)?:/);
  const changes = jobBlock("changes");
  assert.ok(changes.includes("CLASSIFIER=_base/.github/scripts/ci-changes.mjs"));
  assert.ok(changes.includes("ref: ${{ github.event.pull_request.base.sha }}"));
  assert.ok(changes.includes("fetch-depth: 0"));
  assert.match(changes, /- id: classify\n        if: github\.event_name == 'pull_request'/);
  assert.doesNotMatch(CI, /pull_request_target/);
});

// #213: the classifier and the coverage gate diff from one base, pinned once in the
// changes job. On #195 the event base was older than the merge commit's first parent,
// so the gate saw another PR's src/ change that the classifier (rightly) did not.
const stepRun = (block, id) => {
  const m = new RegExp(`^      - id: ${id}\\n([\\s\\S]*?)(?=^      - |(?![\\s\\S]))`, "m").exec(block);
  assert.ok(m, `step ${id} not found`);
  const run = /^ {8}run: \|\n((?: {10}.*\n|\n)+)/m.exec(m[1]);
  assert.ok(run, `step ${id} has no run block`);
  return { step: m[1], script: run[1].replace(/^ {10}/gm, "") };
};

test("ci.yml: the changes job pins one diff base; the classifier and the coverage gate both use it (#213)", () => {
  const changes = jobBlock("changes");
  assert.match(changes, /^      base_sha: \$\{\{ steps\.base\.outputs\.base_sha \}\}$/m);
  const { step } = stepRun(changes, "base");
  // Runs on push too (no checkout there), so the gate always gets an output.
  assert.doesNotMatch(step, /^ {8}if:/m);
  assert.ok(changes.indexOf("- id: base") < changes.indexOf("- id: classify"), "base is pinned before classify");
  const classify = stepRun(changes, "classify");
  assert.ok(classify.step.includes("DIFF_BASE: ${{ steps.base.outputs.base_sha }}"));
  assert.match(classify.script, /^node "\$CLASSIFIER" --base "\$DIFF_BASE" --head "\$HEAD_SHA"$/m);
  // The rules still come from the event base, never from the pinned SHA or the head.
  assert.ok(changes.includes("ref: ${{ github.event.pull_request.base.sha }}"));

  const coverage = jobBlock("coverage");
  assert.match(coverage, /^    needs: \[changes, api, web\]$/m);
  assert.ok(
    coverage.includes(
      "DIFF_BASE: ${{ needs.changes.outputs.base_sha || github.event.pull_request.base.sha || github.event.before }}",
    ),
  );
  assert.ok(coverage.includes('--base "$DIFF_BASE"'));
  assert.doesNotMatch(coverage, /--base "\$BASE_SHA"/);
  // Gate script, thresholds and floor stay at the event base (_base).
  assert.ok(coverage.includes("ref: ${{ github.event.pull_request.base.sha || github.event.before }}"));
  assert.ok(coverage.includes('--default-sha "$BASE_SHA"'));
  assert.match(coverage, /for SHA in "\$\{BASE_SHA:-\}" "\$\{DIFF_BASE:-\}"; do/);
});

// Any job that needs a job that can be skipped must say how it handles the skip (R173-07,
// R173-12): without a job-level `!cancelled()` it is skipped silently whenever a needed job is.
const SKIPPABLE = new Set(["changes", ...Object.values(JOB_IDS)]);

/** Job ids and their raw blocks, from the `jobs:` section of a workflow. */
function workflowJobs(yml) {
  const lines = yml.slice(yml.indexOf("\njobs:\n") + 7).split("\n");
  const jobs = new Map();
  let current = null;
  for (const line of lines) {
    if (/^\s*(#.*)?$/.test(line)) {
      if (current) jobs.get(current).push(line);
      continue;
    }
    if (/^\S/.test(line)) break;
    const head = /^  ([A-Za-z_][\w-]*):\s*$/.exec(line);
    if (head) jobs.set((current = head[1]), []);
    else if (/^  \S/.test(line)) assert.fail(`unparsed job key: ${line}`);
    else if (current) jobs.get(current).push(line);
  }
  return [...jobs].map(([id, body]) => [id, body]);
}

/** `needs:` in inline (`x`, `[a, b]`) or block-list form; anything else fails, never skips. */
function jobNeeds(id, body) {
  const at = body.findIndex((l) => /^    needs:/.test(l));
  if (at < 0) return [];
  const value = body[at].replace(/^    needs:/, "").trim();
  if (/^\[[^\]]*\]$/.test(value)) {
    const xs = value.slice(1, -1).split(",").map((s) => s.trim()).filter(Boolean);
    for (const x of xs) if (!/^[\w-]+$/.test(x)) assert.fail(`${id}: unparsed needs entry ${x}`);
    return xs;
  }
  if (/^[\w-]+$/.test(value)) return [value];
  if (value === "") {
    const items = [];
    // Only the next key ends the list; anything else inside it fails.
    for (const l of body.slice(at + 1)) {
      if (/^ {0,4}[^\s#]/.test(l)) break;
      if (l.trim() === "") continue;
      const item = /^      - ([\w-]+)\s*$/.exec(l);
      if (!item) assert.fail(`${id}: unparsed needs item: ${l}`);
      items.push(item[1]);
    }
    if (items.length) return items;
  }
  assert.fail(`${id}: unparsed needs: ${body[at]}`);
}

/** The job-level `if:` only (plus folded continuation lines), never a step-level one or a comment. */
function jobIf(body) {
  const at = body.findIndex((l) => /^    if:/.test(l));
  if (at < 0) return "";
  const out = [body[at].replace(/^    if:/, "")];
  for (const l of body.slice(at + 1)) {
    if (!/^      /.test(l)) break;
    out.push(l);
  }
  return out.join("\n");
}

function checkSkippableDependents(yml) {
  let dependents = 0;
  for (const [id, body] of workflowJobs(yml)) {
    const skippable = jobNeeds(id, body).filter((n) => SKIPPABLE.has(n));
    if (!skippable.length) continue;
    dependents++;
    const cond = jobIf(body);
    // An expression never needs `#`: a comment on or under the if must not satisfy the checks.
    assert.doesNotMatch(cond, /#/, `${id}: its job-level if carries a comment`);
    assert.ok(cond.includes("!cancelled()"), `${id} needs [${skippable}] but its job-level if has no !cancelled()`);
    for (const n of skippable.filter((x) => x !== "changes")) {
      assert.ok(
        cond.includes(`contains(fromJSON('["success", "skipped"]'), needs.${n}.result)`),
        `${id} must accept success or skipped (and nothing else) from ${n} in its job-level if`,
      );
    }
  }
  return dependents;
}

test("ci.yml: every job that needs a skippable job has an explicit !cancelled() if", () => {
  assert.ok(workflowJobs(CI).length >= 11);
  assert.ok(checkSkippableDependents(CI) >= 7);
});

test("dependency checker: block-list needs, step-level ifs and comments do not pass", () => {
  const wf = (job) => `name: x\non: push\njobs:\n  api:\n    runs-on: ubuntu-latest\n${job}`;
  const blockList = "  summary:\n    runs-on: ubuntu-latest\n    needs:\n      - api\n    steps:\n      - run: echo\n";
  assert.throws(() => checkSkippableDependents(wf(blockList)), /summary needs \[api\]/);
  const stepIf =
    "  summary:\n    needs: [api]\n    # !cancelled() contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result)\n" +
    "    steps:\n      - if: ${{ !cancelled() && contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result) }}\n        run: echo\n";
  assert.throws(() => checkSkippableDependents(wf(stepIf)), /summary needs \[api\]/);
  const good =
    "  summary:\n    needs:\n      - api\n    if: >-\n      ${{ !cancelled()\n      && contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result) }}\n    steps:\n      - run: echo\n";
  assert.equal(checkSkippableDependents(wf(good)), 1);
  assert.throws(() => checkSkippableDependents(wf("  summary:\n    needs: ${{ fromJSON('[]') }}\n")), /summary: unparsed needs/);
  assert.throws(() => checkSkippableDependents(wf("  summary:\n    needs:\n    steps: []\n")), /summary: unparsed needs/);
});

test("dependency checker: unrecognised needs and job-key forms fail, never skip (R205-01)", () => {
  const wf = (job) => `name: x\non: push\njobs:\n  api:\n    runs-on: ubuntu-latest\n${job}`;
  const onlyCancelled = "    if: ${{ !cancelled() }}\n    steps:\n      - run: echo\n";
  const cases = [
    ["comment between items", "    needs:\n      - changes\n      # x\n      - api\n", /summary: unparsed needs item/],
    ["quoted item", '    needs:\n      - changes\n      - "api"\n', /summary: unparsed needs item/],
    ["trailing-comment item", "    needs:\n      - changes\n      - api # x\n", /summary: unparsed needs item/],
    ["quoted flow entry", "    needs: [changes, 'api']\n", /summary: unparsed needs entry 'api'/],
  ];
  for (const [name, needs, err] of cases) {
    assert.throws(() => checkSkippableDependents(wf(`  summary:\n${needs}${onlyCancelled}`)), err, name);
  }
  assert.throws(() => checkSkippableDependents(wf(`  summary: # x\n    needs: [api]\n${onlyCancelled}`)), /unparsed job key/);
  assert.throws(
    () => checkSkippableDependents(wf(`# a column-0 comment\n  summary:\n    needs: [api]\n${onlyCancelled}`)),
    /summary must accept success or skipped/,
  );
});

test("dependency checker: a comment on or under the job-level if fails (R205-02)", () => {
  const wf = (job) => `name: x\non: push\njobs:\n  api:\n    runs-on: ubuntu-latest\n${job}`;
  const text = `!cancelled() contains(fromJSON('["success", "skipped"]'), needs.api.result)`;
  for (const ifs of [`    if: \${{ success() }} # ${text}\n`, `    if: \${{ success() }}\n      # ${text}\n`]) {
    assert.throws(
      () => checkSkippableDependents(wf(`  summary:\n    needs: [api]\n${ifs}    steps:\n      - run: echo\n`)),
      /summary: its job-level if carries a comment/,
    );
  }
});
