import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// #198: a perf-deps job exercises Dependabot /perf bumps; #348: budgets checks the bundle budget's
// estimate against the bytes the compose stack's API really sends.
const CI = readFileSync(new URL("../workflows/ci.yml", import.meta.url), "utf8");
const jobBlock = (id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|(?![\\s\\S]))`, "m").exec(CI);
  assert.ok(m, `job ${id} not found in ci.yml`);
  return m[1];
};

test("#198: perf-deps installs perf/ from its lockfile and round-trips the msgpack API, least privilege", () => {
  const job = jobBlock("perf-deps");
  assert.match(job, /^ {4}runs-on: ubuntu-24\.04$/m);
  assert.match(job, /^ {4}permissions:\n {6}contents: read\n/m);
  assert.doesNotMatch(job, /\$\{\{\s*secrets\.|^ {4}environment:/m);
  assert.match(job, /^ {8}working-directory: perf$/m);
  assert.match(job, /uses: actions\/checkout@[0-9a-f]{40} # v/);
  assert.match(job, /persist-credentials: false/);
  assert.match(job, /uses: actions\/setup-node@[0-9a-f]{40} # v/);
  assert.match(job, /- run: npm ci --no-audit --no-fund\n/);
  assert.match(job, /node --check parse-bench\.mjs/);
  assert.match(job, /import \{ encode, decode \} from '@msgpack\/msgpack'/);
  assert.match(job, /process\.exit\(1\)/);
});

test("#348: budgets copies the served SPA out of the app container and runs bundle-wire-bytes.mjs on it", () => {
  const job = jobBlock("budgets");
  const step = job.slice(job.indexOf("- name: Bundle wire bytes vs the bundle-budget estimate"));
  assert.ok(step.length > 0, "a wire-bytes step");
  const run = step.slice(0, step.search(/\n {6}[-#]/));
  assert.match(run, /docker compose -f docker-compose\.yml -f "\$RUNNER_TEMP\/budgets-limits\.yml" cp app:\/app\/wwwroot "\$RUNNER_TEMP\/spa"/);
  assert.match(run, /BASE_URL=http:\/\/localhost:8080 node perf\/bundle-wire-bytes\.mjs "\$RUNNER_TEMP\/spa" \| tee -a "\$GITHUB_STEP_SUMMARY"/);
  assert.doesNotMatch(run, /continue-on-error/);
  // After the payload step (the stack is up and /health answered), before k6.
  assert.ok(job.indexOf("- name: Payload budgets") < job.indexOf("- name: Bundle wire bytes"));
  assert.ok(job.indexOf("- name: Bundle wire bytes") < job.indexOf("- name: API latency p95 (k6)"));
});
