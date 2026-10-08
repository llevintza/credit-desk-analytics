#!/usr/bin/env node
/**
 * Per-area change classifier for `ci.yml` (issue #169): run only the jobs a PR touches.
 *
 * Input: the PR's changed paths (`git diff --name-only --no-renames BASE...HEAD`).
 * Output: one flag per area plus one `run_<job>` flag per heavy job, written to
 * `$GITHUB_OUTPUT` as `true` / `false`.
 *
 * Each path is classified in this order (first match wins):
 *   1. docs (`*.md` anywhere, `docs/**`, `.claude/skills/**`)  -> no flag;
 *      under `src/` and `web/src/` only AGENTS.md / CLAUDE.md count as docs
 *      Markdown runs in no CI job, so `.github/AGENTS.md` is docs too (#166).
 *   2. shared trigger (`.github/**`, build props, SDK pins, solution files,
 *      lockfiles, Docker/compose files, CI and coverage scripts) -> every flag
 *   3. areas (`api`, `web`, `db`, `app`, `perf`)               -> those flags
 *   4. anything else                                           -> every flag
 * An empty diff also sets every flag. When in doubt, run.
 *
 * CI runs this file from the BASE_SHA checkout (`_base/`), so a PR cannot
 * rewrite the rules that decide what it runs. In `ci.yml` a job is skipped only
 * when its flag is exactly `false`: on push, when the base has no classifier,
 * or when this script fails, the outputs are empty and every job runs.
 */
import { execFileSync } from "node:child_process";
import { appendFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const AREAS = Object.freeze(["api", "web", "db", "app", "perf"]);

// Heavy jobs in ci.yml and the areas that run them (README §14.1). `secrets`,
// `workflows` and `gate-tests` always run and are not listed.
export const JOBS = Object.freeze({
  run_api: Object.freeze(["api", "db"]), // every .NET suite, then migrate + seed
  run_web: Object.freeze(["web"]),
  run_db_tools: Object.freeze(["db"]),
  run_compose_smoke: Object.freeze(["app"]),
  run_e2e: Object.freeze(["app"]),
  run_budgets: Object.freeze(["app", "perf"]), // seeded compose stack + perf/payload-size.mjs
  run_coverage: Object.freeze(["api", "db", "web"]), // whenever the api or web job runs
});

const SHARED_BASENAMES = new Set([
  "global.json",
  "dotnet-tools.json",
  "nuget.config",
  "package-lock.json",
  "packages.lock.json",
  ".dockerignore",
]);

export function isShared(path) {
  const base = basename(path);
  return (
    path.startsWith(".github/") ||
    SHARED_BASENAMES.has(base.toLowerCase()) ||
    /^Directory\.[^/]+\.(props|targets)$/.test(base) ||
    /\.slnx?$/.test(base) ||
    /^Dockerfile/.test(base) ||
    /^(docker-)?compose[^/]*\.ya?ml$/.test(base) ||
    path.startsWith("perf/coverage-") ||
    path === "tests/testconfig.json" ||
    path === ".gitleaks.toml"
  );
}

// Markdown in source trees could be embedded or imported, so there only the agent
// rule files (AGENTS.md / CLAUDE.md, docs "anywhere" per #169) count as docs.
const SOURCE_TREE = /^(src|web\/src)\//;
const AGENT_DOCS = new Set(["AGENTS.md", "CLAUDE.md"]);

export function isDocs(path) {
  if (path.startsWith("docs/") || path.startsWith(".claude/skills/")) return true;
  if (!path.endsWith(".md")) return false;
  return !SOURCE_TREE.test(path) || AGENT_DOCS.has(basename(path));
}

const AREA_PREFIXES = Object.freeze({
  // deploy/start.sh is linked into Desk.Api.Tests (StartScriptTests runs it).
  api: ["src/Desk.Api/", "src/Desk.Data/", "src/Desk.UserAdmin/", "tests/Desk.Api.Tests/", "deploy/start.sh"],
  web: ["web/"],
  db: ["src/Desk.Data/", "src/Desk.Seeder/", "tests/Desk.Data.Tests/", "tests/Desk.Seeder.Tests/"],
  app: ["deploy/", "e2e/", "render.yaml"],
  perf: ["perf/"],
});

// Every perf/ subdirectory is a .NET benchmark project in CreditDesk.slnx that references
// src/, so any file in one (.cs, .csproj, .resx, content) runs the api job's solution
// build too. The perf JS tools all sit at the top level of perf/.
const PERF_DOTNET = /^perf\/[^/]+\//;

/** Areas a single path touches: `null` means shared/unknown (every flag), `[]` means docs. */
export function areasFor(path) {
  if (isDocs(path)) return [];
  if (isShared(path)) return null;
  // A prefix ending in `/` is a directory; anything else is one exact file.
  const hit = AREAS.filter(
    (a) =>
      AREA_PREFIXES[a].some((p) => (p.endsWith("/") ? path.startsWith(p) : path === p)) ||
      (a === "api" && PERF_DOTNET.test(path)),
  );
  return hit.length ? hit : null;
}

export function classify(paths) {
  const flags = Object.fromEntries(AREAS.map((a) => [a, false]));
  const reasons = [];
  const all = (why) => {
    for (const a of AREAS) flags[a] = true;
    reasons.push(why);
  };
  const list = paths.map(normalize).filter(Boolean);
  if (list.length === 0) all("empty diff");
  for (const p of list) {
    const areas = areasFor(p);
    if (areas === null) all(isShared(p) ? `shared trigger: ${p}` : `matches no area: ${p}`);
    else for (const a of areas) flags[a] = true;
  }
  // Migrations and every runtime project change what the image runs.
  flags.app = flags.app || flags.api || flags.web || flags.db;
  const jobs = Object.fromEntries(Object.entries(JOBS).map(([job, areas]) => [job, areas.some((a) => flags[a])]));
  return { flags, jobs, reasons, paths: list };
}

export function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith("--")) continue;
    const next = argv[i + 1];
    out[argv[i].slice(2)] = next !== undefined && !next.startsWith("--") ? argv[++i] : "true";
  }
  return out;
}

