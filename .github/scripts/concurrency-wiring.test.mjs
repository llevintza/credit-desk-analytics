import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";

// #207: main CI must never drop a push, and only the tip of main deploys.
// These assert the concurrency wiring in ci.yml, deploy.yml and db-ops.yml, so a
// regression to a shared main group (or a workflow-level production group) fails CI.
const read = (name) => readFileSync(new URL(`../workflows/${name}`, import.meta.url), "utf8");
const CI = read("ci.yml");
const DEPLOY = read("deploy.yml");
const DB_OPS = read("db-ops.yml");

const jobBlock = (yml, id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|^\\S|(?![\\s\\S]))`, "m").exec(yml);
  assert.ok(m, `job ${id} not found`);
  return m[1];
};
const jobIds = (yml) => [...yml.slice(yml.indexOf("\njobs:")).matchAll(/^  ([a-z][\w-]*):$/gm)].map((m) => m[1]);
// Top-level keys only (column 0), so job-level `concurrency:` does not count.
const topLevel = (yml, key) => new RegExp(`^${key}:`, "m").test(yml);
const PRODUCTION_JOB_CONCURRENCY = "    concurrency:\n      group: production\n      cancel-in-progress: false\n";

test("ci.yml: push runs get a per-sha group and are never cancelled; PR runs group by number and cancel", () => {
  const block = /^concurrency:\n((?: {2}.*\n|\s*#.*\n)+)/m.exec(CI);
  assert.ok(block, "ci.yml has a workflow-level concurrency block");
  const lines = block[1].split("\n").filter((l) => /^ {2}\w/.test(l));
  assert.deepEqual(lines, [
    "  group: ${{ github.event_name == 'push' && format('ci-push-{0}', github.sha) || format('ci-pr-{0}', github.event.pull_request.number) }}",
    "  cancel-in-progress: ${{ github.event_name == 'pull_request' }}",
  ]);
  // A ref-keyed group puts every main push (and a merged PR's `edited` run) in one queue.
  assert.doesNotMatch(block[1], /github\.ref/);
  assert.doesNotMatch(block[1], /cancel-in-progress: true/);
});

test("ci.yml: triggers stay push-to-main and pull_request only", () => {
  const on = CI.slice(CI.indexOf("\non:"), CI.indexOf("\nconcurrency:"));
  assert.match(on, /^ {2}push:\n {4}branches: \[main\]$/m);
  assert.match(on, /^ {2}pull_request:$/m);
  assert.doesNotMatch(on, /pull_request_target|workflow_dispatch|merge_group/);
});

// #243 gap 1: the exact trigger set, not a denylist. A schedule, workflow_call or repository_dispatch run
// has no PR number and no push sha of its own, so it would share one ci-pr- group.
const triggerProblems = (yml) => {
  const on = yml.slice(yml.indexOf("\non:") + 1, yml.indexOf("\nconcurrency:"));
  const keys = [...on.matchAll(/^ {2}([a-z_]+):/gm)].map((m) => m[1]).sort();
  const problems = [];
  if (JSON.stringify(keys) !== JSON.stringify(["pull_request", "push"])) problems.push(`triggers ${keys.join(",")}`);
  if (!/^ {2}push:\n {4}branches: \[main\]$/m.test(on)) problems.push("push.branches is not [main]");
  return problems;
};

test("ci.yml: the trigger set is exactly push (main) and pull_request (#243)", () => {
  assert.deepEqual(triggerProblems(CI), []);
  for (const extra of ["  schedule:\n    - cron: '0 3 * * *'\n", "  workflow_call:\n", "  repository_dispatch:\n"]) {
    const mutated = CI.replace(/\n {2}pull_request:\n/, `\n  pull_request:\n${extra}`);
    assert.notDeepEqual(triggerProblems(mutated), [], `adding ${extra.trim()} must fail`);
  }
  assert.notDeepEqual(triggerProblems(CI.replace("    branches: [main]", "    branches: [main, release]")), []);
});

// #243 gap 2: no CI job may hold its own concurrency group (a job-level group brings back a shared
// queue and pending replacement inside a run).
const jobConcurrency = (yml) => jobIds(yml).filter((id) => /^ {4}concurrency:/m.test(jobBlock(yml, id)));

test("ci.yml: no job-level concurrency in any job (#243)", () => {
  const ids = jobIds(CI);
  assert.ok(ids.length >= 10, `found ${ids.length} ci.yml jobs`);
  assert.deepEqual(jobConcurrency(CI), []);
  const mutated = CI.replace(/^( {2}api:\n)/m, "$1    concurrency:\n      group: shared\n");
  assert.deepEqual(jobConcurrency(mutated), ["api"]);
});

// #243 gap 3: a real step runs this file (a commented-out line must not satisfy the check).
const runsWiringTest = (yml) => /^ {8}run: node --test \.github\/scripts\/concurrency-wiring\.test\.mjs$/m.test(jobBlock(yml, "gate-tests"));

test("ci.yml: gate-tests runs this wiring test", () => {
  assert.ok(runsWiringTest(CI));
  const commented = CI.replace(/^ {8}run: node --test \.github\/scripts\/concurrency-wiring\.test\.mjs$/m, "        # run: node --test .github/scripts/concurrency-wiring.test.mjs\n        run: true");
  assert.equal(runsWiringTest(commented), false);
});

// #242: each Deploy run enters the production group exactly once, in release (migrate, seed, hook, smoke).
// preflight only checks that three values are non-empty and touches neither Neon nor Render, so holding the
// group there let a run's own release, queued later, replace the tip's pending preflight.
test("deploy.yml: only release holds the non-cancelling production group; gate and preflight stay outside", () => {
  assert.ok(!topLevel(DEPLOY, "concurrency"), "the production group must not be workflow-level in deploy.yml");
  const ids = jobIds(DEPLOY);
  assert.deepEqual(ids, ["gate", "preflight", "release"]);
  const release = jobBlock(DEPLOY, "release");
  assert.ok(release.includes(PRODUCTION_JOB_CONCURRENCY), "release must hold the production group");
  assert.match(release, /^ {4}needs: \[gate, preflight\]$/m);
  assert.match(release, /^ {4}if: needs\.gate\.outputs\.allowed == 'true' && needs\.preflight\.outputs\.configured == 'true'$/m);
  for (const id of ["gate", "preflight"]) assert.doesNotMatch(jobBlock(DEPLOY, id), /^ {4}concurrency:/m, `${id} must stay outside the group`);
  assert.match(jobBlock(DEPLOY, "preflight"), /^ {4}if: needs\.gate\.outputs\.allowed == 'true'$/m);
  assert.equal(DEPLOY.match(/group: production/g).length, 1, "one production group in deploy.yml");
});

// #243 gap 4: release's first step re-checks the tip, and every later step is gated on it, so an older sha
// that waited in the group never migrates, seeds or deploys over a newer one.
const releaseTipProblems = (yml) => {
  const release = jobBlock(yml, "release");
  const steps = release.slice(release.indexOf("    steps:\n")).split(/\n(?= {6}- )/).slice(1);
  const problems = [];
  if (!/^ {6}- name: Confirm SHA is still the tip of main\n {8}id: tip\n/.test(steps[0] ?? "")) problems.push("first step is not the tip check");
  for (const step of steps.slice(1)) {
    const name = /- (?:name: (.+)|uses: (\S+))/.exec(step);
    if (/^ {8}if: steps\.tip\.outputs\.allowed == 'true'$/m.test(step)) continue;
    // #302: the Render status read runs only after a failed, tip-gated smoke, so it keeps the tip clause literally.
    if (/^ {8}if: failure\(\) && steps\.tip\.outputs\.allowed == 'true' && steps\.smoke\.outputs\.timed_out == 'true'$/m.test(step)) continue;
    // The one exception: the always() summary, which reads `allowed` itself and stops first when it isn't true.
    const summary =
      name?.[1] === "Summary" &&
      /^ {8}if: always\(\)$/m.test(step) &&
      /ALLOWED: \$\{\{ steps\.tip\.outputs\.allowed \}\}/.test(step) &&
      /run: \|\n(?: {10}#.*\n)* {10}if \[ "\$ALLOWED" != true \]; then\n(?: {12}.*\n)*? {12}exit 0\n {10}fi\n/.test(step);
    if (!summary) problems.push(`ungated: ${name?.[1] ?? name?.[2]}`);
  }
  for (const needed of ["Migrate Neon", "Seed (only if the seed version or scale changed)", "Trigger Render deploy of this commit"])
    if (!steps.some((st) => st.includes(`- name: ${needed}\n`))) problems.push(`missing step ${needed}`);
  return problems;
};

test("deploy.yml: release re-checks the tip first and gates every later step on it (#243)", () => {
  assert.deepEqual(releaseTipProblems(DEPLOY), []);
  const ungated = DEPLOY.replace(/(- name: Migrate Neon\n) {8}if: steps\.tip\.outputs\.allowed == 'true'\n/, "$1");
  assert.deepEqual(releaseTipProblems(ungated), ["ungated: Migrate Neon"]);
  const renamed = DEPLOY.replace("- name: Confirm SHA is still the tip of main", "- name: Something else");
  assert.ok(releaseTipProblems(renamed).includes("first step is not the tip check"));
  const summaryRunsOn = DEPLOY.replace(/( {10}if \[ "\$ALLOWED" != true \]; then\n) {12}exit 0\n/, "$1            true\n");
  assert.deepEqual(releaseTipProblems(summaryRunsOn), ["ungated: Summary"]);
  const statusUntipped = DEPLOY.replace("if: failure() && steps.tip.outputs.allowed == 'true' && ", "if: failure() && ");
  assert.notEqual(statusUntipped, DEPLOY);
  assert.deepEqual(releaseTipProblems(statusUntipped), ["ungated: Read the Render deploy status (the /health smoke timed out)"]);
});

test("deploy.yml: release's non-tip skip warns and writes a summary (#242)", () => {
  const release = jobBlock(DEPLOY, "release");
  const skip = /if \[ "\$SHA" != "\$tip" \]; then\n([\s\S]*?)\n {10}else\n/.exec(release);
  assert.ok(skip, "release has a non-tip branch");
  assert.match(skip[1], /::warning::Nothing deployed: \$SHA is not the tip of main/);
  assert.doesNotMatch(skip[1], /::notice::/);
  assert.match(skip[1], /### Deploy skipped: \$\{SHA:0:7\} is not the tip of main/);
  assert.match(skip[1], /\} >> "\$GITHUB_STEP_SUMMARY"/);
  assert.match(skip[1], /echo "allowed=false" >> "\$GITHUB_OUTPUT"/);
});

test("deploy.yml: the gate runs outside the group, has no secrets, and a non-tip skip says nothing deployed", () => {
  const gate = jobBlock(DEPLOY, "gate");
  assert.doesNotMatch(gate, /environment:|secrets\./);
  const skip = /if \[ "\$SHA" != "\$tip" \]; then\n([\s\S]*?)\n {10}else\n/.exec(gate);
  assert.ok(skip, "gate has a non-tip branch");
  assert.match(skip[1], /::warning::Nothing deployed: \$SHA is not the tip of main/);
  assert.match(skip[1], /### Deploy skipped: \$\{SHA:0:7\} is not the tip of main/);
  assert.match(skip[1], /\} >> "\$GITHUB_STEP_SUMMARY"/);
  assert.match(skip[1], /echo "allowed=false" >> "\$GITHUB_OUTPUT"/);
  assert.doesNotMatch(skip[1], /allowed=true|exit 0/);
});

// #242: db-ops shares deploy's production group on its run job only, and its gate (outside the group) refuses
// while a Deploy run is in flight, so a dispatch can't become the pending run that replaces the tip's release.
test("db-ops.yml: only the run job holds the production group; the gate refuses during a Deploy", () => {
  assert.ok(!topLevel(DB_OPS, "concurrency"), "no workflow-level group in db-ops.yml");
  assert.deepEqual(jobIds(DB_OPS), ["gate", "run"]);
  assert.ok(jobBlock(DB_OPS.slice(DB_OPS.indexOf("\njobs:")), "run").includes(PRODUCTION_JOB_CONCURRENCY));
  assert.match(jobBlock(DB_OPS.slice(DB_OPS.indexOf("\njobs:")), "run"), /^ {4}needs: gate\n {4}if: needs\.gate\.outputs\.allowed == 'true'$/m);
  const gate = jobBlock(DB_OPS.slice(DB_OPS.indexOf("\njobs:")), "gate");
  assert.doesNotMatch(gate, /^ {4}concurrency:|environment:|secrets\./m);
  assert.match(gate, /^ {4}permissions:\n {6}contents: read\n {6}actions: read #/m);
  const step = gate.slice(gate.indexOf("- name: Refuse while a Deploy is in flight"));
  assert.match(step, /^ {8}if: steps\.tip\.outputs\.allowed == 'true'$/m);
  assert.match(step, /actions\/workflows\/deploy\.yml\/runs/);
  assert.match(step, /select\(\.status != "completed"\)/);
  assert.match(step, /\[ "\$active" -ne 0 \]; then\n {12}echo "::error::A Deploy run is in flight/);
  assert.match(step, /::error::Could not list Deploy runs; refusing DB ops \(fail closed\)/);
});

test("db-ops.yml: the in-flight refusal, run in bash against a stub gh", () => {
  const gate = jobBlock(DB_OPS.slice(DB_OPS.indexOf("\njobs:")), "gate");
  const step = gate.slice(gate.indexOf("- name: Refuse while a Deploy is in flight"));
  const body = /\n {8}run: \|\n((?: {10}.*\n?|\s*\n)+)/.exec(step)[1].split("\n").map((l) => l.slice(10)).join("\n");
  const run = (ghOut, ghRc = 0) =>
    spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", `gh() { printf '%s' '${ghOut}'; return ${ghRc}; }\n${body}`], {
      env: { PATH: process.env.PATH, GITHUB_REPOSITORY: "o/r" },
      encoding: "utf8",
    });
  assert.equal(run("0").status, 0);
  const busy = run("2");
  assert.equal(busy.status, 1);
  assert.match(busy.stdout, /::error::A Deploy run is in flight \(2 not completed\)/);
  assert.notEqual(run("", 1).status, 0, "a failed list fails closed");
  const garbage = run("null");
  assert.equal(garbage.status, 1);
  assert.match(garbage.stdout, /Could not list Deploy runs/);
});

// #321: every job runs on the pinned ubuntu-24.04 image, never a moving label. Moving to a
// newer image is a deliberate [workflows] PR after a verification run (README §14).
test("every workflow job runs on the pinned ubuntu-24.04 runner", () => {
  const files = readdirSync(new URL("../workflows/", import.meta.url)).filter((f) => /\.ya?ml$/.test(f));
  assert.ok(files.length > 0, "no workflow files found");
  let jobs = 0;
  for (const name of files) {
    const yml = read(name);
    assert.doesNotMatch(yml, /ubuntu-latest/, `${name} uses the moving ubuntu-latest label`);
    for (const id of jobIds(yml)) {
      jobs++;
      assert.match(jobBlock(yml.slice(yml.indexOf("\njobs:")), id), /^ {4}runs-on: ubuntu-24\.04$/m, `${name} job ${id} runs on ubuntu-24.04`);
    }
  }
  assert.ok(jobs >= 21, `found ${jobs} jobs; the job parser missed some`);
});
