#!/usr/bin/env node
/**
 * ADR-0020 drift checks (docs/agents/drift-checks.md).
 *
 *   node scripts/agents/check-drift.mjs --root <dir> --paths <paths.json> [--base <dir>]
 *
 * --root   the tree under test: pr-data's scratch tree in `agents-drift (base)`, or a checkout.
 * --paths  the full head listing (pr-data's paths.json). Required: the head is never listed
 *          with git, so a --root inside another work tree can't list that repo (#505).
 * --base   the trusted base checkout (presence rule P, D22, and the base-relative checks),
 *          listed with `git ls-tree`; it must be the top of its own work tree.
 *
 * PR files are DATA: read with readFileSync and parsed only by JSON.parse or line-anchored
 * markers; nothing from --root is imported or run. Arming (which checks run) always comes
 * from the base copy of arming.json. Any lookup error fails the job.
 */
import { execFileSync } from "node:child_process";
import { appendFileSync, readFileSync, realpathSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CheckError, capNames, escapeHtml, fail, fold, isSafePath, logUntrusted, matchesAny } from "./lib/common.mjs";
import { TEXT_CHECKS } from "./lib/checks-text.mjs";
import { TREE_CHECKS, validateArming } from "./lib/checks-tree.mjs";

export const CHECKS = Object.freeze({ ...TREE_CHECKS, ...TEXT_CHECKS });

export const USAGE = "usage: check-drift.mjs --root <dir> --paths <paths.json> [--base <dir>]";

/** `git ls-tree -r -z` of HEAD in `dir`: [{path, mode, type, sha}]. */
export function gitListing(dir) {
  // Never list an enclosing repository: `dir` must be the top of its own work tree (#502 nit).
  const top = execFileSync("git", ["-C", dir, "rev-parse", "--show-toplevel"], { encoding: "utf8" }).trim();
  if (realpathSync(top) !== realpathSync(dir)) throw new CheckError("--root/--base must be the top of a git work tree");
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
  for (const e of entries) if (!isSafePath(e.path)) fail("unsafe path in listing", e.path);
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
  // D12 is also armed by any `# agents-drift:` marker line in main's claude-review.yml.
  const marked = /^\s*# agents-drift:/m.test(base?.read(".github/workflows/claude-review.yml") ?? "");
  for (const [id, globs] of Object.entries(arming.subjects)) {
    if (head.glob(globs).length || base?.glob(globs).length || (id === "D12" && marked)) on.add(id);
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
    const pathsFile = arg(argv, "--paths");
    if (!rootArg || !pathsFile) throw new CheckError(USAGE);
    const root = resolve(rootArg);
    const head = treeView(root, JSON.parse(readFileSync(pathsFile, "utf8")));
    const baseDir = arg(argv, "--base");
    const base = baseDir ? treeView(resolve(baseDir), gitListing(resolve(baseDir))) : null;
    // Arming and the input globs are always the trusted copies, never the PR's.
    const own = (f) => (base ? join(base.root, "scripts/agents", f) : fileURLToPath(new URL(`./${f}`, import.meta.url)));
    // The trusted copy must name only checks this checker has (P is the presence rule).
    const arming = validateArming(JSON.parse(readFileSync(own("arming.json"), "utf8")), new Set(Object.keys(CHECKS)));
    const adapters = JSON.parse(readFileSync(own("adapters.json"), "utf8"));
    const ctx = { head, base, arming, inputs: adapters.inputs, armed: armed(arming, head, base) };
    const results = runChecks(ctx);
    if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, summary(results, ctx.armed.off));
    const failed = results.filter((r) => r.fails.length);
    write(`agents-drift: ${results.length} checks, ${failed.length} failed${ctx.armed.off.length ? `; disarmed: ${ctx.armed.off.join(", ")}` : ""}\n`);
    for (const r of results) if (r.fails.length || r.warns.length) logUntrusted(write, [...r.fails.map((m) => `${r.id} fail: ${m}`), ...r.warns.map((m) => `${r.id} warn: ${m}`)]);
    if (failed.length) write(`::error::agents-drift failed: ${failed.map((r) => r.id).join(", ")}\n`);
    return failed.length ? 1 : 0;
  } catch (err) {
    write(`::error::agents-drift: ${err instanceof CheckError ? err.message : "lookup or parse error (fail closed)"}\n`);
    if (err instanceof CheckError && err.detail !== undefined) logUntrusted(write, [err.detail]);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), process.env);
}
