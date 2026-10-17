#!/usr/bin/env node
/**
 * Fetch a PR's steering files as DATA through the REST API (docs/agents/governance.md
 * "pr-data.mjs"; guards 5, 7, 10, 11). Runs from the base checkout in the
 * `pull_request_target` jobs; it never checks out, imports or runs anything from the PR.
 *
 *   node scripts/agents/pr-data.mjs --out <dir>
 *
 * Writes <dir>/paths.json (every head tree entry with mode, type, size and blob sha),
 * <dir>/files.json (the changed-file list) and <dir>/tree/<path> for each blob in the
 * input set that main's adapters.json declares. Any doubt fails the job (exit 1).
 */
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { CheckError, assertSafePath, fail, githubApi, logUntrusted, matchesAny, readEvent } from "./lib/common.mjs";

export const MAX_CHANGED_FILES = 3000;
export const MAX_BLOBS = 500;
export const MAX_BLOB_BYTES = 1024 * 1024;
export const STATUSES = new Set(["added", "modified", "removed", "renamed", "copied", "changed", "unchanged"]);
const SYMLINK = "120000";
const SUBMODULE = "160000";

/** Main's manifest, read from this script's own (base) directory, never from the PR. */
export const loadAdapters = (path = fileURLToPath(new URL("./adapters.json", import.meta.url))) =>
  JSON.parse(readFileSync(path, "utf8"));

async function headOf(api, number, expected) {
  const pr = await api(`pulls/${number}`);
  if (pr.head?.sha !== expected) fail("PR head moved since the event; the next synchronize re-runs");
  return pr;
}

/** Guard 10: the complete changed-file list, or a failure. Shared with governance.mjs. */
export async function collectFiles(api, event) {
  const pr = await headOf(api, event.number, event.headSha);
  const total = pr.changed_files;
  if (!Number.isSafeInteger(total) || total < 0) fail("PR has no changed_files count");
  if (total >= MAX_CHANGED_FILES) fail(`PR changes ${MAX_CHANGED_FILES} or more files`);
  const files = [];
  for (let page = 1; files.length < total; page++) {
    const batch = await api(`pulls/${event.number}/files?per_page=100&page=${page}`);
    if (!Array.isArray(batch) || batch.length === 0) break;
    for (const f of batch) {
      if (!STATUSES.has(f.status)) fail("unknown file status in PR data", f.status);
      const entry = { filename: assertSafePath(f.filename), status: f.status };
      if (f.previous_filename !== undefined) entry.previous_filename = assertSafePath(f.previous_filename);
      files.push(entry);
    }
  }
  if (files.length !== total) fail(`changed-file count mismatch: listed ${files.length}, PR says ${total}`);
  return files;
}

/**
 * Guard 11 modes. Any symlink fails: a symlinked directory (`.claude`, `docs/agents`, or any
 * directory under the `**\/AGENTS.md` globs) would hide the steering files behind it from
 * this API tree, and main has none (#498 review). No submodule that is new, changed or steering.
 */
export function checkModes(entries, files, adapters) {
  const steering = [...adapters.inputs, ...adapters.enforcement];
  const touched = new Set(files.filter((f) => f.status !== "removed").map((f) => f.filename));
  for (const e of entries) {
    if (e.mode === SYMLINK) fail("symlink in the PR tree (none are allowed; D15)", e.path);
    if (e.mode === SUBMODULE && (touched.has(e.path) || matchesAny(e.path, steering)))
      fail("new, changed or steering submodule", e.path);
  }
}

const utf8 = new TextDecoder("utf-8", { fatal: true });

export function decodeBlob(blob, path) {
  if (blob.encoding !== "base64" || typeof blob.content !== "string") fail("blob is not base64", path);
  const buf = Buffer.from(blob.content, "base64");
  if (buf.length > MAX_BLOB_BYTES) fail("steering blob over 1 MB", path);
  if (buf.includes(0)) fail("steering blob has a NUL byte", path);
  try {
    return utf8.decode(buf);
  } catch {
    return fail("steering blob is not valid UTF-8", path);
  }
}

/** Write one blob under the scratch root, never through or over anything that exists. */
export function writeData(root, path, text) {
  const target = resolve(root, path);
  if (!target.startsWith(root + sep)) fail("path escapes the scratch root", path);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, text, { flag: "wx", mode: 0o644 });
}

export async function fetchPrData({ api, event, adapters, outDir }) {
  const files = await collectFiles(api, event);
  const tree = await api(`git/trees/${event.headSha}?recursive=1`);
  if (tree.truncated !== false || !Array.isArray(tree.tree)) fail("head tree listing is truncated");
  const entries = tree.tree.map((e) => ({ path: assertSafePath(e.path), mode: e.mode, type: e.type, size: e.size, sha: e.sha }));
  checkModes(entries, files, adapters);

  const wanted = entries.filter((e) => e.type === "blob" && matchesAny(e.path, adapters.inputs));
  if (wanted.length > MAX_BLOBS) fail(`more than ${MAX_BLOBS} steering blobs`);
  for (const e of wanted) if (!(e.size <= MAX_BLOB_BYTES)) fail("steering blob over 1 MB", e.path);

  const root = resolve(outDir);
  mkdirSync(root, { recursive: true });
  const treeRoot = resolve(root, "tree");
  mkdirSync(treeRoot);
  for (const e of wanted) writeData(treeRoot, e.path, decodeBlob(await api(`git/blobs/${e.sha}`), e.path));

  await headOf(api, event.number, event.headSha);
  writeFileSync(resolve(root, "paths.json"), JSON.stringify(entries), { flag: "wx", mode: 0o644 });
  writeFileSync(resolve(root, "files.json"), JSON.stringify(files), { flag: "wx", mode: 0o644 });
  return { files: files.length, entries: entries.length, blobs: wanted.length };
}

/** Report a failure without letting a PR-chosen name become a workflow command. */
export function report(err, write) {
  if (err instanceof CheckError) {
    write(`::error::agents pr-data: ${err.message}\n`);
    if (err.detail !== undefined) logUntrusted(write, [err.detail]);
  } else write("::error::agents pr-data: unexpected error (fail closed)\n");
}

export async function main(argv, env, { write = (s) => process.stdout.write(s), fetchImpl } = {}) {
  try {
    const i = argv.indexOf("--out");
    if (i < 0 || !argv[i + 1]) fail("usage: pr-data.mjs --out <dir>");
    const event = readEvent(env.GITHUB_EVENT_PATH);
    const api = githubApi({ token: env.GH_TOKEN, repo: env.GITHUB_REPOSITORY, fetchImpl });
    const n = await fetchPrData({ api, event, adapters: loadAdapters(), outDir: argv[i + 1] });
    write(`agents pr-data: ${n.files} changed files, ${n.entries} tree entries, ${n.blobs} steering blobs\n`);
    return 0;
  } catch (err) {
    report(err, write);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2), process.env);
}
