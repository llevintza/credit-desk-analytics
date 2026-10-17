import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync, statSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import { CheckError, githubApi } from "../lib/common.mjs";
import { MAX_BLOB_BYTES, decodeBlob, fetchPrData, loadAdapters, main, writeData } from "../pr-data.mjs";
import { BASE, HEAD, blob, capture, eventFile, fakeGithub, scratch } from "./helpers.mjs";

const adapters = loadAdapters();
const event = { number: 7, headSha: HEAD, baseSha: BASE, title: "t" };
const run = (gh, outDir = scratch()) =>
  fetchPrData({ api: githubApi({ repo: "o/r", fetchImpl: gh.fetchImpl }), event, adapters, outDir }).then((n) => ({ n, outDir }));
const rejects = (gh, re) => assert.rejects(run(gh), (e) => e instanceof CheckError && re.test(e.message));
const files = (...names) => names.map((filename) => ({ filename, status: "modified" }));

test("writes steering blobs as data, plus paths.json and files.json", async () => {
  const gh = fakeGithub({
    files: [...files("AGENTS.md"), { filename: "web/AGENTS.md", previous_filename: "web/agents.md", status: "renamed" }],
    tree: { "AGENTS.md": "# root\n", "web/AGENTS.md": "# web\n", "src/app.cs": "class A {}", "docs/agents/x.md": "x" },
  });
  const { n, outDir } = await run(gh);
  assert.deepEqual(n, { files: 2, entries: 4, blobs: 3 });
  assert.equal(readFileSync(join(outDir, "tree/AGENTS.md"), "utf8"), "# root\n");
  assert.equal(readFileSync(join(outDir, "tree/web/AGENTS.md"), "utf8"), "# web\n");
  assert.equal(existsSync(join(outDir, "tree/src/app.cs")), false, "non-steering blobs are never fetched");
  assert.equal(statSync(join(outDir, "tree/AGENTS.md")).mode & 0o777, 0o644);
  const paths = JSON.parse(readFileSync(join(outDir, "paths.json"), "utf8"));
  assert.deepEqual(Object.keys(paths[0]), ["path", "mode", "type", "size", "sha"]);
  assert.equal(JSON.parse(readFileSync(join(outDir, "files.json"), "utf8"))[1].previous_filename, "web/agents.md");
  assert.ok(!gh.calls.some((c) => c.includes("patch")), "never reads the patch field");
  assert.equal(gh.calls.filter((c) => /^pulls\/7$/.test(c)).length, 2, "head re-checked at the end");
});

test("paginates the file list and asserts the count", async () => {
  const many = Array.from({ length: 250 }, (_, i) => ({ filename: `f${i}.txt`, status: "added" }));
  const { n } = await run(fakeGithub({ files: many }));
  assert.equal(n.files, 250);
  await rejects(fakeGithub({ files: many.slice(0, 120), changed: 121 }), /count mismatch: listed 120, PR says 121/);
  await rejects(fakeGithub({ files: many.slice(0, 3), changed: 2 }), /count mismatch/);
});

