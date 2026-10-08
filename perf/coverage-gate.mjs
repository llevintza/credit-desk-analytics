#!/usr/bin/env node
/**
 * Coverage gates (README §11 / CI):
 *   1. New/changed coverable lines vs the merge-base with BASE_SHA >= diffLineMinPercent
 *      (same for branches).
 *   2. Overall line/branch % per project never drops vs the baseline at BASE_SHA.
 *   3. The committed baseline must match measured numbers and must not sit
 *      below the BASE_SHA floor.
 *
 * CI always runs THIS FILE from the BASE_SHA checkout
 * (`_base/perf/coverage-gate.mjs`). There is no HEAD fallback. If `_base` is
 * missing this script, the workflow fails closed before `node` (bootstrap
 * closed after #5). Thresholds and the floor are loaded with
 * `git show $BASE_SHA:…` (never from the commit under test).
 *
 * A missing or renamed gate script, thresholds file, or baseline on BASE_SHA
 * or HEAD fails closed. PRs whose base is not the default branch fail closed.
 * History such as `git log --all --not HEAD -- perf/coverage-gate.mjs` is not
 * a success path.
 *
 * Push to main: BASE_SHA is `github.event.before`. Empty / zero / unknown
 * `before` fails closed. Empty `--base-ref` is empty (not "true"). The
 * retarget check runs only on `pull_request`. Floor, thresholds, and the gate
 * script come from `_base` at BASE_SHA so a push cannot rewrite the rules it
 * is judged by.
 *
 * `perf/coverage-override.json` is applied only when the file differs from
 * BASE_SHA (documented measurement-scope change).
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

export const OVERRIDE_FILE = "perf/coverage-override.json";
export const TESTCONFIG_FILE = "tests/testconfig.json";

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
      const next = argv[i + 1];
      // Empty string is a value: push CI passes `--base-ref ""`.
      const val = next !== undefined && !String(next).startsWith("--") ? argv[++i] : "true";
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

export function validateOverride(obj, label = "override") {
  if (obj == null || typeof obj !== "object" || Array.isArray(obj)) {
    throw new GateFailure(`${label} must be a JSON object.`);
  }
  const keys = Object.keys(obj).sort();
  if (keys.join(",") !== "from,reason,to") {
    throw new GateFailure(`${label} must have exactly {from, reason, to}, got keys [${keys.join(", ")}].`);
  }
  if (typeof obj.reason !== "string" || !obj.reason.trim()) {
    throw new GateFailure(`${label}.reason must be a non-empty string.`);
  }
  validateBaseline(obj.from, `${label}.from`);
  validateBaseline(obj.to, `${label}.to`);
  return obj;
}

export function baselinePairEq(a, b) {
  return round1(a.line) === round1(b.line) && round1(a.branch) === round1(b.branch);
}

export function baselineEq(a, b) {
  return baselinePairEq(a.dotnet, b.dotnet) && baselinePairEq(a.web, b.web);
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

  let decisionPath = "fail/unknown";
  const failHard = (msg) => {
    const mark = `gate-path: ${decisionPath}`;
    const text = `${mark}\n- **FAIL** ${msg}\n`;
    collected.push(mark);
    collected.push(`- **FAIL** ${msg}`);
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
    const defaultBranch = args["default-branch"] ?? env("DEFAULT_BRANCH") ?? "main";
    const baseRef = String(args["base-ref"] ?? env("GITHUB_BASE_REF") ?? "")
      .replace(/^refs\/heads\//, "")
      .trim();
    const eventName = String(args.event ?? env("GITHUB_EVENT_NAME") ?? "")
      .trim()
      .toLowerCase();
    const isPush = eventName === "push";
    const isPullRequest =
      eventName === "pull_request" ||
      eventName === "pull_request_target" ||
      (!isPush && Boolean(baseRef));

    const dotnetDir = resolve(repoRoot, args.dotnet ?? "TestResults/coverage");
    const webDir = resolve(repoRoot, args.web ?? "web/coverage");

    decisionPath = "fail/missing-base-sha";
    requireCommit(repoRoot, baseSha, failHard);
    const mergeBase = requireMergeBase(repoRoot, baseSha, failHard);

    if (isPullRequest && baseRef && baseRef !== defaultBranch) {
      decisionPath = "fail/retarget";
      failHard(
        `PR base '${baseRef}' is not the default branch '${defaultBranch}'. ` +
          `Coverage is only evaluated against ${defaultBranch} (retarget bypass).`,
      );
    }

    const baseDir = args["base-dir"] ? resolve(repoRoot, args["base-dir"]) : null;
    const defaultDir = args["default-dir"] ? resolve(repoRoot, args["default-dir"]) : null;
    const defaultSha =
      args["default-sha"] ??
      env("DEFAULT_SHA") ??
      resolveDefaultSha(repoRoot, defaultBranch, defaultDir, failHard);

    const gateOnBase = filePresent(repoRoot, baseDir, baseSha, gatePath, failHard);
    const headHasGate = existsSync(join(repoRoot, gatePath));
    const headHasThresholds = existsSync(join(repoRoot, thresholdsPath));
    const headHasBaseline = existsSync(join(repoRoot, baselinePath));
    const who = isPush ? "This push" : "This PR";

    if (!gateOnBase) {
      decisionPath = "fail/missing-base-gate";
      failHard(
        `BASE_SHA has no ${gatePath}. Bootstrap closed after #5; rebase onto main. Failing closed.`,
      );
    }
    decisionPath = isPush ? "push/base" : "pr/base";
    if (!headHasGate) {
      decisionPath = "fail/missing-head-gate";
      failHard(`head is missing ${gatePath}; deleting or renaming the gate fails closed.`);
    }
    if (!headHasThresholds) failHard(`head is missing ${thresholdsPath}; refusing to run without thresholds.`);
    if (!headHasBaseline) failHard(`head is missing ${baselinePath}; refusing to run without a baseline.`);

    const floorSourceSha = defaultSha || baseSha;
    const floorDir = defaultDir || baseDir;
    const baseThresholds = loadJsonFromBase(repoRoot, floorDir, floorSourceSha, thresholdsPath, failHard);
    const baseBaseline = loadJsonFromBase(repoRoot, floorDir, floorSourceSha, baselinePath, failHard);

    if (!baseThresholds.present) {
      failHard(`BASE_SHA has ${gatePath} but is missing ${thresholdsPath}; failing closed.`);
    }
    if (!baseBaseline.present) {
      failHard(`BASE_SHA has ${gatePath} but is missing ${baselinePath}; failing closed.`);
    }

    const thresholds = validateThresholds(baseThresholds.value, "BASE_SHA thresholds");
    const mainBaseline = validateBaseline(baseBaseline.value, "BASE_SHA baseline");

    const headThresholds = validateThresholds(readHeadJson(repoRoot, thresholdsPath, "thresholds", failHard), "head thresholds");
    const committed = validateBaseline(readHeadJson(repoRoot, baselinePath, "baseline", failHard), "head baseline");
    const overrideChanged = fileChangedVsBase(repoRoot, baseSha, OVERRIDE_FILE, failHard);
    const override = overrideChanged ? readHeadOverride(repoRoot, failHard) : null;

    assertThresholdsNotLooser(headThresholds, thresholds, who, failHard);
    let overallFloor = mainBaseline;
    const lowered =
      !meetsFloor(round1(committed.dotnet.line), round1(mainBaseline.dotnet.line)) ||
      !meetsFloor(round1(committed.dotnet.branch), round1(mainBaseline.dotnet.branch)) ||
      !meetsFloor(round1(committed.web.line), round1(mainBaseline.web.line)) ||
      !meetsFloor(round1(committed.web.branch), round1(mainBaseline.web.branch));
    if (lowered) {
      if (
        !override ||
        !baselineEq(override.from, mainBaseline) ||
        !baselineEq(override.to, committed)
      ) {
        failHard(
          `${who} lowers the committed baseline below the BASE_SHA floor. ` +
            `A baseline may be lowered ONLY for a documented change in measurement scope, never to absorb a real coverage drop. ` +
            `Use ${OVERRIDE_FILE} {from, to, reason} in its own [workflows] PR, with sign-off from Code Reviewer, Tech Coordinator and Helms.`,
        );
      }
      overallFloor = override.to;
    } else {
      assertBaselineNotLowered(committed, mainBaseline, who, failHard);
    }

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
    say("## Coverage scope");
    say("");
    for (const line of describeCoverageScope(repoRoot)) say(`- ${line}`);
    say("");
    say("| Project | Overall line | Overall branch | Diff line | Diff branch | Floor (BASE_SHA) line | Floor (BASE_SHA) branch |");
    say("|---|---:|---:|---:|---:|---:|---:|");
    for (const name of ["dotnet", "web"]) {
      const m = measured[name];
      const d = diff[name];
      const f = overallFloor[name];
      say(
        `| ${name} | ${pct(m.line)}% | ${pct(m.branch)}% | ${diffPct(d.line)} | ${diffPct(d.branch)} | ${pct(f.line)}% | ${pct(f.branch)}% |`,
      );
    }
    say("");
    const tol = thresholds.baselineMatchTolerancePercent;
    say(`gate-path: ${decisionPath}`);
    say(`Base SHA: \`${baseSha}\`. Merge-base: \`${mergeBase}\`. Default branch: \`${defaultBranch}\`${defaultSha ? ` (\`${defaultSha}\`)` : ""}.`);
    const src = `\`${gatePath}\` from \`_base\` at BASE_SHA (\`${baseSha}\`)`;
    say(`Gate script: ${src}.`);
    say(`Thresholds: \`${thresholdsPath}\` from \`_base\` at BASE_SHA (\`${baseSha}\`).`);
    say(`Floor: \`${baselinePath}\` from \`_base\` at BASE_SHA (\`${baseSha}\`).`);
    say(
      `Match tolerance at BASE_SHA: ${tol}pp (${who} may not widen it). No-drop uses 1-decimal plus epsilon ${NO_DROP_EPS}.`,
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
      const f = overallFloor[name];
      if (thresholds.overallMustNotDrop) {
        if (!meetsFloor(round1(m.line), round1(f.line))) {
          failed = true;
          say(`- **FAIL** ${name} overall line ${pct(m.line)}% dropped below BASE_SHA ${pct(f.line)}%.`);
        }
        if (!meetsFloor(round1(m.branch), round1(f.branch))) {
          failed = true;
          say(`- **FAIL** ${name} overall branch ${pct(m.branch)}% dropped below BASE_SHA ${pct(f.branch)}%.`);
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
      const f = overallFloor[name];
      if (!meetsFloor(round1(c.line), round1(f.line)) || !meetsFloor(round1(c.branch), round1(f.branch))) {
        failed = true;
        say(`- **FAIL** committed baseline ${name} is below the BASE_SHA floor.`);
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

    if (override) {
      const measuredBaseline = {
        dotnet: { line: round1(dotnet.line), branch: round1(dotnet.branch) },
        web: { line: round1(web.line), branch: round1(web.branch) },
      };
      if (!baselineEq(override.to, committed)) {
        failed = true;
        say(`- **FAIL** ${OVERRIDE_FILE} \`to\` must equal the committed head baseline.`);
      }
      if (!baselineEq(override.to, measuredBaseline)) {
        failed = true;
        say(
          `- **FAIL** ${OVERRIDE_FILE} \`to\` must equal measured coverage (dotnet ${pct(measuredBaseline.dotnet.line)}/${pct(measuredBaseline.dotnet.branch)}, web ${pct(measuredBaseline.web.line)}/${pct(measuredBaseline.web.branch)}).`,
        );
      }
      if (!baselineEq(override.from, mainBaseline)) {
        failed = true;
        say(`- **FAIL** ${OVERRIDE_FILE} \`from\` must equal the BASE_SHA floor.`);
      }
      say(
        `- **Re-baseline override:** ${override.reason.trim()} ` +
          `(dotnet ${pct(override.from.dotnet.line)}/${pct(override.from.dotnet.branch)} → ${pct(override.to.dotnet.line)}/${pct(override.to.dotnet.branch)}; ` +
          `web ${pct(override.from.web.line)}/${pct(override.from.web.branch)} → ${pct(override.to.web.line)}/${pct(override.to.web.branch)}). ` +
          `A baseline may be lowered ONLY for a documented change in measurement scope, never to absorb a real coverage drop. ` +
          `Requires its own [workflows] PR and sign-off from Code Reviewer, Tech Coordinator and Helms.`,
      );
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

    return { failed, output: body, expected, exitCode: failed ? 1 : 0, bootstrapped: false, mergeBase, baseSha };
  } catch (e) {
    if (e instanceof GateFailure) {
      const text = e.output && e.output.trim() ? e.output : `- **FAIL** ${e.message}\n`;
      if (!e.output) {
        write(text);
        writeErr(`FAIL: ${e.message}\n`);
      }
      const summary = options.summaryPath ?? env("GITHUB_STEP_SUMMARY");
      if (summary) appendFileSync(summary, text.endsWith("\n") ? text : `${text}\n`);
      return { failed: true, output: text, expected: null, exitCode: 1, error: e };
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

function loadJsonFromBase(root, baseDir, ref, path, failHard) {
  if (ref) {
    const fromGit = loadJsonAtRef(root, ref, path, failHard);
    if (fromGit.present) return fromGit;
  }
  if (baseDir) {
    const full = join(baseDir, path);
    if (existsSync(full)) {
      try {
        return { present: true, value: JSON.parse(readFileSync(full, "utf8")) };
      } catch (e) {
        failHard(`cannot parse ${path} in base checkout: ${e.message}`);
      }
    }
  }
  return { present: false, value: null };
}

function filePresent(root, dir, sha, path, failHard) {
  if (sha && fileExistsAtRef(root, sha, path, failHard)) return true;
  if (dir && existsSync(join(dir, path))) return true;
  return false;
}

function resolveDefaultSha(root, defaultBranch, defaultDir, failHard) {
  if (defaultDir) return null;
  const candidates = [`origin/${defaultBranch}`, defaultBranch];
  for (const name of candidates) {
    if (gitOk(root, ["rev-parse", "--verify", `${name}^{commit}`])) {
      return git(root, ["rev-parse", name], failHard).trim();
    }
  }
  failHard(
    `cannot resolve default branch '${defaultBranch}' tip; pass --default-sha or --default-dir.`,
  );
}

function fileChangedVsBase(root, baseSha, path, failHard) {
  const named = git(root, ["diff", "--name-only", baseSha, "--", path], failHard);
  return Boolean(named && named.trim());
}

function readHeadOverride(root, failHard) {
  const full = join(root, OVERRIDE_FILE);
  if (!existsSync(full)) return null;
  let raw;
  try {
    raw = JSON.parse(readFileSync(full, "utf8"));
  } catch (e) {
    failHard(`cannot parse head ${OVERRIDE_FILE}: ${e.message}`);
  }
  try {
    return validateOverride(raw);
  } catch (e) {
    if (e instanceof GateFailure) failHard(e.message);
    throw e;
  }
}

export function describeCoverageScope(root) {
  const lines = [
    "include: `[Desk.*]*`",
    "excludeByAttribute: `GeneratedCodeAttribute`, `ExcludeFromCodeCoverage` (not `CompilerGeneratedAttribute`)",
    "excludeByFile: `**/obj/**`, `**/*.generated.cs`",
  ];
  const full = join(root, TESTCONFIG_FILE);
  if (!existsSync(full)) {
    lines.push(`config: ${TESTCONFIG_FILE} not present; documented defaults above`);
    return lines;
  }
  try {
    const cfg = JSON.parse(readFileSync(full, "utf8"));
    const coverlet = cfg?.platformOptions?.Coverlet ?? {};
    if (coverlet.include) lines[0] = `include: \`${coverlet.include}\``;
    if (coverlet.excludeByAttribute) {
      lines[1] =
        `excludeByAttribute: \`${coverlet.excludeByAttribute}\` (not \`CompilerGeneratedAttribute\`)`;
    }
    if (coverlet.excludeByFile) lines[2] = `excludeByFile: \`${coverlet.excludeByFile}\``;
    lines.push(`config: \`${TESTCONFIG_FILE}\``);
  } catch {
    lines.push(`config: ${TESTCONFIG_FILE} present but unreadable`);
  }
  return lines;
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

