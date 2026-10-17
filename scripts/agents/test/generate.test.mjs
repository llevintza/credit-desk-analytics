import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { LOCK, diff, loadManifest, main, parseGitleaksRules, render, renderCursorCli, renderCursorignore, renderSecretPatterns } from "../generate.mjs";
import { ASK, MAX_ALLOW, MAX_ALLOW_CURSOR, MIN_DENY, MIN_DENY_CURSOR } from "../lib/harness-policy.mjs";
import { SETTINGS, capture, scratch } from "./helpers.mjs";

const MAP = readFileSync(new URL("../permission-map.json", import.meta.url), "utf8");
const GITLEAKS = readFileSync(new URL("../../../.gitleaks.toml", import.meta.url), "utf8");


test("the policy counts: MIN_DENY 71, MIN_DENY_CURSOR 70, six allows, seven asks", () => {
  assert.equal(MIN_DENY.length, 71);
  assert.equal(MIN_DENY_CURSOR.length, 70);
  assert.equal(new Set(MIN_DENY).size, 71);
  assert.deepEqual([MAX_ALLOW.length, MAX_ALLOW_CURSOR.length, ASK.length], [6, 6, 7]);
  assert.ok(MIN_DENY.includes("Bash(git push)") && MIN_DENY.includes("Bash(git push *--force-*)"));
  assert.ok(!MIN_DENY_CURSOR.some((e) => e === "Shell(git:push)" || e === "Shell(git:push*)"));
});

test("golden cli.json: colon form, the 70 Cursor denies, no bare push, Edit->Write, no !", () => {
  const cli = JSON.parse(renderCursorCli(JSON.stringify(SETTINGS), MAP)).permissions;
  assert.deepEqual(cli.deny, [...MIN_DENY_CURSOR]);
  assert.deepEqual(cli.allow, [...MAX_ALLOW_CURSOR]);
  assert.ok(cli.deny.every((e) => !e.includes("!") && !/^Shell\([^:)]* /.test(e)));
  assert.ok(cli.deny.includes("Shell(curl)") && cli.deny.includes("Shell(git:push *--force-*)"));
  const web = JSON.parse(renderCursorCli(JSON.stringify({ permissions: { allow: ["WebFetch(domain:docs.github.com)"] } }), MAP));
  assert.deepEqual(web, { permissions: { allow: ["WebFetch(docs.github.com)"], deny: [] } });
  assert.throws(() => renderCursorCli(JSON.stringify({ permissions: { deny: ["Bash(rm -rf *)"] } }), MAP), /no permission-map.json entry for Bash\(rm -rf \*\)/);
});

test("golden .cursorignore and secret-patterns.txt", () => {
  assert.equal(renderCursorignore(JSON.stringify(SETTINGS)), "# GENERATED from .claude/settings.json by scripts/agents/generate.mjs — do not edit\n**/.env\n**/.env.*\n**/secrets/**\n");
  const sp = renderSecretPatterns(GITLEAKS).split("\n");
  assert.equal(sp[0], "# GENERATED from .gitleaks.toml by scripts/agents/generate.mjs — do not edit");
  assert.deepEqual(sp.slice(1, -1).map((l) => l.split("\t")[0]), ["connection-string-password", "postgres-uri-password", "postgres-password-literal"]);
  assert.equal(readFileSync(new URL("../hooks/secret-patterns.txt", import.meta.url), "utf8"), renderSecretPatterns(GITLEAKS), "committed file is current");
  const rules = parseGitleaksRules(`[[rules]]\nid = "a"\nregex = "x\\\\d"\n[[rules]]\nid = 'b'\nregex = 'y'\n[[allowlists]]\nid = "skip"\n`);
  assert.deepEqual(rules, [{ id: "a", regex: "x\\d" }, { id: "b", regex: "y" }]);
  assert.throws(() => parseGitleaksRules('[[rules]]\nid = "a"\nregex = """\nmulti\n"""\n'), /lacks a one-line id or regex/);
});

test("render: absent sources render nothing; the lock hashes sources; diff lists stale outputs", () => {
  const manifest = loadManifest();
  const files = { ".gitleaks.toml": GITLEAKS };
  const read = (p) => files[p] ?? null;
  const out = render(manifest, read);
  assert.deepEqual([...out.keys()].sort(), [LOCK, "scripts/agents/hooks/secret-patterns.txt"]);
  assert.deepEqual(render(manifest, () => null).size, 0, "no sources, no lock");
  assert.deepEqual(diff(manifest, read).sort(), [LOCK, "scripts/agents/hooks/secret-patterns.txt"]);
  Object.assign(files, Object.fromEntries(out));
  assert.deepEqual(diff(manifest, read), []);
  files[".cursor/cli.json"] = "{}";
  assert.deepEqual(diff(manifest, read), [".cursor/cli.json"], "an output whose source is gone must not exist");
  files[".claude/settings.json"] = JSON.stringify(SETTINGS);
  files["scripts/agents/permission-map.json"] = MAP;
  const full = render(manifest, read);
  const lock = JSON.parse(full.get(LOCK));
  assert.equal(Object.keys(lock).length, 3);
  // A bare 64-hex value after a path containing "secret" trips gitleaks' generic-api-key rule.
  for (const v of Object.values(lock)) assert.match(v, /^sha256:[0-9a-f]{64}$/);
  assert.throws(() => render([{ path: "x", sources: [".gitleaks.toml"], render: "nope" }], read), /unknown renderer nope/);
});

const tree = (files) => {
  const d = scratch();
  for (const [p, t] of Object.entries(files)) {
    mkdirSync(dirname(join(d, p)), { recursive: true });
    writeFileSync(join(d, p), t);
  }
  return d;
};

test("main: write, then --check passes; a hand edit or a bad source fails", () => {
  const d = tree({ ".gitleaks.toml": GITLEAKS, ".claude/settings.json": JSON.stringify(SETTINGS), "scripts/agents/permission-map.json": MAP });
  const w = capture();
  assert.equal(main(["--root", d], { write: w.write }), 0);
  assert.match(w.text(), /wrote 4 files/);
  const ok = capture();
  assert.equal(main(["--check", "--root", d], { write: ok.write }), 0);
  writeFileSync(join(d, ".cursorignore"), "hand edit\n");
  const bad = capture();
  assert.equal(main(["--check", "--root", d], { write: bad.write }), 1);
  assert.match(bad.text(), /::error::generated file out of date: \.cursorignore/);
  writeFileSync(join(d, ".claude/settings.json"), "{ nope");
  const e = capture();
  assert.equal(main(["--root", d], { write: e.write }), 1);
  assert.match(e.text(), /^::error::agents generate: /);
  const gone = tree({});
  assert.equal(main(["--root", gone], { write: capture().write }), 0, "nothing to render, nothing written");
  const r = spawnSync(process.execPath, [fileURLToPath(new URL("../generate.mjs", import.meta.url)), "--check"], { encoding: "utf8", cwd: fileURLToPath(new URL("../../../", import.meta.url)) });
  assert.equal(r.status, 0, r.stdout);
});
