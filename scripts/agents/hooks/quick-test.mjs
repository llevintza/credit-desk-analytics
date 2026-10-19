if (process.env.CI || process.env.GITHUB_ACTIONS === 'true') { markCiHookRan('quick-test'); process.exit(0); }
/**
 * Stop hook: quick tests for what the session touched (docs/agents/adapters.md "Hooks").
 * Advisory only, so every path exits 0. It runs the fast `node --test` suites next to changed
 * scripts (scripts/agents, .github/scripts, perf, web/scripts) and only reminds about the .NET
 * and Angular suites, which need Docker or a browser and are the pr-ready skill's job.
 *
 * Line 1 is the exact CI no-op D18 requires (see format.mjs).
 */
import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

function markCiHookRan(hook) {
  try {
    appendFileSync(join(process.env.RUNNER_TEMP || tmpdir(), "agents-hooks-ran"), `${hook}\n`);
  } catch {
    // The canary is a signal for the review job; the hook still exits 0.
  }
}

/** Changed and untracked paths, from read-only git. */
function changed(root) {
  const r = spawnSync("git", ["-C", root, "status", "--porcelain", "-z", "--untracked-files=all"], { encoding: "utf8", timeout: 10_000 });
  if (r.status !== 0) return [];
  const out = [];
  const fields = r.stdout.split("\0");
  for (let i = 0; i < fields.length; i++) {
    if (fields[i].length < 4) continue;
    out.push(fields[i].slice(3));
    if (/^[RC]/.test(fields[i])) i++; // a rename or copy is followed by its old path
  }
  return out;
}

/** The test files to run for a set of changed paths, and the suites to remind about. */
function plan(paths, root) {
  const tests = new Set();
  const remind = new Set();
  for (const p of paths) {
    if (p.startsWith("scripts/agents/")) tests.add("scripts/agents/test/*.test.mjs");
    else if (/^(\.github\/scripts|perf|web\/scripts)\/[^/]+\.mjs$/.test(p)) {
      const t = p.endsWith(".test.mjs") ? p : p.replace(/\.mjs$/, ".test.mjs");
      if (existsSync(join(root, t))) tests.add(t);
    } else if (/^(src|tests)\//.test(p)) remind.add("dotnet test -c Release (Docker required)");
    else if (p.startsWith("web/src/")) remind.add("cd web && npm run lint && npm test -- --watch=false");
  }
  return { tests: [...tests], remind: [...remind] };
}

function run() {
  const root = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  const { tests, remind } = plan(changed(root), root);
  for (const t of tests) {
    const r = spawnSync(process.execPath, ["--test", t], { cwd: root, encoding: "utf8", timeout: 120_000 });
    const tally = /ℹ pass (\d+)[\s\S]*?ℹ fail (\d+)/.exec(r.stdout ?? "");
    process.stderr.write(`agents quick-test: ${t}: ${tally ? `${tally[1]} pass, ${tally[2]} fail` : r.error ? r.error.code : `exit ${r.status}`}\n`);
  }
  for (const m of remind) process.stderr.write(`agents quick-test: also run ${m}\n`);
}

try {
  run();
} catch {
  // Advisory only.
}
process.exit(0);
