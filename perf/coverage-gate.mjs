#!/usr/bin/env node
/**
 * Coverage gates (README §11 / CI):
 *   1. New/changed coverable lines vs BASE_SHA >= diffLineMinPercent (same for branches).
 *   2. Overall line/branch % per project never drops vs the baseline at BASELINE_REF (main).
 *   3. The committed perf/coverage-baseline.json must match the measured numbers (so main's
 *      floor is updated by the PR that earned it, not by a dashboard).
 *
 * Usage:
 *   node perf/coverage-gate.mjs \
 *     --dotnet TestResults/coverage \
 *     --web web/coverage \
 *     --base <sha> \
 *     --baseline-ref origin/main
 */
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync, appendFileSync } from "node:fs";
import { join, resolve } from "node:path";

const args = parseArgs(process.argv.slice(2));
const repoRoot = resolve(args.root ?? ".");
const thresholds = JSON.parse(readFileSync(join(repoRoot, args.thresholds ?? "perf/coverage-thresholds.json"), "utf8"));
const committedBaselinePath = join(repoRoot, args.baseline ?? "perf/coverage-baseline.json");
const baseSha = args.base ?? env("BASE_SHA") ?? "";
const baselineRef = args["baseline-ref"] ?? env("BASELINE_REF") ?? "origin/main";

const dotnetDir = resolve(repoRoot, args.dotnet ?? "TestResults/coverage");
const webDir = resolve(repoRoot, args.web ?? "web/coverage");

const dotnet = summarizeDotnet(dotnetDir);
const web = summarizeLcov(findLcov(webDir));
const measured = { dotnet, web };

const mainBaseline = loadBaselineAtRef(repoRoot, baselineRef, args.baseline ?? "perf/coverage-baseline.json");
const committed = JSON.parse(readFileSync(committedBaselinePath, "utf8"));

const changed = baseSha ? changedLines(repoRoot, baseSha) : { files: new Map() };
const diff = {
  dotnet: diffCoverage(changed, dotnet.files, (p) => p.replaceAll("\\", "/").includes("web/src/") === false),
  web: diffCoverage(changed, web.files, (p) => p.replaceAll("\\", "/").includes("web/src/")),
};

let failed = false;
const lines = [];
const say = (s) => lines.push(s);

say("## Coverage");
say("");
say("| Project | Overall line | Overall branch | Diff line | Diff branch | Floor (main) line | Floor (main) branch |");
say("|---|---:|---:|---:|---:|---:|---:|");
for (const name of ["dotnet", "web"]) {
  const m = measured[name];
  const d = diff[name];
  const f = mainBaseline[name] ?? { line: 0, branch: 0 };
  say(
    `| ${name} | ${pct(m.line)}% | ${pct(m.branch)}% | ${diffPct(d.line)} | ${diffPct(d.branch)} | ${pct(f.line)}% | ${pct(f.branch)}% |`,
  );
}
say("");
say(`Base SHA: \`${baseSha || "(none)"}\`. Baseline ref: \`${baselineRef}\`.`);
say("");

for (const name of ["dotnet", "web"]) {
  const m = measured[name];
  const f = mainBaseline[name] ?? { line: 0, branch: 0 };
  if (thresholds.overallMustNotDrop) {
    if (m.line + 1e-9 < f.line) {
      failed = true;
      say(`- **FAIL** ${name} overall line ${pct(m.line)}% dropped below main ${pct(f.line)}%.`);
    }
    if (m.branch + 1e-9 < f.branch) {
      failed = true;
      say(`- **FAIL** ${name} overall branch ${pct(m.branch)}% dropped below main ${pct(f.branch)}%.`);
    }
  }
  const d = diff[name];
  if (d.line.total > 0 && d.line.percent + 1e-9 < thresholds.diffLineMinPercent) {
    failed = true;
    say(
      `- **FAIL** ${name} diff line ${pct(d.line.percent)}% (${d.line.hit}/${d.line.total}) < ${thresholds.diffLineMinPercent}%.`,
    );
  }
  if (d.branch.total > 0 && d.branch.percent + 1e-9 < thresholds.diffBranchMinPercent) {
    failed = true;
    say(
      `- **FAIL** ${name} diff branch ${pct(d.branch.percent)}% (${d.branch.hit}/${d.branch.total}) < ${thresholds.diffBranchMinPercent}%.`,
    );
  }
}

  const tol = thresholds.baselineMatchTolerancePercent ?? 0.5;
