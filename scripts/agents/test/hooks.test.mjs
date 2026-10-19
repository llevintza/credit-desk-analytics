// format.mjs and quick-test.mjs (adapters.md "Hooks"). Both exit under CI on line 1, so they
// are never imported here (that would end this process in CI); every row spawns the hook with
// CI unset and a scratch repo, and fake formatters record what they were asked to do.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { delimiter, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { HOOK_ENTRIES } from "../lib/harness-policy.mjs";
import { scratch } from "./helpers.mjs";

const hook = (name) => fileURLToPath(new URL(`../hooks/${name}.mjs`, import.meta.url));
const FORMAT = hook("format");
const QUICK = hook("quick-test");
const GUARD = hook("guard");

function localEnv(extra = {}) {
  const env = { ...process.env, ...extra };
  delete env.CI;
  delete env.GITHUB_ACTIONS;
  delete env.NODE_TEST_CONTEXT; // the hook's own node --test must not report to this runner
  return env;
}
const run = (file, input, env) =>
  spawnSync(process.execPath, [file], { input: typeof input === "string" ? input : JSON.stringify(input), env, encoding: "utf8", timeout: 60_000 });

function put(root, files) {
  for (const [p, text] of Object.entries(files)) {
    mkdirSync(dirname(join(root, p)), { recursive: true });
    writeFileSync(join(root, p), text);
  }
}

