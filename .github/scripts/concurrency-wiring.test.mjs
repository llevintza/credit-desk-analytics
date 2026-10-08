import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

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

test("ci.yml: gate-tests runs this wiring test", () => {
  assert.ok(jobBlock(CI, "gate-tests").includes(".github/scripts/concurrency-wiring.test.mjs"));
});

test("deploy.yml: no workflow-level concurrency; production jobs hold the non-cancelling production group", () => {
  assert.ok(!topLevel(DEPLOY, "concurrency"), "the production group must not be workflow-level in deploy.yml");
  const ids = jobIds(DEPLOY);
  assert.deepEqual(ids, ["gate", "preflight", "release"]);
  let production = 0;
  for (const id of ids) {
    const block = jobBlock(DEPLOY, id);
    if (/^ {4}environment: production$/m.test(block)) {
      production++;
      assert.ok(block.includes(PRODUCTION_JOB_CONCURRENCY), `${id} uses production and must hold the production group`);
      assert.match(block, /^ {4}needs: (gate|\[gate, preflight\])$/m, `${id} runs only after the tip gate`);
      assert.match(block, /^ {4}if: needs\.gate\.outputs\.allowed == 'true'/m, `${id} runs only for the tip`);
    } else {
      assert.doesNotMatch(block, /^ {4}concurrency:/m, `${id} has no production environment and must stay outside the group`);
    }
  }
  assert.equal(production, 2);
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

test("db-ops.yml: keeps the workflow-level non-cancelling production group shared with deploy", () => {
  assert.match(DB_OPS, /^concurrency:\n {2}group: production\n {2}cancel-in-progress: false$/m);
});