test("fails closed on API completeness problems", async () => {
  await rejects(fakeGithub({ changed: 3000 }), /3000 or more/);
  await rejects(fakeGithub({ changed: -1 }), /no changed_files/);
  await rejects(fakeGithub({ truncated: true }), /truncated/);
  await rejects(fakeGithub({ heads: ["c".repeat(40)] }), /head moved/);
  await rejects(fakeGithub({ heads: [HEAD, "c".repeat(40)] }), /head moved/);
  await rejects(fakeGithub({ files: [{ filename: "a", status: "exploded" }] }), /unknown file status/);
  await rejects(fakeGithub({ overrides: { [`git/trees/${HEAD}?recursive=1`]: { status: 502 } } }), /GitHub API 502 on git\/trees\//);
});

test("rejects unsafe paths anywhere in the PR data", async () => {
  for (const bad of ["../x", "a/../b", "a/./b", "/etc/passwd", "a\\b", "a//b", "", "a\nb", "a\u0007b", "a\u009bb"]) {
    await rejects(fakeGithub({ files: files(bad) }), /unsafe path/);
    await rejects(fakeGithub({ tree: { [bad]: "x" } }), /unsafe path/);
  }
  await rejects(fakeGithub({ files: [{ filename: "a", previous_filename: "../b", status: "renamed" }] }), /unsafe path/);
});

test("every symlink fails, incl. one at a steering directory; submodules (D15 at fetch time)", async () => {
  const link = { mode: "120000", type: "blob", size: 5 };
  for (const p of ["web/AGENTS.md", ".github/workflows/x.yml", "Scripts/Agents/hooks/g.mjs", ".claude", ".Claude", ".github", ".cursor", "docs", "docs/agents", "src/Desk.Api", "web/public/logo.svg"])
    await rejects(fakeGithub({ tree: { [p]: link } }), /symlink in the PR tree/);

  const sub = { mode: "160000", type: "commit" };
  await rejects(fakeGithub({ files: files("vendor/lib"), tree: { "vendor/lib": sub } }), /submodule/);
  await rejects(fakeGithub({ tree: { "docs/agents/sub": sub } }), /submodule/);
  assert.equal((await run(fakeGithub({ tree: { "vendor/lib": sub } }))).n.entries, 1, "an unchanged submodule passes");
});

test("blob limits: count, size, NUL, UTF-8, encoding", async () => {
  const tree = Object.fromEntries(Array.from({ length: 501 }, (_, i) => [`docs/agents/f${i}.md`, "x"]));
  await rejects(fakeGithub({ tree }), /more than 500 steering blobs/);
  await rejects(fakeGithub({ tree: { "AGENTS.md": { mode: "100644", type: "blob", size: MAX_BLOB_BYTES + 1 } } }), /over 1 MB/);
  await rejects(fakeGithub({ tree: { "AGENTS.md": "a\0b" } }), /NUL/);
  const bad = fakeGithub({ tree: { "AGENTS.md": "x" } });
  bad.entries[0].sha = "e".repeat(40);
  await rejects(bad, /GitHub API 404/);
  assert.throws(() => decodeBlob({ encoding: "base64", content: Buffer.from([0xc3, 0x28]).toString("base64") }, "p"), /not valid UTF-8/);
  assert.throws(() => decodeBlob({ encoding: "utf-8", content: "x" }, "p"), /not base64/);
  assert.throws(() => decodeBlob(blob("x".repeat(MAX_BLOB_BYTES + 1)), "p"), /over 1 MB/, "checked again after decoding");
  assert.equal(decodeBlob(blob("héllo"), "p"), "héllo");
});

test("writeData never escapes the scratch root or overwrites", () => {
  const root = scratch();
  assert.throws(() => writeData(root, "../out.txt", "x"), /escapes the scratch root/);
  writeData(root, "a/b.md", "x");
  assert.throws(() => writeData(root, "a/b.md", "y"), /EEXIST/);
});

test("output paths come from main's adapters.json; a PR adapters.json is only data", async () => {
  const evil = JSON.stringify({ inputs: ["**"], outputs: ["../../etc/x"] });
  const { outDir } = await run(fakeGithub({ tree: { "scripts/agents/adapters.json": evil, "src/secret.cs": "x" } }));
  assert.equal(readFileSync(join(outDir, "tree/scripts/agents/adapters.json"), "utf8"), evil);
  assert.equal(existsSync(join(outDir, "tree/src/secret.cs")), false);
});

test("main: success line, and failures print fixed text plus escaped names only", async () => {
  const gh = fakeGithub({ tree: { "AGENTS.md": "x" } });
  const env = { GITHUB_EVENT_PATH: eventFile(), GITHUB_REPOSITORY: "o/r", GH_TOKEN: "t" };
  const ok = capture();
  assert.equal(await main(["--out", scratch()], env, { write: ok.write, fetchImpl: gh.fetchImpl }), 0);
  assert.equal(ok.text(), "agents pr-data: 0 changed files, 1 tree entries, 1 steering blobs\n");

  const inj = "x\n::set-output name=a::b|`<script>`";
  const bad = capture();
  const code = await main(["--out", scratch()], env, { write: bad.write, fetchImpl: fakeGithub({ files: files(inj) }).fetchImpl });
  assert.equal(code, 1);
  const lines = bad.text().split("\n");
  assert.match(lines[0], /^::error::agents pr-data: unsafe path in PR data$/);
  const token = /^::stop-commands::([0-9a-f]{32})$/.exec(lines[1])[1];
  assert.equal(lines[2], JSON.stringify(inj));
  assert.equal(lines[3], `::${token}::`);
  assert.ok(lines.every((l) => !l.startsWith("::set-output")));
});

test("main: usage, event and repo errors fail closed", async () => {
  const c = capture();
  assert.equal(await main([], {}, { write: c.write }), 1);
  assert.match(c.text(), /usage/);
  const e = capture();
  assert.equal(await main(["--out", scratch()], { GITHUB_EVENT_PATH: eventFile({ number: "7" }) }, { write: e.write }), 1);
  assert.match(e.text(), /no pull_request number/);
  const s = capture();
  await main(["--out", scratch()], { GITHUB_EVENT_PATH: eventFile({ head: { sha: "HEAD" } }) }, { write: s.write });
  assert.match(s.text(), /no head\/base sha/);
  const r = capture();
  await main(["--out", scratch()], { GITHUB_EVENT_PATH: eventFile(), GITHUB_REPOSITORY: "o/r; x" }, { write: r.write });
  assert.match(r.text(), /not owner\/repo/);
  const x = capture();
  assert.equal(await main(["--out", scratch()], { GITHUB_EVENT_PATH: "/nonexistent/event.json" }, { write: x.write }), 1);
  assert.match(x.text(), /unexpected error \(fail closed\)/);
});

test("CLI entry point: run directly, a missing --out exits 1", () => {
  const r = spawnSync(process.execPath, [fileURLToPath(new URL("../pr-data.mjs", import.meta.url))], { encoding: "utf8" });
  assert.equal(r.status, 1);
  assert.match(r.stdout, /^::error::agents pr-data: usage/);
});