for (const name of ["dotnet", "web"]) {
  const m = measured[name];
  const c = committed[name] ?? { line: 0, branch: 0 };
  const f = mainBaseline[name] ?? { line: 0, branch: 0 };
  if (c.line + 1e-9 < f.line || c.branch + 1e-9 < f.branch) {
    failed = true;
    say(`- **FAIL** committed baseline ${name} is below main's floor.`);
  }
  if (c.line > m.line + tol || c.branch > m.branch + tol) {
    failed = true;
    say(
      `- **FAIL** committed baseline ${name} line/branch ${pct(c.line)}/${pct(c.branch)} is above measured ${pct(m.line)}/${pct(m.branch)}.`,
    );
  }
  if (m.line - c.line > tol || m.branch - c.branch > tol) {
    failed = true;
    say(
      `- **FAIL** committed baseline ${name} line/branch ${pct(c.line)}/${pct(c.branch)} is behind measured ${pct(m.line)}/${pct(m.branch)}. Update \`perf/coverage-baseline.json\` to the measured JSON below.`,
    );
  }
}

if (!failed) say("- All coverage gates passed.");

const body = lines.join("\n") + "\n";
process.stdout.write(body);
const summary = env("GITHUB_STEP_SUMMARY");
if (summary) appendFileSync(summary, body);

const expected = {
  dotnet: { line: round1(dotnet.line), branch: round1(dotnet.branch) },
  web: { line: round1(web.line), branch: round1(web.branch) },
};
process.stdout.write("\nMeasured baseline JSON:\n" + JSON.stringify(expected, null, 2) + "\n");

if (failed) process.exit(1);

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const key = a.slice(2);
      const val = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : "true";
      out[key] = val;
    }
  }
  return out;
}

function env(name) {
  const v = process.env[name];
  return v && v.length ? v : undefined;
}

function pct(n) {
  return Number(n).toFixed(1);
}

function round1(n) {
  return Math.round(n * 10) / 10;
}

function diffPct(s) {
  if (!s.total) return "n/a";
  return `${pct(s.percent)}% (${s.hit}/${s.total})`;
}

