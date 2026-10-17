#!/usr/bin/env node
/**
 * ADR-0020 drift checks (docs/agents/drift-checks.md).
 *
 *   node scripts/agents/check-drift.mjs --root <dir> [--paths <paths.json>] [--base <dir>]
 *
 * --root   the tree under test: pr-data's scratch tree in `agents-drift (base)`, or a checkout.
 * --paths  the full head listing (pr-data's paths.json); without it, `git ls-tree` of --root.
 * --base   the trusted base checkout (presence rule P, D22, and the base-relative checks).
 *
 * PR files are DATA: read with readFileSync and parsed only by JSON.parse or line-anchored
 * markers; nothing from --root is imported or run. Arming (which checks run) always comes
 * from the base copy of arming.json. Any lookup error fails the job.
 */
import { execFileSync } from "node:child_process";
import { appendFileSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CheckError, capNames, escapeHtml, fold, isSafePath, logUntrusted, matchesAny } from "./lib/common.mjs";
import { TREE_CHECKS } from "./lib/checks-tree.mjs";

export const CHECKS = Object.freeze({ ...TREE_CHECKS });

/** `git ls-tree -r -z` of HEAD in `dir`: [{path, mode, type, sha}]. */
export function gitListing(dir) {
  const out = execFileSync("git", ["-C", dir, "ls-tree", "-r", "-z", "--full-tree", "HEAD"], { encoding: "utf8", maxBuffer: 64 << 20 });
  return out.split("\0").filter(Boolean).map((l) => {
    const [meta, path] = l.split("\t");
    const [mode, type, sha] = meta.split(" ");
    return { path, mode, type, sha };
  });
}

/** Read-only view of one tree for the checks. Unknown paths read as null; listed ones must read. */
export function treeView(root, listing) {
  const entries = listing.filter((e) => e.type !== "tree");
  for (const e of entries) if (!isSafePath(e.path)) throw new CheckError(`unsafe path in listing: ${JSON.stringify(e.path)}`);
  const byPath = new Map(entries.map((e) => [e.path, e]));
  const folded = new Map();
  for (const e of entries) folded.set(fold(e.path), [...(folded.get(fold(e.path)) ?? []), e.path]);
  return {
    root,
    entries,
    paths: entries.map((e) => e.path),
    entry: (p) => byPath.get(p),
    has: (p) => byPath.has(p),
    hasFolded: (p) => folded.has(fold(p)),
    glob: (globs) => entries.filter((e) => matchesAny(e.path, [].concat(globs))).map((e) => e.path),
    read: (p) => (byPath.has(p) && byPath.get(p).mode !== "120000" ? readFileSync(join(root, p), "utf8") : null),
  };
}

/** Which checks run: always-armed IDs, plus subject-armed IDs whose subject is on main or in the PR. */
export function armed(arming, head, base) {
  const on = new Set(arming.alwaysArmed);
  const off = [];
  for (const [id, globs] of Object.entries(arming.subjects)) {
    if (head.glob(globs).length || base?.glob(globs).length) on.add(id);
    else off.push(id);
  }
  return { on, off };
}

export function runChecks(ctx, ids = Object.keys(CHECKS)) {
  const results = [];
  for (const id of ids) {
    if (!ctx.armed.on.has(id)) continue;
    const fails = [];
    const warns = [];
    CHECKS[id](ctx, { fail: (m) => fails.push(m), warn: (m) => warns.push(m) });
    results.push({ id, fails, warns });
  }
  return results;
}

export function summary(results, off) {
  const failed = results.filter((r) => r.fails.length);
  const lines = ["## agents-drift", "", `${results.length} checks run, ${failed.length} failed.`];
  if (off.length) lines.push("", `Disarmed (subject absent on main and PR; success with a notice): ${off.join(", ")}`);
  for (const r of results) {
    for (const m of capNames(r.fails)) lines.push(`- **${r.id} fail:** ${escapeHtml(m)}`);
    for (const m of capNames(r.warns)) lines.push(`- ${r.id} warn: ${escapeHtml(m)}`);
  }
  return `${lines.join("\n")}\n`;
}

const arg = (argv, name) => {
  const i = argv.indexOf(name);
  return i >= 0 ? argv[i + 1] : undefined;
};

export function main(argv, env, { write = (s) => process.stdout.write(s) } = {}) {
  try {
    const rootArg = arg(argv, "--root");
    if (!rootArg) throw new CheckError("usage: check-drift.mjs --root <dir> [--paths <paths.json>] [--base <dir>]");
    const root = resolve(rootArg);
    const pathsFile = arg(argv, "--paths");
    const head = treeView(root, pathsFile ? JSON.parse(readFileSync(pathsFile, "utf8")) : gitListing(root));
    const baseDir = arg(argv, "--base");
    const base = baseDir ? treeView(resolve(baseDir), gitListing(resolve(baseDir))) : null;
    // Arming and the steering globs are always the trusted copies, never the PR's.
    const own = (f) => (base ? join(base.root, "scripts/agents", f) : fileURLToPath(new URL(`./${f}`, import.meta.url)));
    const arming = JSON.parse(readFileSync(own("arming.json"), "utf8"));
    const adapters = JSON.parse(readFileSync(own("adapters.json"), "utf8"));
    const ctx = { head, base, arming, steering: [...adapters.inputs, ...adapters.enforcement], armed: armed(arming, head, base) };
    const results = runChecks(ctx);
    if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, summary(results, ctx.armed.off));
    const failed = results.filter((r) => r.fails.length);
    write(`agents-drift: ${results.length} checks, ${failed.length} failed${ctx.armed.off.length ? `; disarmed: ${ctx.armed.off.join(", ")}` : ""}\n`);
    for (const r of results) if (r.fails.length || r.warns.length) logUntrusted(write, [...r.fails.map((m) => `${r.id} fail: ${m}`), ...r.warns.map((m) => `${r.id} warn: ${m}`)]);
    if (failed.length) write(`::error::agents-drift failed: ${failed.map((r) => r.id).join(", ")}\n`);
    return failed.length ? 1 : 0;
  } catch (err) {
    write(`::error::agents-drift: ${err instanceof CheckError ? err.message : "lookup or parse error (fail closed)"}\n`);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), process.env);
}
