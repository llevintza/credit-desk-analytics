#!/usr/bin/env node
/**
 * Coverage gates (README §11 / CI):
 *   1. New/changed coverable lines vs the merge-base with BASE_SHA >= diffLineMinPercent
 *      (same for branches).
 *   2. Overall line/branch % per project never drops vs the baseline at BASE_SHA.
 *   3. The committed perf/coverage-baseline.json must match the measured numbers.
 *
 * CI runs THIS FILE from the BASE checkout (`_base/perf/coverage-gate.mjs`) against
 * the PR head's coverage artifacts. Thresholds and the floor are loaded with
 * `git show $BASE_SHA:…`. The PR head cannot rewrite the rules by editing this script.
 *
 * Bootstrap (this PR only): main has no `perf/coverage-gate.mjs`. CI uses the head
 * copy once. After merge, a missing thresholds or baseline file at a base that
 * already has the script is a hard failure (never a silent 0/0 floor).
 *
 * Fail closed: missing/empty/non-numeric/NaN schema, unresolvable BASE_SHA or
 * merge-base, or a git show/diff error. Comparisons use `!(actual >= floor)` so
 * NaN/undefined fail.
 */
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync, appendFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const BOOTSTRAP_THRESHOLDS = Object.freeze({
  diffLineMinPercent: 80,
  diffBranchMinPercent: 80,
  overallMustNotDrop: true,
  baselineMatchTolerancePercent: 0.5,
});

export const ZERO_FLOOR = Object.freeze({
  dotnet: Object.freeze({ line: 0, branch: 0 }),
  web: Object.freeze({ line: 0, branch: 0 }),
});

export const NO_DROP_EPS = 1e-9;

const THRESHOLD_KEYS = Object.freeze([
  "baselineMatchTolerancePercent",
  "diffBranchMinPercent",
  "diffLineMinPercent",
  "overallMustNotDrop",
]);

export class GateFailure extends Error {
  constructor(message, output = "") {
    super(message);
    this.name = "GateFailure";
    this.output = output;
    this.exitCode = 1;
  }
}

export function parseArgs(argv) {
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

export function finitePercent(value, label) {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || value > 100) {
    throw new GateFailure(`${label} must be a finite number in 0..100, got ${JSON.stringify(value)}.`);
  }
  return value;
}

export function validateThresholds(obj, label = "thresholds") {
  if (obj == null || typeof obj !== "object" || Array.isArray(obj)) {
    throw new GateFailure(`${label} must be a JSON object.`);
  }
  const keys = Object.keys(obj).sort();
  const extra = keys.filter((k) => !THRESHOLD_KEYS.includes(k));
  const missing = THRESHOLD_KEYS.filter((k) => !keys.includes(k));
  if (extra.length || missing.length) {
    throw new GateFailure(
      `${label} schema: missing [${missing.join(", ")}], unknown [${extra.join(", ")}].`,
    );
  }
  finitePercent(obj.diffLineMinPercent, `${label}.diffLineMinPercent`);
  finitePercent(obj.diffBranchMinPercent, `${label}.diffBranchMinPercent`);
  finitePercent(obj.baselineMatchTolerancePercent, `${label}.baselineMatchTolerancePercent`);
  if (typeof obj.overallMustNotDrop !== "boolean") {
    throw new GateFailure(`${label}.overallMustNotDrop must be a boolean.`);
  }
  return obj;
}

export function validateProjectPair(obj, label) {
  if (obj == null || typeof obj !== "object" || Array.isArray(obj)) {
    throw new GateFailure(`${label} must be an object with line and branch.`);
  }
  const keys = Object.keys(obj).sort();
  if (keys.join(",") !== "branch,line") {
    throw new GateFailure(`${label} must have exactly {line, branch}, got keys [${keys.join(", ")}].`);
  }
  finitePercent(obj.line, `${label}.line`);
  finitePercent(obj.branch, `${label}.branch`);
  return obj;
}

export function validateBaseline(obj, label = "baseline") {
  if (obj == null || typeof obj !== "object" || Array.isArray(obj)) {
    throw new GateFailure(`${label} must be a JSON object.`);
  }
  const keys = Object.keys(obj).sort();
  if (keys.join(",") !== "dotnet,web") {
    throw new GateFailure(`${label} must have exactly {dotnet, web}, got keys [${keys.join(", ")}].`);
  }
  validateProjectPair(obj.dotnet, `${label}.dotnet`);
  validateProjectPair(obj.web, `${label}.web`);
  return obj;
}