/** A fake executable that appends its arguments to `log`, then exits with `code`. */
function fakeBin(path, log, code = 0) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `#!/usr/bin/env node\nrequire("fs").appendFileSync(${JSON.stringify(log)}, JSON.stringify(process.argv.slice(2)) + "\\n");\nprocess.exit(${code});\n`);
  chmodSync(path, 0o755);
}
const calls = (log) => (existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").map((l) => JSON.parse(l)) : []);

/** A scratch repo with a fake Prettier under web/ and a fake dotnet on PATH. */
function formatRepo({ prettierExit = 0 } = {}) {
  const root = scratch();
  const log = join(root, "calls.log");
  fakeBin(join(root, "web/node_modules/.bin/prettier"), log, prettierExit);
  fakeBin(join(root, "bin/dotnet"), log);
  const env = localEnv({ CLAUDE_PROJECT_DIR: root, PATH: [join(root, "bin"), process.env.PATH].join(delimiter) });
  return { root, log, env };
}

test("CI canary rows: format and quick-test only write the canary; the guard writes it and still blocks", () => {
  const { root, log } = formatRepo();
  const tmp = scratch();
  const ci = (extra) => ({ ...localEnv({ CLAUDE_PROJECT_DIR: root, RUNNER_TEMP: tmp }), ...extra });
  const write = { tool_name: "Write", tool_input: { file_path: "web/src/a.ts", content: "x" } };
  for (const [file, extra] of [[FORMAT, { CI: "true" }], [QUICK, { GITHUB_ACTIONS: "true" }], [FORMAT, { GITHUB_ACTIONS: "true" }]])
    assert.equal(run(file, write, ci(extra)).status, 0);
  const g = run(GUARD, { tool_name: "Bash", tool_input: { command: "git push origin main" } }, ci({ CI: "1" }));
  assert.equal(g.status, 2, g.stderr);
  assert.deepEqual(readFileSync(join(tmp, "agents-hooks-ran"), "utf8").split("\n"), ["format", "quick-test", "format", "guard", ""]);
  assert.deepEqual(calls(log), [], "no formatter ran under CI");
  // GITHUB_ACTIONS other than "true" is not CI; an unwritable canary still exits 0.
  assert.equal(run(FORMAT, write, ci({ GITHUB_ACTIONS: "false", RUNNER_TEMP: join(tmp, "missing") })).status, 0);
  assert.equal(calls(log).length, 1);
  for (const file of [FORMAT, QUICK]) assert.equal(run(file, write, ci({ CI: "true", RUNNER_TEMP: join(tmp, "missing") })).status, 0);
  // Without RUNNER_TEMP the canary goes to the OS temp dir (TMPDIR points it at a scratch dir).
  const own = scratch();
  const noRunner = { ...ci({ CI: "true", TMPDIR: own, TMP: own, TEMP: own }) };
  delete noRunner.RUNNER_TEMP;
  assert.equal(run(FORMAT, write, noRunner).status, 0);
  assert.equal(readFileSync(join(own, "agents-hooks-ran"), "utf8"), "format\n");
  assert.equal(calls(log).length, 1);
});

test("format: a Cursor-shape Write and a Claude-shape Edit format the one web file with Prettier", () => {
  const { root, log, env } = formatRepo();
  assert.equal(run(FORMAT, { tool_name: "Write", tool_input: { path: "web/src/app/a.ts", contents: "x" } }, env).status, 0); // Cursor field names
  assert.equal(run(FORMAT, { tool_name: "Edit", tool_input: { file_path: join(root, "web/src/b.scss"), old_string: "a", new_string: "b" } }, env).status, 0);
  assert.equal(run(FORMAT, { toolName: "MultiEdit", toolInput: { file_path: "web/README.md", edits: [] } }, env).status, 0);
  assert.deepEqual(calls(log), [
    ["--write", "--log-level", "warn", join(root, "web/src/app/a.ts")],
    ["--write", "--log-level", "warn", join(root, "web/src/b.scss")],
    ["--write", "--log-level", "warn", join(root, "web/README.md")],
  ]);
});

test("format: without CLAUDE_PROJECT_DIR (Cursor) the repo root is the hook's cwd", () => {
  const { root, log, env } = formatRepo();
  delete env.CLAUDE_PROJECT_DIR;
  const r = spawnSync(process.execPath, [FORMAT], { cwd: root, env, encoding: "utf8", input: JSON.stringify({ tool_name: "Write", tool_input: { path: "web/src/a.ts", contents: "x" } }) });
  assert.equal(r.status, 0);
  assert.equal(calls(log).length, 1);
  assert.match(calls(log)[0][3], /web\/src\/a\.ts$/);
});

test("format: *.cs goes to dotnet format whitespace for that file only", () => {
  const { log, env } = formatRepo();
  assert.equal(run(FORMAT, { tool_name: "Write", tool_input: { file_path: "src/Desk.Api/Program.cs", content: "x" } }, env).status, 0);
  assert.deepEqual(calls(log), [["format", "whitespace", "--folder", "--include", "src/Desk.Api/Program.cs"]]);
});

test("format: nothing runs for other tools, other files, paths outside the repo or bad input; it never blocks", () => {
  const { root, log, env } = formatRepo();
  const rows = [
    { tool_name: "Read", tool_input: { file_path: "web/src/a.ts" } },
    { tool_name: "Bash", tool_input: { command: "ls" } },
    { tool_name: "Write", tool_input: { file_path: "docs/a.md", content: "x" } },
    { tool_name: "Write", tool_input: { file_path: "web/src/a.png", content: "x" } },
    { tool_name: "Write", tool_input: { file_path: "../web/src/a.ts", content: "x" } },
    { tool_name: "Write", tool_input: { file_path: join(dirname(root), "web/src/a.ts"), content: "x" } },
    { tool_name: "Write", tool_input: { file_path: root, content: "x" } },
    { tool_name: "Write", tool_input: { file_path: dirname(root), content: "x" } },
    { tool_name: "Write", tool_input: { file_path: "" } },
    { tool_name: "Write", tool_input: { file_path: 7 } },
    { tool_name: "Write" },
    null,
  ];
  for (const r of rows) assert.equal(run(FORMAT, r, env).status, 0, JSON.stringify(r));
  assert.equal(run(FORMAT, "not json", env).status, 0);
  assert.deepEqual(calls(log), []);
});

test("format: a missing Prettier or a failing formatter is reported on stderr and still exits 0", () => {
  const failing = formatRepo({ prettierExit: 3 });
  const r = run(FORMAT, { tool_name: "Write", tool_input: { file_path: "web/src/a.ts", content: "x" } }, failing.env);
  assert.equal(r.status, 0);
  assert.match(r.stderr, /agents format: web\/src\/a\.ts was not formatted \(exit 3\); not blocking/);
  const bare = localEnv({ CLAUDE_PROJECT_DIR: scratch() });
  assert.equal(run(FORMAT, { tool_name: "Write", tool_input: { file_path: "web/src/a.ts", content: "x" } }, bare).stderr, "");
  const noDotnet = run(FORMAT, { tool_name: "Write", tool_input: { file_path: "src/A.cs", content: "x" } }, { ...bare, PATH: "" });
  assert.equal(noDotnet.status, 0);
  assert.match(noDotnet.stderr, /src\/A\.cs was not formatted \(ENOENT\)/);
});

const git = (root, ...args) => {
  const r = spawnSync("git", ["-C", root, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", ...args], { encoding: "utf8" });
  assert.equal(r.status, 0, r.stderr);
};

test("quick-test: runs node --test next to changed scripts and reminds about the .NET and web suites", () => {
  const root = scratch();
  git(root, "init", "-q");
  put(root, { "xx/src/Old.cs": "x", "keep.txt": "x" });
  git(root, "add", "-A");
  git(root, "commit", "-q", "-m", "init");
  git(root, "mv", "xx/src/Old.cs", "xx/Old.txt"); // the old path of a rename is not a changed src/ file
  put(root, {
    "scripts/agents/test/a.test.mjs": 'import { test } from "node:test";\ntest("a", () => {});\n',
    "perf/x.mjs": "export {};\n",
    "perf/x.test.mjs": 'import { test } from "node:test";\nimport assert from "node:assert";\ntest("x", () => assert.ok(false));\n',
    "perf/untested.mjs": "export {};\n",
    "web/src/app.ts": "x",
  });
  const r = run(QUICK, "", localEnv({ CLAUDE_PROJECT_DIR: root }));
  assert.equal(r.status, 0);
  const lines = r.stderr.trim().split("\n").sort();
  assert.deepEqual(lines, [
    "agents quick-test: also run cd web && npm run lint && npm test -- --watch=false",
    "agents quick-test: perf/x.test.mjs: 0 pass, 1 fail",
    "agents quick-test: scripts/agents/test/*.test.mjs: 1 pass, 0 fail",
  ]);
  put(root, { "src/Desk.Api/A.cs": "x" });
  assert.match(run(QUICK, "", localEnv({ CLAUDE_PROJECT_DIR: root })).stderr, /also run dotnet test -c Release \(Docker required\)/);
});

test("quick-test: outside a git repo, or with nothing changed, it is silent and exits 0", () => {
  const r = run(QUICK, "", localEnv({ CLAUDE_PROJECT_DIR: scratch() }));
  assert.equal(r.status, 0);
  assert.equal(r.stderr, "");
});

test("the configured guard command runs from a subdirectory and still blocks (#528 review)", () => {
  // Claude runs hook commands through a shell in the session's current directory.
  const root = fileURLToPath(new URL("../../..", import.meta.url));
  const sh = (cwd, env, command) => spawnSync("sh", ["-c", HOOK_ENTRIES.PreToolUse.command], { cwd, env, encoding: "utf8", input: JSON.stringify({ tool_name: "Bash", tool_input: { command } }), timeout: 30_000 });
  const env = localEnv({ CLAUDE_PROJECT_DIR: root });
  const sub = join(root, "web");
  assert.equal(sh(sub, env, "git push --force origin main").status, 2);
  assert.equal(sh(sub, env, "git status").status, 0);
  const unset = localEnv();
  delete unset.CLAUDE_PROJECT_DIR;
  assert.equal(sh(root, unset, "git push --force origin main").status, 2, "unset (Cursor): relative to the root, where Cursor runs hooks");
});