function loadBaselineAtRef(root, ref, path) {
  try {
    const raw = execFileSync("git", ["show", `${ref}:${path}`], {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    return JSON.parse(raw);
  } catch {
    return { dotnet: { line: 0, branch: 0 }, web: { line: 0, branch: 0 } };
  }
}

function summarizeDotnet(dir) {
  const files = Object.create(null);
  let lineHit = 0,
    lineTotal = 0,
    branchHit = 0,
    branchTotal = 0;
  if (!existsSync(dir)) return emptySummary();
  for (const xml of walk(dir).filter((f) => f.endsWith(".xml") && f.includes("cobertura"))) {
    const text = readFileSync(xml, "utf8");
    const classRe = /<class\b[^>]*filename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g;
    let cm;
    while ((cm = classRe.exec(text))) {
      const filename = mergeKey(files, cm[1]);
      const body = cm[2];
      const file = (files[filename] ??= { lines: Object.create(null), branches: Object.create(null) });
      const lineRe = /<line\b([^>]*)\/?>/g;
      let lm;
      while ((lm = lineRe.exec(body))) {
        const attrs = lm[1];
        const number = Number(attr(attrs, "number"));
        const hits = Number(attr(attrs, "hits") ?? "0");
        if (!Number.isFinite(number)) continue;
        file.lines[number] = (file.lines[number] ?? 0) + hits;
        const cond = attr(attrs, "condition-coverage");
        if (cond) {
          const m = cond.match(/\((\d+)\/(\d+)\)/);
          if (m) {
            file.branches[number] = {
              hit: (file.branches[number]?.hit ?? 0) + Number(m[1]),
              total: (file.branches[number]?.total ?? 0) + Number(m[2]),
            };
          }
        }
      }
    }
  }
  for (const file of Object.values(files)) {
    for (const hits of Object.values(file.lines)) {
      lineTotal++;
      if (hits > 0) lineHit++;
    }
    for (const b of Object.values(file.branches)) {
      branchTotal += b.total;
      branchHit += b.hit;
    }
  }
  return {
    line: percent(lineHit, lineTotal),
    branch: percent(branchHit, branchTotal),
    files,
    lineHit,
    lineTotal,
    branchHit,
    branchTotal,
  };
}

function summarizeLcov(lcovPath) {
  if (!lcovPath || !existsSync(lcovPath)) return emptySummary();
  const files = Object.create(null);
  let current = null;
  let lineHit = 0,
    lineTotal = 0,
    branchHit = 0,
    branchTotal = 0;
  for (const raw of readFileSync(lcovPath, "utf8").split(/\r?\n/)) {
    if (raw.startsWith("SF:")) {
      current = (files[mergeKey(files, raw.slice(3))] ??= { lines: Object.create(null), branches: Object.create(null) });
    } else if (raw.startsWith("DA:") && current) {
      const [n, hits] = raw.slice(3).split(",");
      current.lines[Number(n)] = (current.lines[Number(n)] ?? 0) + Number(hits);
    } else if (raw.startsWith("BRDA:") && current) {
      const [n, , , taken] = raw.slice(5).split(",");
      const hit = taken === "-" ? 0 : Number(taken) > 0 ? 1 : 0;
      const slot = (current.branches[Number(n)] ??= { hit: 0, total: 0 });
      slot.total++;
      slot.hit += hit;
    }
  }
  for (const file of Object.values(files)) {
    for (const hits of Object.values(file.lines)) {
      lineTotal++;
      if (hits > 0) lineHit++;
    }
    for (const b of Object.values(file.branches)) {
      branchTotal += b.total;
      branchHit += b.hit;
    }
  }
  return {
    line: percent(lineHit, lineTotal),
    branch: percent(branchHit, branchTotal),
    files,
    lineHit,
    lineTotal,
    branchHit,
    branchTotal,
  };
}

function emptySummary() {
  return { line: 0, branch: 0, files: Object.create(null), lineHit: 0, lineTotal: 0, branchHit: 0, branchTotal: 0 };
}

function findLcov(dir) {
  if (!existsSync(dir)) return null;
  const direct = join(dir, "lcov.info");
  if (existsSync(direct)) return direct;
  const found = walk(dir).find((f) => f.endsWith("lcov.info"));
  return found ?? null;
}

function changedLines(root, base) {
  let diff;
  try {
    diff = execFileSync("git", ["diff", "-U0", "--no-color", base, "HEAD", "--", "src", "web/src", "deploy"], {
      cwd: root,
      encoding: "utf8",
      maxBuffer: 20 * 1024 * 1024,
    });
  } catch (e) {
    diff = e.stdout ?? "";
  }
  const files = new Map();
  let path = "";
  let newLine = 0;
  for (const line of diff.split(/\n/)) {
    if (line.startsWith("+++ b/")) {
      path = line.slice(6);
      if (!files.has(path)) files.set(path, new Set());
      continue;
    }
    const hunk = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
    if (hunk) {
      newLine = Number(hunk[1]);
      continue;
    }
    if (!path || path === "/dev/null") continue;
    if (line.startsWith("+") && !line.startsWith("+++")) {
      files.get(path).add(newLine);
      newLine++;
    } else if (line.startsWith("-") && !line.startsWith("---")) {
      // removed from old file; new-file cursor stays
    } else if (line.startsWith("\\")) {
      // "No newline at end of file"
    } else {
      newLine++;
    }
  }
  return { files };
}

function diffCoverage(changed, coverageFiles, pathPred) {
  let lineHit = 0,
    lineTotal = 0,
    branchHit = 0,
    branchTotal = 0;
  for (const [path, lines] of changed.files) {
    if (!pathPred(path)) continue;
    if (path.endsWith(".spec.ts") || path.includes(".Tests/") || path.includes("/Migrations/")) continue;
    if (!(path.endsWith(".cs") || path.endsWith(".ts"))) continue;
    const cov = lookupFile(coverageFiles, path);
    if (!cov) continue;
    for (const n of lines) {
      if (cov.lines[n] === undefined) continue;
      lineTotal++;
      if (cov.lines[n] > 0) lineHit++;
      if (cov.branches[n]) {
        branchTotal += cov.branches[n].total;
        branchHit += cov.branches[n].hit;
      }
    }
  }
  return {
    line: { hit: lineHit, total: lineTotal, percent: percent(lineHit, lineTotal) },
    branch: { hit: branchHit, total: branchTotal, percent: percent(branchHit, branchTotal) },
  };
}

function lookupFile(files, path) {
  const want = canonicalSource(path);
  if (files[want]) return files[want];
  const keys = Object.keys(files);
  const hit = keys.find((k) => k.endsWith(want) || want.endsWith(k) || k.endsWith(want.split("/").slice(-2).join("/")));
  return hit ? files[hit] : null;
}

function mergeKey(files, path) {
  const canon = canonicalSource(path);
  if (files[canon]) return canon;
  const suffix = canon.split("/").pop();
  const existing = Object.keys(files).find((k) => k === suffix || k.endsWith("/" + suffix) || canon.endsWith("/" + k));
  return existing ?? canon;
}

function canonicalSource(p) {
  p = p.replaceAll("\\", "/").replace(/^\.\//, "");
  const src = p.match(/(?:^|\/)(src\/Desk\.[^/]+\/.*)$/);
  if (src) return src[1];
  if (p.includes("web/src/")) return p.slice(p.indexOf("web/src/"));
  if (/^src\//.test(p)) return p;
  if (/^Desk\.(Api|Data|Seeder)\//.test(p)) return "src/" + p;
  if (p.startsWith("App/")) return "src/Desk.Data/" + p;
  if (p.startsWith("src/")) return p;
  return p;
}

function percent(hit, total) {
  if (!total) return 0;
  return (100 * hit) / total;
}

function attr(attrs, name) {
  const m = attrs.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : undefined;
}

function walk(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) out.push(...walk(p));
    else out.push(p);
  }
  return out;
}