/** NaN-safe: NaN >= x is false, so this FAILS closed. */
export function meetsFloor(actual, floor, eps = NO_DROP_EPS) {
  return actual + eps >= floor;
}

export function round1(n) {
  return Math.round(Number(n) * 10) / 10;
}

export function floor1(n) {
  return Math.floor(Number(n) * 10 + 1e-8) / 10;
}

export function pct(n) {
  return Number(n).toFixed(1);
}

export function runGate(options = {}) {
  const argv = options.argv ?? process.argv.slice(2);
  const collected = [];
  const say = (s) => collected.push(s);
  const write = (s) => {
    if (options.stdoutWrite) options.stdoutWrite(s);
    else process.stdout.write(s);
  };
  const writeErr = (s) => {
    if (options.stderrWrite) options.stderrWrite(s);
    else process.stderr.write(s);
  };

  const failHard = (msg) => {
    const text = `- **FAIL** ${msg}\n`;
    collected.push(text.trimEnd());
    write(text);
    writeErr(`FAIL: ${msg}\n`);
    throw new GateFailure(msg, collected.join("\n") + "\n");
  };

  try {
    const args = parseArgs(argv);
    const repoRoot = resolve(options.root ?? args.root ?? ".");
    const thresholdsPath = args.thresholds ?? "perf/coverage-thresholds.json";
    const baselinePath = args.baseline ?? "perf/coverage-baseline.json";
    const gatePath = args["gate-script"] ?? "perf/coverage-gate.mjs";
    const baseSha = args.base ?? env("BASE_SHA") ?? "";

    const dotnetDir = resolve(repoRoot, args.dotnet ?? "TestResults/coverage");
    const webDir = resolve(repoRoot, args.web ?? "web/coverage");

    requireCommit(repoRoot, baseSha, failHard);
    const mergeBase = requireMergeBase(repoRoot, baseSha, failHard);

    const scriptAtBase = fileExistsAtRef(repoRoot, baseSha, gatePath, failHard);
    const baseThresholds = loadJsonAtRef(repoRoot, baseSha, thresholdsPath, failHard);
    const baseBaseline = loadJsonAtRef(repoRoot, baseSha, baselinePath, failHard);

    if (scriptAtBase) {
      if (!baseThresholds.present) {
        failHard(`base has ${gatePath} but is missing ${thresholdsPath}; refusing to bootstrap.`);
      }
      if (!baseBaseline.present) {
        failHard(`base has ${gatePath} but is missing ${baselinePath}; refusing to bootstrap.`);
      }
    }

    const bootstrapped = !scriptAtBase;
    const thresholds = validateThresholds(
      bootstrapped || !baseThresholds.present ? { ...BOOTSTRAP_THRESHOLDS } : baseThresholds.value,
      bootstrapped ? "bootstrap thresholds" : "BASE_SHA thresholds",
    );
    const mainBaseline = validateBaseline(
      bootstrapped || !baseBaseline.present ? structuredClone(ZERO_FLOOR) : baseBaseline.value,
      bootstrapped ? "bootstrap baseline" : "BASE_SHA baseline",
    );

    const headThresholds = validateThresholds(readHeadJson(repoRoot, thresholdsPath, "thresholds", failHard), "head thresholds");
    const committed = validateBaseline(readHeadJson(repoRoot, baselinePath, "baseline", failHard), "head baseline");

    assertThresholdsNotLooser(headThresholds, thresholds, bootstrapped, failHard);
    if (!bootstrapped) assertBaselineNotLowered(committed, mainBaseline, failHard);

    const unknownPaths = [];
    const dotnet = summarizeDotnet(dotnetDir, unknownPaths);
    const web = summarizeLcov(findLcov(webDir), unknownPaths);
    if (unknownPaths.length) {
      failHard(
        "cannot canonicalize coverage paths (no basename merge). Unknown:\n- " + unknownPaths.join("\n- "),
      );
    }

    const measured = { dotnet, web };
    const changed = changedLines(repoRoot, mergeBase, failHard);
    const unmapped = [];
    const diff = {
      dotnet: diffCoverage(changed, dotnet.files, (p) => isDotnetSource(p), unmapped),
      web: diffCoverage(changed, web.files, (p) => isWebSource(p), unmapped),
    };

    let failed = false;
    say("## Coverage");
    say("");
    if (bootstrapped) {
      say(
        "**Bootstrap:** BASE_SHA has no `perf/coverage-gate.mjs`. This PR may establish the gate, " +
          "thresholds, and baseline. Hardcoded bootstrap thresholds are diff ≥ 80/80, `overallMustNotDrop: true`, " +
          "tolerance 0.5. After merge, CI runs `_base/perf/coverage-gate.mjs` (the copy at BASE_SHA) and a missing " +
          "thresholds or baseline file **fails closed**. Future PRs cannot set their own floor. Only a push to " +
          "`main` ratchets the baseline, and only upward.",
      );
      say("");
    }
    say("| Project | Overall line | Overall branch | Diff line | Diff branch | Floor (main) line | Floor (main) branch |");
    say("|---|---:|---:|---:|---:|---:|---:|");
    for (const name of ["dotnet", "web"]) {
      const m = measured[name];
      const d = diff[name];
      const f = mainBaseline[name];
      say(
        `| ${name} | ${pct(m.line)}% | ${pct(m.branch)}% | ${diffPct(d.line)} | ${diffPct(d.branch)} | ${pct(f.line)}% | ${pct(f.branch)}% |`,
      );
    }
    say("");
    const tol = thresholds.baselineMatchTolerancePercent;
    say(`Base SHA: \`${baseSha}\`. Merge-base: \`${mergeBase}\`.`);
    say(bootstrapped ? "Gate script: **head copy** (bootstrap; absent on BASE_SHA)." : "Gate script: loaded from BASE checkout (not the PR head).");
    say(bootstrapped ? "Thresholds: **bootstrap defaults**." : "Thresholds: loaded from BASE_SHA (not the PR head).");
    say(bootstrapped ? "Floor: **0 / 0** (bootstrap)." : "Floor: loaded from BASE_SHA (not the PR head).");
    say(
      `Match tolerance at BASE_SHA: ${tol}pp (a PR may not widen it). No-drop uses 1-decimal plus epsilon ${NO_DROP_EPS}.`,
    );
    say("");

    if (unmapped.length) {
      say(
        "- Changed `src/` or `web/src` files with no coverage data are counted as **0%** (never skipped): " +
          unmapped.map((p) => `\`${p}\``).join(", ") +
          ".",
      );
    }

    for (const name of ["dotnet", "web"]) {
      const m = measured[name];
      const f = mainBaseline[name];
      if (thresholds.overallMustNotDrop) {
        if (!meetsFloor(round1(m.line), round1(f.line))) {
          failed = true;
          say(`- **FAIL** ${name} overall line ${pct(m.line)}% dropped below main ${pct(f.line)}%.`);
        }
        if (!meetsFloor(round1(m.branch), round1(f.branch))) {
          failed = true;
          say(`- **FAIL** ${name} overall branch ${pct(m.branch)}% dropped below main ${pct(f.branch)}%.`);
        }
      }
      const d = diff[name];
      if (d.line.total > 0 && !meetsFloor(d.line.percent, thresholds.diffLineMinPercent)) {
        failed = true;
        say(
          `- **FAIL** ${name} diff line ${pct(d.line.percent)}% (${d.line.hit}/${d.line.total}) < ${thresholds.diffLineMinPercent}%.`,
        );
      }
      if (d.branch.total > 0 && !meetsFloor(d.branch.percent, thresholds.diffBranchMinPercent)) {
        failed = true;
        say(
          `- **FAIL** ${name} diff branch ${pct(d.branch.percent)}% (${d.branch.hit}/${d.branch.total}) < ${thresholds.diffBranchMinPercent}%.`,
        );
      }
    }

    for (const name of ["dotnet", "web"]) {
      const m = measured[name];
      const c = committed[name];
      const f = mainBaseline[name];
      if (!bootstrapped && (!meetsFloor(round1(c.line), round1(f.line)) || !meetsFloor(round1(c.branch), round1(f.branch)))) {
        failed = true;
        say(`- **FAIL** committed baseline ${name} is below main's floor.`);
      }
      if (!(round1(c.line) <= round1(m.line) + NO_DROP_EPS) || !(round1(c.branch) <= round1(m.branch) + NO_DROP_EPS)) {
        failed = true;
        say(
          `- **FAIL** committed baseline ${name} line/branch ${pct(c.line)}/${pct(c.branch)} is above measured ${pct(m.line)}/${pct(m.branch)}.`,
        );
      }
      if (!meetsFloor(round1(c.line), floor1(m.line)) || !meetsFloor(round1(c.branch), floor1(m.branch))) {
        failed = true;
        say(
          `- **FAIL** committed baseline ${name} line/branch ${pct(c.line)}/${pct(c.branch)} is behind measured ${pct(m.line)}/${pct(m.branch)}. Update \`perf/coverage-baseline.json\` to the measured JSON below.`,
        );
      }
    }

    if (!failed) say("- All coverage gates passed.");

    const body = collected.join("\n") + "\n";
    write(body);
    const summary = options.summaryPath ?? env("GITHUB_STEP_SUMMARY");
    if (summary) appendFileSync(summary, body);

    const expected = {
      dotnet: { line: round1(dotnet.line), branch: round1(dotnet.branch) },
      web: { line: round1(web.line), branch: round1(web.branch) },
    };
    write("\nMeasured baseline JSON:\n" + JSON.stringify(expected, null, 2) + "\n");

    return { failed, output: body, expected, exitCode: failed ? 1 : 0, bootstrapped, mergeBase, baseSha };
  } catch (e) {
    if (e instanceof GateFailure) {
      const summary = options.summaryPath ?? env("GITHUB_STEP_SUMMARY");
      if (summary && e.output) appendFileSync(summary, e.output);
      return { failed: true, output: e.output || e.message, expected: null, exitCode: 1, error: e };
    }
    throw e;
  }
}