function assertThresholdsNotLooser(head, base, who, failHard) {
  const origin = "BASE_SHA";
  if (!(head.diffLineMinPercent >= base.diffLineMinPercent)) {
    failHard(`${who} lowers diffLineMinPercent (${head.diffLineMinPercent} < ${base.diffLineMinPercent} from ${origin}).`);
  }
  if (!(head.diffBranchMinPercent >= base.diffBranchMinPercent)) {
    failHard(`${who} lowers diffBranchMinPercent (${head.diffBranchMinPercent} < ${base.diffBranchMinPercent} from ${origin}).`);
  }
  if (base.overallMustNotDrop && head.overallMustNotDrop !== true) {
    failHard(`${who} turns off overallMustNotDrop (required by ${origin}).`);
  }
  if (!(head.baselineMatchTolerancePercent <= base.baselineMatchTolerancePercent)) {
    failHard(
      `${who} widens baselineMatchTolerancePercent (${head.baselineMatchTolerancePercent} > ${base.baselineMatchTolerancePercent} from ${origin}).`,
    );
  }
}

function assertBaselineNotLowered(head, base, who, failHard) {
  for (const name of ["dotnet", "web"]) {
    const h = head[name];
    const b = base[name];
    if (!meetsFloor(round1(h.line), round1(b.line)) || !meetsFloor(round1(h.branch), round1(b.branch))) {
      failHard(
        `${who} lowers the committed ${name} baseline below BASE_SHA (${pct(h.line)}/${pct(h.branch)} < ${pct(b.line)}/${pct(b.branch)}). The floor may not be lowered except via a documented ${OVERRIDE_FILE} override.`,
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
