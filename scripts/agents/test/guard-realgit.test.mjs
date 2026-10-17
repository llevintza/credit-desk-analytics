// The real-git runner (testing.md "Guard build-work requirements"): a local bare remote with
// `main` and `feat`, a work clone on `feat` with a local `main` ahead, a clone on `main`, an
// isolated HOME and no network. Each row asserts the guard's exit code, then runs the command
// unguarded in a fresh copy and asserts whether remote `main` moved, so a pass can't hide a hole.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { decide } from "../hooks/lib/decide.mjs";
import rows from "./fixtures/guard-realgit.mjs";

for (const k of Object.keys(process.env)) if (/^GIT_/.test(k) || k === "XDG_CONFIG_HOME") delete process.env[k];
const ROOT = mkdtempSync(join(tmpdir(), "agents-realgit-"));
const TEMPLATE = join(ROOT, "template");
Object.assign(process.env, { HOME: join(ROOT, "home"), GIT_CONFIG_NOSYSTEM: "1", GIT_ALLOW_PROTOCOL: "file", GIT_TERMINAL_PROMPT: "0" });
mkdirSync(process.env.HOME);
writeFileSync(join(process.env.HOME, ".gitconfig"), "[user]\n\tname = t\n\temail = t@example.invalid\n[init]\n\tdefaultBranch = main\n[advice]\n\tdetachedHead = false\n");

const sh = (cmd, cwd) => spawnSync("bash", ["-c", cmd], { cwd, encoding: "utf8", timeout: 20000 });
const must = (cmd, cwd) => {
  const r = sh(cmd, cwd);
  assert.equal(r.status, 0, `${cmd}: ${r.stderr}`);
};
mkdirSync(TEMPLATE);
must([
  "git init -q --bare remote.git",
  "git clone -q remote.git seed && cd seed && git commit -q --allow-empty -m init && git push -q origin main",
  "git switch -q -c feat && git commit -q --allow-empty -m f && git push -q -u origin feat && cd ..",
  "git clone -q remote.git work && cd work && git remote set-url origin ../remote.git && git switch -q feat",
  "git switch -q main && git commit -q --allow-empty -m main-ahead && git switch -q feat && git commit -q --allow-empty -m feat-ahead && cd ..",
  "git clone -q remote.git onmain && cd onmain && git remote set-url origin ../remote.git && git commit -q --allow-empty -m on-main",
  "rm -rf seed",
].join(" && "), TEMPLATE);

const mainSha = (box) => execFileSync("git", ["rev-parse", "main"], { cwd: join(box, "remote.git"), encoding: "utf8" }).trim();

for (const r of rows) {
  test(`${r.id}: ${r.cmd} -> ${r.exit}`, () => {
    const box = mkdtempSync(join(ROOT, "box-"));
    cpSync(TEMPLATE, box, { recursive: true });
    const dir = join(box, r.repo);
    if (r.setup) must(r.setup, dir);
    const d = decide(JSON.stringify({ tool_name: "Bash", tool_input: { command: r.cmd }, cwd: dir }));
    assert.equal(d.code, r.exit, d.reason ?? "allowed");
    if (r.moves === null) return;
    const before = mainSha(box);
    sh(r.cmd, dir);
    const moved = mainSha(box) !== before;
    assert.equal(moved, r.moves, "the recorded ground truth");
    if (d.code === 0) assert.equal(moved, false, "an allowed command moved remote main");
  });
}

test("the fixture set is well formed", () => {
  assert.equal(new Set(rows.map((r) => r.id)).size, rows.length);
  for (const r of rows) assert.ok([0, 2].includes(r.exit) && [true, false, null].includes(r.moves) && ["work", "onmain"].includes(r.repo), r.id);
});