function env(name) {
  const v = process.env[name];
  return v && v.length ? v : undefined;
}

function diffPct(s) {
  if (!s.total) return "n/a";
  return `${pct(s.percent)}% (${s.hit}/${s.total})`;
}

function git(root, gitArgs, failHard) {
  try {
    return execFileSync("git", gitArgs, {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      maxBuffer: 20 * 1024 * 1024,
    });
  } catch (e) {
    const err = String(e.stderr || e.message || e).trim();
    failHard(`git ${gitArgs.join(" ")} failed: ${err}`);
  }
}

function gitOk(root, gitArgs) {
  try {
    execFileSync("git", gitArgs, {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    return true;
  } catch {
    return false;
  }
}

function requireCommit(root, sha, failHard) {
  if (!sha || sha === "0000000000000000000000000000000000000000") {
    failHard("BASE_SHA is missing; refusing to treat a missing base as floor 0 or as no changed lines.");
  }
  if (!gitOk(root, ["cat-file", "-e", `${sha}^{commit}`])) {
    failHard(`cannot resolve base commit ${sha} (git cat-file). Failing closed.`);
  }
}

function requireMergeBase(root, sha, failHard) {
  let mb;
  try {
    mb = execFileSync("git", ["merge-base", sha, "HEAD"], {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
  } catch (e) {
    const err = String(e.stderr || e.message || e).trim();
    failHard(`cannot resolve merge-base of ${sha} and HEAD: ${err}`);
  }
  if (!mb) failHard(`git merge-base ${sha} HEAD returned empty. Failing closed.`);
  return mb;
}

function fileExistsAtRef(root, ref, path, failHard) {
  let listed;
  try {
    listed = execFileSync("git", ["ls-tree", "-r", "--name-only", ref, "--", path], {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
  } catch (e) {
    const err = String(e.stderr || e.message || e).trim();
    failHard(`git ls-tree ${ref} -- ${path} failed: ${err}`);
  }
  return listed.length > 0;
}

function loadJsonAtRef(root, ref, path, failHard) {
  if (!fileExistsAtRef(root, ref, path, failHard)) return { present: false, value: null };
  let raw;
  try {
    raw = execFileSync("git", ["show", `${ref}:${path}`], {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
  } catch (e) {
    const err = String(e.stderr || e.message || e).trim();
    failHard(`git show ${ref}:${path} failed: ${err}`);
  }
  try {
    return { present: true, value: JSON.parse(raw) };
  } catch (e) {
    failHard(`cannot parse ${path} at ${ref}: ${e.message}`);
  }
}

function readHeadJson(root, path, label, failHard) {
  const full = join(root, path);
  if (!existsSync(full)) failHard(`head is missing ${path} (${label}).`);
  try {
    return JSON.parse(readFileSync(full, "utf8"));
  } catch (e) {
    failHard(`cannot parse head ${path}: ${e.message}`);
  }
}

function assertThresholdsNotLooser(head, base, fromBootstrap, failHard) {
  const origin = fromBootstrap ? "bootstrap defaults" : "BASE_SHA";
  if (!(head.diffLineMinPercent >= base.diffLineMinPercent)) {
    failHard(`PR lowers diffLineMinPercent (${head.diffLineMinPercent} < ${base.diffLineMinPercent} from ${origin}).`);
  }
  if (!(head.diffBranchMinPercent >= base.diffBranchMinPercent)) {
    failHard(`PR lowers diffBranchMinPercent (${head.diffBranchMinPercent} < ${base.diffBranchMinPercent} from ${origin}).`);
  }
  if (base.overallMustNotDrop && head.overallMustNotDrop !== true) {
    failHard(`PR turns off overallMustNotDrop (required by ${origin}).`);
  }
  if (!(head.baselineMatchTolerancePercent <= base.baselineMatchTolerancePercent)) {
    failHard(
      `PR widens baselineMatchTolerancePercent (${head.baselineMatchTolerancePercent} > ${base.baselineMatchTolerancePercent} from ${origin}).`,
    );
  }
}

function assertBaselineNotLowered(head, base, failHard) {
  for (const name of ["dotnet", "web"]) {
    const h = head[name];
    const b = base[name];
    if (!meetsFloor(round1(h.line), round1(b.line)) || !meetsFloor(round1(h.branch), round1(b.branch))) {
      failHard(
        `PR lowers the committed ${name} baseline below BASE_SHA (${pct(h.line)}/${pct(h.branch)} < ${pct(b.line)}/${pct(b.branch)}). Only a push to main may ratchet, and only upward.`,
      );
    }
  }
}

export function summarizeDotnet(dir, unknownPaths) {
  const files = Object.create(null);
  if (!existsSync(dir)) return emptySummary();
  for (const xml of walk(dir).filter((f) => f.endsWith(".xml") && f.includes("cobertura"))) {
    const text = readFileSync(xml, "utf8");
    const sources = [...text.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => m[1].trim());
    const packageRe = /<package\b([^>]*)>([\s\S]*?)<\/package>/g;
    let pkg;
    while ((pkg = packageRe.exec(text))) {
      const packageName = attr(pkg[1], "name") ?? "";
      const classRe = /<class\b([^>]*)>([\s\S]*?)<\/class>/g;
      let cm;
      while ((cm = classRe.exec(pkg[2]))) {
        const filename = attr(cm[1], "filename") ?? "";
        const key = canonicalCobertura(filename, sources, packageName, unknownPaths);
        if (!key) continue;
        const file = (files[key] ??= { lines: Object.create(null), branches: Object.create(null) });
        const lineRe = /<line\b([^>]*)\/?>/g;
        let lm;
        while ((lm = lineRe.exec(cm[2]))) {
          const attrs = lm[1];
          const number = Number(attr(attrs, "number"));
          const hits = Number(attr(attrs, "hits") ?? "0");
          if (!Number.isFinite(number)) continue;
          file.lines[number] = Math.max(file.lines[number] ?? 0, hits);
          const cond = attr(attrs, "condition-coverage");
          if (cond) {
            const m = cond.match(/\((\d+)\/(\d+)\)/);
            if (m) {
              const hit = Number(m[1]);
              const total = Number(m[2]);
              const prev = file.branches[number];
              file.branches[number] = prev
                ? { hit: Math.max(prev.hit, hit), total: Math.max(prev.total, total) }
                : { hit, total };
            }
          }
        }
      }
    }
  }
  return tally(files);
}

export function summarizeLcov(lcovPath, unknownPaths) {
  if (!lcovPath || !existsSync(lcovPath)) return emptySummary();
  const files = Object.create(null);
  let current = null;
  for (const raw of readFileSync(lcovPath, "utf8").split(/\r?\n/)) {
    if (raw.startsWith("SF:")) {
      const key = canonicalLcov(raw.slice(3), unknownPaths);
      current = key ? (files[key] ??= { lines: Object.create(null), branches: Object.create(null) }) : null;
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
  return tally(files);
}

function tally(files) {
  let lineHit = 0,
    lineTotal = 0,
    branchHit = 0,
    branchTotal = 0;
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

function changedLines(root, base, failHard) {
  const diff = git(root, ["diff", "-U0", "--no-color", base, "HEAD", "--", "src", "web/src"], failHard);
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
      // removed from old file
    } else if (line.startsWith("\\")) {
      // no newline at end of file
    } else {
      newLine++;
    }
  }
  return { files };
}

export function isCoverableSource(path) {
  const p = path.replaceAll("\\", "/");
  if (p.endsWith(".spec.ts")) return false;
  if (p.includes(".Tests/") || p.includes("/tests/")) return false;
  if (p.includes("/Migrations/")) return false;
  return (p.endsWith(".cs") && isDotnetSource(p)) || (p.endsWith(".ts") && isWebSource(p));
}

function isDotnetSource(path) {
  const p = path.replaceAll("\\", "/");
  return p.startsWith("src/") || p.includes("/src/Desk.");
}

function isWebSource(path) {
  const p = path.replaceAll("\\", "/");
  return p.startsWith("web/src/") || p.includes("/web/src/");
}

export function diffCoverage(changed, coverageFiles, pathPred, unmapped) {
  let lineHit = 0,
    lineTotal = 0,
    branchHit = 0,
    branchTotal = 0;
  for (const [path, lineSet] of changed.files) {
    if (!pathPred(path) || !isCoverableSource(path)) continue;
    const key = canonicalGitPath(path);
    const cov = coverageFiles[key];
    if (!cov) {
      unmapped.push(key);
      for (const _ of lineSet) lineTotal++;
      continue;
    }
    for (const n of lineSet) {
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

function canonicalGitPath(p) {
  p = p.replaceAll("\\", "/").replace(/^\.\//, "");
  if (p.startsWith("src/") || p.startsWith("web/src/")) return p;
  const src = p.match(/(?:^|\/)(src\/Desk\.[^/]+\/.*)$/);
  if (src) return src[1];
  if (p.includes("web/src/")) return p.slice(p.indexOf("web/src/"));
  return p;
}

export function canonicalCobertura(filename, sources, packageName, unknownPaths) {
  const norm = (filename ?? "").replaceAll("\\", "/").replace(/^\.\//, "");
  const extracted = extractRepoPath(norm);
  if (extracted) return extracted;
  for (const source of sources) {
    const combined = joinPosix(source.replaceAll("\\", "/"), norm);
    const fromJoin = extractRepoPath(combined);
    if (fromJoin) return fromJoin;
    const srcRoot = source.replaceAll("\\", "/").match(/(?:^|\/)(src\/Desk\.[^/]+)\/?$/);
    if (srcRoot && norm && !norm.startsWith("src/")) {
      return `${srcRoot[1].replace(/\/+$/, "")}/${norm.replace(/^\/+/, "")}`;
    }
  }
  if (/^Desk\.[A-Za-z0-9.]+\/.+/.test(norm)) return `src/${norm}`;
  if (packageName && /^Desk\.[A-Za-z0-9.]+$/.test(packageName) && norm) {
    if (norm.startsWith(`${packageName}/`)) return `src/${norm}`;
    return `src/${packageName}/${norm}`;
  }
  unknownPaths.push(`cobertura filename=${filename} package=${packageName} sources=${sources.join("|")}`);
  return null;
}

export function canonicalLcov(sf, unknownPaths) {
  const norm = (sf ?? "").replaceAll("\\", "/");
  if (norm.includes("web/src/")) return norm.slice(norm.indexOf("web/src/"));
  if (norm.startsWith("web/src/")) return norm;
  if (/(?:^|\/)src\//.test(norm) && !norm.includes("src/Desk.")) {
    const src = norm.match(/(?:^|\/)(src\/.*)$/);
    if (src) return `web/${src[1]}`;
  }
  unknownPaths.push(`lcov SF=${sf}`);
  return null;
}

function extractRepoPath(p) {
  const src = p.match(/(?:^|\/)(src\/Desk\.[^/]+\/.+)$/);
  if (src) return src[1];
  const web = p.match(/(?:^|\/)(web\/src\/.+)$/);
  if (web) return web[1];
  return null;
}

function joinPosix(a, b) {
  if (!a) return b;
  if (!b) return a;
  if (b.startsWith("/")) return b;
  return a.replace(/\/+$/, "") + "/" + b.replace(/^\/+/, "");
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

function isDirectRun() {
  const entry = process.argv[1] && resolve(process.argv[1]);
  return Boolean(entry) && pathToFileURL(entry).href === import.meta.url;
}

if (isDirectRun()) {
  const result = runGate({ argv: process.argv.slice(2) });
  process.exit(result.exitCode);
}
