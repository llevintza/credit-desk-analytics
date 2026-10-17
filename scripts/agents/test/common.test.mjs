import { test } from "node:test";
import assert from "node:assert/strict";
import { capNames, escapeHtml, githubApi, globToRegExp, isSafePath, logUntrusted, matchesAny } from "../lib/common.mjs";

test("globs: ** spans directories, * and ? stay in one segment, matching is case- and NFC-folded", () => {
  assert.ok(matchesAny("AGENTS.md", ["**/AGENTS.md"]));
  assert.ok(matchesAny("src/Desk.Api/AGENTS.md", ["**/AGENTS.md"]));
  assert.ok(matchesAny("web/agents.md", ["**/AGENTS.md"]));
  assert.ok(!matchesAny("web/AGENTS.md.bak", ["**/AGENTS.md"]));
  assert.ok(matchesAny(".claude/skills/a/SKILL.md", [".claude/**"]));
  assert.ok(!matchesAny(".claude", [".claude/**"]));
  assert.ok(matchesAny(".github/workflows/ci.yaml", [".github/workflows/*.y*ml"]));
  assert.ok(!matchesAny(".github/workflows/sub/ci.yml", [".github/workflows/*.y*ml"]));
  assert.ok(matchesAny("perf/coverage-baseline.json", ["perf/coverage-*"]));
  assert.ok(matchesAny("a.md", ["?.md"]) && !matchesAny("ab.md", ["?.md"]));
  assert.ok(matchesAny("docs/agents/Café.md", ["docs/agents/café.md"]), "NFC");
  assert.ok(matchesAny("a+b(c).md", ["a+b(c).md"]), "regex metacharacters are literal");
  assert.equal(globToRegExp("**/x"), globToRegExp("**/x"), "cached");
});

test("isSafePath", () => {
  assert.ok(isSafePath("a/b.c"));
  for (const bad of [null, 1, "", "/a", "a/", "a\\b", "..", ".", "a/../b", "a\tb", "\u007f"]) assert.equal(isSafePath(bad), false, String(bad));
});

test("escapeHtml covers & < > \" ' ` | [ ]", () => {
  assert.equal(escapeHtml(`&<>"'\`|[]x`), "&amp;&lt;&gt;&quot;&#39;&#96;&#124;&#91;&#93;x");
});

test("capNames: 200 names of 300 characters, then +N more", () => {
  const names = Array.from({ length: 203 }, (_, i) => (i === 0 ? "y".repeat(301) : `n${i}`));
  const out = capNames(names);
  assert.equal(out.length, 201);
  assert.equal(out[0], `${"y".repeat(300)}…`);
  assert.equal(out[200], "+3 more");
  assert.deepEqual(capNames(["a"]), ["a"]);
});

test("logUntrusted: JSON-encoded lines inside a fresh stop-commands token", () => {
  const a = [];
  const b = [];
  logUntrusted((s) => a.push(s), ["::error::x", "a\r\nb"]);
  logUntrusted((s) => b.push(s), []);
  assert.match(a[0], /^::stop-commands::[0-9a-f]{32}\n$/);
  assert.deepEqual(a.slice(1, 3), ['"::error::x"\n', '"a\\r\\nb"\n']);
  assert.equal(a[3], `::${a[0].slice(17, 49)}::\n`);
  assert.notEqual(a[0], b[0]);
});

test("githubApi sends the token and API version only to api.github.com", async () => {
  const seen = [];
  const api = githubApi({ token: "tok", repo: "o/r", fetchImpl: async (url, init) => (seen.push([url, init]), { ok: true, json: async () => 1 }) });
  assert.equal(await api("pulls/1"), 1);
  assert.equal(seen[0][0], "https://api.github.com/repos/o/r/pulls/1");
  assert.equal(seen[0][1].headers.authorization, "Bearer tok");
  const anon = githubApi({ repo: "o/r", fetchImpl: async (url, init) => (seen.push([url, init]), { ok: true, json: async () => 2 }) });
  await anon("x");
  assert.equal(seen[1][1].headers.authorization, undefined);
  assert.throws(() => githubApi({ repo: undefined }), /owner\/repo/);
});
