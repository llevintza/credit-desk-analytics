import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import { githubApi } from "../lib/common.mjs";
import { BASELINE, evaluate, isGovernance, main, raiseOnly, summary } from "../governance.mjs";
import { BASE, HEAD, capture, eventFile, fakeGithub, scratch } from "./helpers.mjs";

const BASE_JSON = { dotnet: { line: 99.9, branch: 98.9 }, web: { line: 99.8, branch: 100 } };
const baseDir = (json = JSON.stringify(BASE_JSON)) => {
  const d = scratch();
  mkdirSync(join(d, "perf"));
  if (json !== null) writeFileSync(join(d, BASELINE), json);
  return d;
};
const mod = (...names) => names.map((filename) => ({ filename, status: "modified" }));
const event = (title = "feat(api): x") => ({ number: 7, headSha: HEAD, baseSha: BASE, title });
const run = ({ files, tree = {}, title, base = baseDir(), overrides } = {}) =>
  evaluate({ api: githubApi({ repo: "o/r", fetchImpl: fakeGithub({ files, tree, overrides }).fetchImpl }), event: event(title), baseDir: base });
const bump = (head, extra = [], opts = {}) =>
  run({ files: [...mod(BASELINE), ...extra], tree: { [BASELINE]: typeof head === "string" ? head : JSON.stringify(head) }, ...opts });
const raised = { dotnet: { line: 100, branch: 98.9 }, web: { line: 99.8, branch: 100 } };

test("the governance path list (governance.md), case-insensitive", () => {
  for (const p of [
    ".github/workflows/ci.yml", ".github/dependabot.yml", "deploy/start.sh", "render.yaml", "Dockerfile", "deploy/ci-images/x/Dockerfile",
    "web/app.Dockerfile", "docker-compose.yml", "e2e/docker-compose.e2e.yml", "compose.override.yaml", ".claude/settings.json",
    "src/x/.claude/a.json", ".cursor/cli.json", ".cursorignore", ".grok/agents/a.md", ".mcp.json", "AGENTS.md", "web/AGENTS.md",
    "src/Desk.UserAdmin/AGENTS.md", "src/Desk.UserAdmin/CLAUDE.md", "x/AGENT.md", "CLAUDE.local.md", ".claudeignore",
    "docs/agents/README.md", "scripts/agents/guard.mjs", ".gitleaks.toml", "perf/coverage-gate.mjs", "tests/testconfig.json",
    ".gitmodules", ".GITHUB/workflows/x.yml", "Web/agents.md", ".cursorrules", ".github", ".claude", "docs/agents", "scripts/agents", "deploy",
  ]) assert.ok(isGovernance(p), p);
  for (const p of ["docs", "scripts", ".githubx", "src/Desk.UserAdmin/UserAdminApp.cs", "src/Desk.Api/Program.cs", "README.md", "docs/adr/0001-stack.md", "perf/payload-size.mjs", "web/src/app.ts"])
    assert.ok(!isGovernance(p), p);
});

test("a governance path needs a [workflows] title; app-only PRs pass", async () => {
  assert.deepEqual(await run({ files: mod("src/Desk.UserAdmin/UserAdminApp.cs") }), { files: 1, matched: [], exempt: false, ok: true });
  const r = await run({ files: mod("src/Desk.UserAdmin/AGENTS.md") });
  assert.deepEqual([r.ok, r.matched], [false, ["src/Desk.UserAdmin/AGENTS.md"]]);
  assert.equal((await run({ files: mod("src/Desk.UserAdmin/AGENTS.md"), title: "[workflows] docs: x" })).ok, true);
  assert.equal((await run({ files: mod("e2e/docker-compose.e2e.yml") })).ok, false, "#157 recursive compose glob");
  assert.equal((await run({ files: mod("docker-compose.yml") })).ok, false);
  assert.equal((await run({ files: mod("x"), title: " [workflows]" })).ok, true, "no governance path, any title");
  assert.equal((await run({ files: mod(".github/x"), title: "[Workflows] x" })).ok, false, "the prefix is exact");
});

