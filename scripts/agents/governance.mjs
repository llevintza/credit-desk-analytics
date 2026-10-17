#!/usr/bin/env node
/**
 * `governance-paths (base)` (docs/agents/governance.md; ADR-0020 §7). Main's copy runs in
 * a `pull_request_target` job: it lists the PR's changed files through the API (the shared
 * pr-data checks) and fails unless a PR that touches a governance path has a `[workflows]`
 * title. It is a base-run SIGNAL the PR can't remove; human governance review is the control.
 *
 *   node scripts/agents/governance.mjs [--base <dir>]
 *
 * The title is read from the event JSON and never printed. Names reach the log and the step
 * summary only escaped (guards 7, 8).
 */
import { appendFileSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CheckError, capNames, escapeHtml, githubApi, logUntrusted, matchesAny, readEvent } from "./lib/common.mjs";
import { collectFiles, decodeBlob } from "./pr-data.mjs";

export const GOVERNANCE_PATHS = Object.freeze([
  ".github/**", "**/Dockerfile*", "**/*.Dockerfile", "render.yaml", "deploy/**",
  "**/docker-compose*.y*ml", "**/compose*.y*ml",
  ".claude/**", "**/.claude/**", ".cursor/**", ".cursorignore", ".grok/**", ".mcp.json",
  "**/AGENTS.md", "**/AGENT.md", "**/CLAUDE.md", "**/CLAUDE.local.md", ".claudeignore",
  "docs/agents/**", "scripts/agents/**", ".gitleaks.toml", "perf/coverage-*", "tests/testconfig.json", ".gitmodules",
]);

export const BASELINE = "perf/coverage-baseline.json";

// The raise-only exemption's voider set (governance.md; ADR §7): any same-PR change to one of
// these, or to another governance path, voids it. Files that set coverage measurement or
// thresholds; the list is explicit, and anything unlisted that is a governance path voids too.
export const COVERAGE_VOIDERS = Object.freeze([
  "perf/coverage-*", "tests/testconfig.json", "**/Directory.Build.props", "**/Directory.Build.targets",
  "**/*.runsettings", "web/angular.json", "web/vitest.config.*", "web/package.json",
]);

export const isGovernance = (path) => matchesAny(path, GOVERNANCE_PATHS);
const names = (f) => [f.filename, ...(f.previous_filename === undefined ? [] : [f.previous_filename])];

/** Same keys at every level; `line`/`branch` numbers may only go up; nothing else may change. */
export function raiseOnly(base, head) {
  const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
  if (!isObj(base) || !isObj(head)) return false;
  const bk = Object.keys(base).sort();
  const hk = Object.keys(head).sort();
  if (bk.length !== hk.length || bk.some((k, i) => k !== hk[i])) return false;
  return bk.every((k) => {
    const b = base[k];
    const h = head[k];
    if (isObj(b) || isObj(h)) return raiseOnly(b, h);
    if (k === "line" || k === "branch") return typeof b === "number" && typeof h === "number" && Number.isFinite(h) && h >= b;
    return JSON.stringify(b) === JSON.stringify(h);
  });
}

/**
 * Whether the baseline bump is exempt. Every doubt (wrong status, another voiding change,
 * an unreadable or unparseable copy, an API error) means "not exempt", never a pass.
 */
export async function exemptBaseline({ api, event, files, baseDir }) {
  const bump = files.filter((f) => names(f).some((n) => n.toLowerCase() === BASELINE));
  if (bump.length !== 1 || bump[0].status !== "modified" || bump[0].filename !== BASELINE) return false;
  const others = files.filter((f) => f !== bump[0]).flatMap(names);
  if (others.some((n) => isGovernance(n) || matchesAny(n, COVERAGE_VOIDERS))) return false;
  try {
    const base = JSON.parse(readFileSync(resolve(baseDir, BASELINE), "utf8"));
    const tree = await api(`git/trees/${event.headSha}?recursive=1`);
    if (tree.truncated !== false) return false;
    const entry = tree.tree.find((e) => e.path === BASELINE && e.type === "blob" && e.mode === "100644");
    if (!entry) return false;
    const head = JSON.parse(decodeBlob(await api(`git/blobs/${entry.sha}`), BASELINE));
    return raiseOnly(base, head);
  } catch {
    return false;
  }
}

export async function evaluate({ api, event, baseDir }) {
  const files = await collectFiles(api, event);
  const exempt = await exemptBaseline({ api, event, files, baseDir });
  const matched = files.flatMap(names).filter((n) => isGovernance(n) && !(exempt && n === BASELINE));
  const unique = [...new Set(matched)];
  const titled = event.title.startsWith("[workflows]");
  return { files: files.length, matched: unique, exempt, ok: unique.length === 0 || titled };
}

export function summary({ files, matched, exempt, ok }) {
  const lines = ["## governance-paths (base)", "", `${files} changed files.`];
  if (exempt) lines.push("", "`perf/coverage-baseline.json`: raise-only bump, exempt (governance.md; the reviewer's governance check still applies).");
  if (matched.length === 0) lines.push("", "No governance paths touched.");
  else {
    lines.push("", `**Governance review required (gate 3 + Leo):** ${matched.length} governance path(s).`, "", "| Path |", "|---|");
    for (const n of capNames(matched)) lines.push(`| <code>${escapeHtml(n)}</code> |`);
    lines.push("", ok ? "The title starts with `[workflows]`." : "**Fail:** the PR title must start with `[workflows]`. Editing the title re-runs this job.");
  }
  return `${lines.join("\n")}\n`;
}

export async function main(argv, env, { write = (s) => process.stdout.write(s), fetchImpl } = {}) {
  try {
    const i = argv.indexOf("--base");
    const baseDir = i >= 0 && argv[i + 1] ? argv[i + 1] : ".";
    const event = readEvent(env.GITHUB_EVENT_PATH);
    const api = githubApi({ token: env.GH_TOKEN, repo: env.GITHUB_REPOSITORY, fetchImpl });
    const result = await evaluate({ api, event, baseDir });
    if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, summary(result));
    write(`governance-paths: ${result.files} changed files, ${result.matched.length} governance paths${result.exempt ? ", baseline bump exempt" : ""}\n`);
    if (result.matched.length) logUntrusted(write, result.matched);
    if (!result.ok) {
      write("::error::governance paths changed: the PR title must start with [workflows] (gate 3 + Leo)\n");
      return 1;
    }
    return 0;
  } catch (err) {
    write(`::error::governance-paths: ${err instanceof CheckError ? err.message : "unexpected error (fail closed)"}\n`);
    if (err instanceof CheckError && err.detail !== undefined) logUntrusted(write, [err.detail]);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2), process.env);
}