export function changedPaths(root, base, head) {
  const out = execFileSync(
    "git",
    ["-C", root, "diff", "--name-only", "--no-renames", "-z", `${base}...${head}`],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  );
  return out.split("\0").filter(Boolean);
}

export function render(result) {
  const lines = ["## Changed areas (#169)", ""];
  lines.push("| Flag | Value |", "|---|---|");
  for (const a of AREAS) lines.push(`| \`${a}\` | ${result.flags[a]} |`);
  for (const [job, v] of Object.entries(result.jobs)) lines.push(`| \`${job}\` | ${v} |`);
  lines.push("");
  if (result.reasons.length) {
    lines.push("Every flag is set:", "");
    for (const r of result.reasons.slice(0, 20)) lines.push(`- ${r}`);
    if (result.reasons.length > 20) lines.push(`- … and ${result.reasons.length - 20} more`);
    lines.push("");
  }
  lines.push(`${result.paths.length} changed path(s).`, "");
  return lines.join("\n");
}

export function outputs(result) {
  return Object.entries({ ...result.flags, ...result.jobs })
    .map(([k, v]) => `${k}=${v}`)
    .join("\n") + "\n";
}

export function main(argv, env = process.env, deps = { changedPaths, appendFileSync }) {
  const args = parseArgs(argv);
  const base = args.base ?? "";
  const head = args.head ?? "";
  if (!/^[0-9a-f]{40}$/.test(base) || !/^[0-9a-f]{40}$/.test(head)) {
    throw new Error(`--base and --head must be full commit SHAs (got '${base}', '${head}').`);
  }
  const result = classify(deps.changedPaths(resolve(args.root ?? "."), base, head));
  const summary = render(result);
  if (env.GITHUB_OUTPUT) deps.appendFileSync(env.GITHUB_OUTPUT, outputs(result));
  if (env.GITHUB_STEP_SUMMARY) deps.appendFileSync(env.GITHUB_STEP_SUMMARY, summary);
  return { result, summary };
}

function normalize(p) {
  // No trim: `git diff -z` paths are exact, and "x.md " is not a docs file.
  return String(p).replace(/\\/g, "/").replace(/^\.\//, "");
}

function basename(p) {
  return p.slice(p.lastIndexOf("/") + 1);
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const { summary } = main(process.argv.slice(2));
  process.stdout.write(summary);
}