test("renames out of a governance path and deletions still match", async () => {
  const out = await run({ files: [{ filename: "tmp/ci.yml", previous_filename: ".github/workflows/ci.yml", status: "renamed" }] });
  assert.deepEqual(out.matched, [".github/workflows/ci.yml"]);
  assert.equal((await run({ files: [{ filename: "web/AGENTS.md", status: "removed" }] })).ok, false);
});

test("raise-only coverage-baseline bump: the exemption and its passes", async () => {
  const r = await bump(raised);
  assert.deepEqual([r.exempt, r.ok, r.matched], [true, true, []]);
  assert.equal((await bump(BASE_JSON)).exempt, true, "unchanged values count as raise-only");
  assert.equal((await bump(raised, mod("src/Desk.Api/Program.cs"))).exempt, true, "app code alongside is fine");
});

test("each must fail the exemption (the nine governance rows and the extras)", async () => {
  const fails = {
    lowered: () => bump({ ...raised, web: { line: 99.7, branch: 100 } }),
    "added key": () => bump({ ...raised, e2e: { line: 1, branch: 1 } }),
    "added nested key": () => bump({ ...raised, web: { line: 99.8, branch: 100, func: 1 } }),
    "deleted key": () => bump({ dotnet: raised.dotnet }),
    "unparseable head": () => bump("{ nope"),
    "unparseable base": () => bump(raised, [], { base: baseDir("{ nope") }),
    "missing base": () => bump(raised, [], { base: baseDir(null) }),
    "non line/branch field": () => bump({ ...raised, note: "x" }, [], { base: baseDir(JSON.stringify({ ...BASE_JSON, note: "y" })) }),
    "plus thresholds": () => bump(raised, mod("perf/coverage-thresholds.json")),
    "plus override": () => bump(raised, [{ filename: "perf/coverage-override.json", status: "added" }]),
    "plus testconfig": () => bump(raised, mod("tests/testconfig.json")),
    "plus vitest config": () => bump(raised, mod("web/vitest.config.mts")),
    "plus another governance path": () => bump(raised, mod("docs/agents/README.md")),
    "non-number value": () => bump({ ...raised, web: { line: "100", branch: 100 } }),
    "infinite value": () => bump('{"dotnet":{"line":1e999,"branch":98.9},"web":{"line":99.8,"branch":100}}'),
    "head is an array": () => bump([1]),
    added: () => run({ files: [{ filename: BASELINE, status: "added" }], tree: { [BASELINE]: JSON.stringify(raised) } }),
    deleted: () => run({ files: [{ filename: BASELINE, status: "removed" }] }),
    renamed: () => run({ files: [{ filename: BASELINE, previous_filename: "perf/old.json", status: "renamed" }] }),
    "case variant": () => run({ files: mod("perf/Coverage-Baseline.json"), tree: { "perf/Coverage-Baseline.json": "{}" } }),
    "head blob missing": () => run({ files: mod(BASELINE) }),
    "tree truncated": () => run({ files: mod(BASELINE), overrides: { [`git/trees/${HEAD}?recursive=1`]: { truncated: true, tree: [] } } }),
    "api error": () => run({ files: mod(BASELINE), overrides: { [`git/trees/${HEAD}?recursive=1`]: { status: 500 } } }),
  };
  for (const [name, f] of Object.entries(fails)) {
    const r = await f();
    assert.equal(r.exempt, false, name);
    assert.equal(r.ok, false, `${name}: needs a [workflows] title`);
  }
});

