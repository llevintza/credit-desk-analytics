import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";

// #224: both production seeder runs pass the reseed peak cap (--cap-mb), the value comes in
// through env (never an inline expression in run:), and bash validates it before the seeder runs.
const read = (name) => readFileSync(new URL(`../workflows/${name}`, import.meta.url), "utf8");
const DEPLOY = read("deploy.yml");
const DB_OPS = read("db-ops.yml");

// One step, from its `- name:` (or `- uses:`) line to the next step or the end of the job.
const stepBlock = (yml, name) => {
  const start = yml.indexOf(`- name: ${name}`);
  assert.ok(start >= 0, `step "${name}" not found`);
  const rest = yml.slice(start + 1);
  const end = rest.search(/\n {6}- (name|uses):|\n {2}[a-z][\w-]*:\n|\n\S/);
  return end < 0 ? yml.slice(start) : yml.slice(start, start + 1 + end);
};
// The `run: |` body of a step, dedented, so the test can execute it.
const runBody = (step) => {
  const m = /\n( +)run: \|\n((?:\1 {2}.*\n?|\s*\n)+)/.exec(step);
  assert.ok(m, "step has a run: | block");
  const indent = m[1].length + 2;
  return m[2].split("\n").map((l) => l.slice(indent)).join("\n");
};
// The validation is everything before the seeder call: run it with the value and a stub seeder.
const validate = (script, env) =>
  spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", script], {
    env: { PATH: process.env.PATH, ...env },
    encoding: "utf8",
  });

const BAD = ["", "0", "00", "-1", "1.5", "512MB", "1e3", "1234567", " 512", "512 ", "$(id)", "512;id", "0x200"];
const GOOD = ["1", "512", "1024", "999999"];

test("deploy.yml: seed step passes --cap-mb from env, defaulting vars.SEED_CAP_MB to 512", () => {
  const step = stepBlock(DEPLOY, "Seed (only if the seed version or scale changed)");
  assert.match(step, /^ {10}SEED_CAP_MB: \$\{\{ vars\.SEED_CAP_MB \|\| '512' \}\}$/m);
  assert.match(step, /Desk\.Seeder --if-changed --scale "\$SEED_SCALE" --cap-mb "\$SEED_CAP_MB" \| tee seed\.txt/);
  // No expression inside the script itself (script injection).
  assert.doesNotMatch(runBody(step), /\$\{\{/);
});

test("deploy.yml: the seed step refuses a malformed SEED_CAP_MB before the seeder runs", () => {
  const body = runBody(stepBlock(DEPLOY, "Seed (only if the seed version or scale changed)"));
  const guard = body.slice(0, body.indexOf("./dbtools/seeder/Desk.Seeder"));
  assert.ok(guard.trim().length > 0, "a guard comes before the seeder call");
  for (const v of BAD) {
    const r = validate(guard, { SEED_CAP_MB: v });
    assert.notEqual(r.status, 0, `SEED_CAP_MB=${JSON.stringify(v)} must fail`);
    assert.match(r.stdout, /::error::vars\.SEED_CAP_MB must be a positive whole number/);
  }
  for (const v of GOOD) assert.equal(validate(guard, { SEED_CAP_MB: v }).status, 0, `SEED_CAP_MB=${v} must pass`);
});

test("db-ops.yml: cap_mb input defaults to 512 and reaches reseed only through env", () => {
  const inputs = DB_OPS.slice(DB_OPS.indexOf("    inputs:"), DB_OPS.indexOf("\nconcurrency:"));
  assert.match(inputs, /^ {6}cap_mb:\n {8}description: .+\n {8}default: "512"$/m);
  assert.match(DB_OPS, /^ {6}CAP_MB: \$\{\{ inputs\.cap_mb \}\}$/m);
  // inputs.cap_mb appears exactly once: the job-level env line.
  assert.equal(DB_OPS.match(/inputs\.cap_mb/g).length, 1);
  const run = runBody(stepBlock(DB_OPS, "Run"));
  assert.match(run, /reseed\) +\.\/dbtools\/seeder\/Desk\.Seeder --force --scale "\$SCALE" --cap-mb "\$CAP_MB" ;;/);
  assert.doesNotMatch(run, /\$\{\{/);
});

test("db-ops.yml: Guard refuses a malformed cap_mb before any database step", () => {
  const body = runBody(stepBlock(DB_OPS, "Guard"));
  const ok = { NEON_DATABASE_URL: "x", SCALE: "1.0", OPERATION: "reseed", CONFIRM: "RESEED-PRODUCTION" };
  for (const v of BAD) {
    const r = validate(body, { ...ok, CAP_MB: v });
    assert.notEqual(r.status, 0, `cap_mb=${JSON.stringify(v)} must fail`);
    assert.match(r.stdout, /::error::cap_mb must be a positive whole number/);
  }
  for (const v of GOOD) assert.equal(validate(body, { ...ok, CAP_MB: v }).status, 0, `cap_mb=${v} must pass`);
  // Guard runs before checkout, build and Run, so a bad value never reaches Neon.
  assert.ok(DB_OPS.indexOf("- name: Guard") < DB_OPS.indexOf("- name: Run"));
});