test("raiseOnly edge cases", () => {
  assert.equal(raiseOnly(null, {}), false);
  assert.equal(raiseOnly({ a: { line: 1 } }, { a: 2 }), false);
  assert.equal(raiseOnly([1], [1]), false, "arrays are not objects");
  assert.equal(raiseOnly({ a: [1] }, { a: [1] }), true, "an unchanged non-line/branch field is fine");
  assert.equal(raiseOnly({ a: [1] }, { a: [2] }), false);
  assert.equal(raiseOnly({ x: { line: 1, branch: 2 } }, { x: { branch: 2, line: 1.5 } }), true, "key order is irrelevant");
});

test("summary: escaped, capped table; the title is never printed", () => {
  const evil = "x|`<script>`[a](b)&'\"";
  const s = summary({ files: 2, matched: [evil], exempt: false, ok: false });
  assert.ok(s.includes("<code>x&#124;&#96;&lt;script&gt;&#96;&#91;a&#93;(b)&amp;&#39;&quot;</code>"));
  assert.match(s, /must start with `\[workflows\]`/);
  const many = summary({ files: 250, matched: Array.from({ length: 250 }, (_, i) => `.github/f${i}`), exempt: false, ok: true });
  assert.match(many, /\+50 more/);
  assert.match(summary({ files: 1, matched: [], exempt: true, ok: true }), /raise-only bump, exempt[\s\S]*No governance paths/);
});

test("main: exit codes, summary file, escaped names, and the title stays out of the output", async () => {
  const sum = join(scratch(), "summary.md");
  const env = (title) => ({ GITHUB_EVENT_PATH: eventFile({ title }), GITHUB_REPOSITORY: "o/r", GITHUB_STEP_SUMMARY: sum });
  const secretTitle = "feat: SECRET-TITLE ::warning::x";
  const fail = capture();
  const files = mod(".github/workflows/x.yml\n::error::spoof");
  // An unsafe name never gets this far: collectFiles rejects it first.
  assert.equal(await main([], env(secretTitle), { write: fail.write, fetchImpl: fakeGithub({ files }).fetchImpl }), 1);
  assert.match(fail.text(), /^::error::governance-paths: unsafe path in PR data\n::stop-commands::/);

  const no = capture();
  assert.equal(await main([], env(secretTitle), { write: no.write, fetchImpl: fakeGithub({ files: mod(".github/a.yml") }).fetchImpl }), 1);
  assert.match(no.text(), /^governance-paths: 1 changed files, 1 governance paths\n::stop-commands::(\w+)\n"\.github\/a\.yml"\n::\1::\n::error::governance paths changed/);
  assert.ok(!no.text().includes("SECRET-TITLE") && !readFileSync(sum, "utf8").includes("SECRET-TITLE"));

  const yes = capture();
  assert.equal(await main(["--base", baseDir()], env("[workflows] x"), { write: yes.write, fetchImpl: fakeGithub({ files: mod(".github/a.yml") }).fetchImpl }), 0);
  const ex = capture();
  const gh = fakeGithub({ files: mod(BASELINE), tree: { [BASELINE]: JSON.stringify(raised) } });
  assert.equal(await main(["--base", baseDir()], { ...env("perf: x"), GITHUB_STEP_SUMMARY: "" }, { write: ex.write, fetchImpl: gh.fetchImpl }), 0);
  assert.match(ex.text(), /0 governance paths, baseline bump exempt\n$/);

  const crash = capture();
  assert.equal(await main([], { GITHUB_EVENT_PATH: "/nonexistent" }, { write: crash.write }), 1);
  assert.match(crash.text(), /unexpected error \(fail closed\)/);
  const head = capture();
  assert.equal(await main([], env("x"), { write: head.write, fetchImpl: fakeGithub({ heads: ["c".repeat(40)] }).fetchImpl }), 1);
  assert.match(head.text(), /head moved/);
});

test("CLI entry point: run directly without an event fails closed", () => {
  const r = spawnSync(process.execPath, [fileURLToPath(new URL("../governance.mjs", import.meta.url))], { encoding: "utf8", env: {} });
  assert.equal(r.status, 1);
  assert.match(r.stdout, /^::error::governance-paths: unexpected error/);
});
