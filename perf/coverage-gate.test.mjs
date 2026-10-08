import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  BOOTSTRAP_THRESHOLDS,
  GateFailure,
  canonicalCobertura,
  canonicalLcov,
  describeCoverageScope,
  finitePercent,
  meetsFloor,
  parseArgs,
  parseSuites,
  runGate,
  summarizeDotnet,
  validateBaseline,
  validateOverride,
  validateThresholds,
} from "./coverage-gate.mjs";

const repoRoot = dirname(fileURLToPath(new URL(".", import.meta.url)));
const THIS_GATE = readFileSync(new URL("./coverage-gate.mjs", import.meta.url), "utf8");

const VALID_THRESHOLDS = {
  diffLineMinPercent: 80,
  diffBranchMinPercent: 80,
  overallMustNotDrop: true,
  baselineMatchTolerancePercent: 0.5,
};

const VALID_BASELINE = {
  dotnet: { line: 90, branch: 90 },
  web: { line: 80, branch: 100 },
};

function git(cwd, args) {
  return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
}

function initRepo() {
  const dir = mkdtempSync(join(tmpdir(), "gate-"));
  git(dir, ["init", "-b", "main"]);
  git(dir, ["config", "user.email", "gate@test.invalid"]);
  git(dir, ["config", "user.name", "Gate Test"]);
  git(dir, ["config", "commit.gpgsign", "false"]);
  // `git commit` otherwise forks a detached `git maintenance run --auto`
  // that can still be touching .git/objects when the test removes the repo
  // (ENOTEMPTY in rmdir, #249).
  git(dir, ["config", "maintenance.auto", "false"]);
  git(dir, ["config", "gc.auto", "0"]);
  return dir;
}

// Cleanup only, so a straggling writer can't fail a test (#249). Each attempt
// walks the tree again: rmSync's own maxRetries only retries the rmdir and
// never removes a file that appeared after it read the directory.
function removeRepo(dir) {
  for (let attempt = 1; ; attempt++) {
    try {
      rmSync(dir, { recursive: true, force: true, maxRetries: 2, retryDelay: 50 });
      return;
    } catch (err) {
      if (attempt >= 5 || (err.code !== "ENOTEMPTY" && err.code !== "EBUSY")) throw err;
    }
  }
}

function write(dir, rel, contents) {
  const full = join(dir, rel);
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, contents);
}

function commit(dir, msg) {
  git(dir, ["add", "-A"]);
  git(dir, ["commit", "-m", msg, "--allow-empty"]);
  return git(dir, ["rev-parse", "HEAD"]);
}

function cobertura({ source, pkg, filename, lines }) {
  const lineXml = lines
    .map(([n, hits, cond]) =>
      cond
        ? `<line number="${n}" hits="${hits}" branch="True" condition-coverage="${cond}"/>`
        : `<line number="${n}" hits="${hits}" branch="False"/>`,
    )
    .join("\n");
  return `<?xml version="1.0"?>
<coverage>
  <sources><source>${source}</source></sources>
  <packages>
    <package name="${pkg}">
      <classes>
        <class name="${pkg}.C" filename="${filename}">
          ${lineXml}
        </class>
      </classes>
    </package>
  </packages>
</coverage>
`;
}

function lcov(sf, lines, branches = []) {
  const da = lines.map(([n, h]) => `DA:${n},${h}`).join("\n");
  const br = branches.map(([n, block, branch, taken]) => `BRDA:${n},${block},${branch},${taken}`).join("\n");
  return `TN:\nSF:${sf}\n${da}${br ? `\n${br}` : ""}\nend_of_record\n`;
}

function headJson(dir, measured = { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } }) {
  write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
  write(dir, "perf/coverage-baseline.json", JSON.stringify(measured));
}

function gateArgs(base, extra = []) {
  return [
    "--dotnet",
    "cov/dotnet",
    "--web",
    "cov/web",
    "--base",
    base,
    "--base-dir",
    "_base",
    "--default-dir",
    "_base",
    "--default-sha",
    base,
    "--default-branch",
    "main",
    "--base-ref",
    "main",
    "--event",
    "pull_request",
    ...extra,
  ];
}

function pushArgs(base, extra = []) {
  return [
    "--dotnet",
    "cov/dotnet",
    "--web",
    "cov/web",
    "--base",
    base,
    "--base-dir",
    "_base",
    "--default-dir",
    "_base",
    "--default-sha",
    base,
    "--default-branch",
    "main",
    "--base-ref",
    "",
    "--event",
    "push",
    ...extra,
  ];
}

function assertGatePath(output, path) {
  assert.match(output, new RegExp(`^gate-path: ${path}$`, "m"));
}

function assertBaseSourceLabels(output, baseSha) {
  const sha = baseSha.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  assert.match(output, new RegExp(`Gate script: \`perf/coverage-gate\\.mjs\` from \`_base\` at BASE_SHA \\(\`${sha}\`\\)\\.`));
  assert.match(output, new RegExp(`Thresholds: \`perf/coverage-thresholds\\.json\` from \`_base\` at BASE_SHA \\(\`${sha}\`\\)\\.`));
  assert.match(output, new RegExp(`Floor: \`perf/coverage-baseline\\.json\` from \`_base\` at BASE_SHA \\(\`${sha}\`\\)\\.`));
}

function assertMissingBaseGate(r) {
  assert.equal(r.failed, true);
  assertGatePath(r.output, "fail/missing-base-gate");
  assert.match(r.output, /BASE_SHA has no perf\/coverage-gate\.mjs|Bootstrap closed after #5/);
}

test("parseArgs reads flags", () => {
  assert.deepEqual(parseArgs(["--base", "abc", "--dotnet", "x"]), { base: "abc", dotnet: "x" });
});

test("parseArgs treats empty --base-ref as empty string, not true", () => {
  assert.deepEqual(parseArgs(["--base-ref", "", "--event", "push"]), { "base-ref": "", event: "push" });
  assert.equal(parseArgs(["--verbose"]).verbose, "true");
});

test("meetsFloor is NaN-safe (NaN fails closed)", () => {
  assert.equal(meetsFloor(80, 80), true);
  assert.equal(meetsFloor(79.9, 80), false);
  assert.equal(meetsFloor(Number.NaN, 80), false);
  assert.equal(meetsFloor(80, Number.NaN), false);
  assert.equal(meetsFloor(undefined, 0), false);
});

test("finitePercent rejects non-numeric, NaN, out of range", () => {
  assert.equal(finitePercent(50, "x"), 50);
  assert.throws(() => finitePercent("x", "x"), GateFailure);
  assert.throws(() => finitePercent(Number.NaN, "x"), GateFailure);
  assert.throws(() => finitePercent(-1, "x"), GateFailure);
  assert.throws(() => finitePercent(101, "x"), GateFailure);
  assert.throws(() => finitePercent(null, "x"), GateFailure);
});

test("schema: dropped diffLineMinPercent fails", () => {
  const t = { ...VALID_THRESHOLDS };
  delete t.diffLineMinPercent;
  assert.throws(() => validateThresholds(t), /missing \[diffLineMinPercent\]/);
});

test("schema: non-numeric diffLineMinPercent fails", () => {
  assert.throws(() => validateThresholds({ ...VALID_THRESHOLDS, diffLineMinPercent: "x" }), /finite number/);
});

test("schema: unknown key fails", () => {
  assert.throws(() => validateThresholds({ ...VALID_THRESHOLDS, extra: 1 }), /unknown \[extra\]/);
});

test("schema: baseline {dotnet:{}} fails", () => {
  assert.throws(() => validateBaseline({ dotnet: {}, web: { line: 1, branch: 1 } }), /exactly \{line, branch\}/);
});

test("schema: NaN-like null baseline line fails", () => {
  assert.throws(
    () => validateBaseline({ dotnet: { line: null, branch: 0 }, web: { line: 0, branch: 0 } }),
    /finite number/,
  );
});

test("schema: valid objects pass", () => {
  validateThresholds(VALID_THRESHOLDS);
  validateBaseline(VALID_BASELINE);
  validateThresholds(BOOTSTRAP_THRESHOLDS);
  validateOverride({
    from: VALID_BASELINE,
    to: VALID_BASELINE,
    reason: "documented re-baseline",
  });
});

test("schema: extra or unknown baseline key fails", () => {
  assert.throws(
    () => validateBaseline({ ...VALID_BASELINE, extra: 1 }),
    /exactly \{dotnet, web\}/,
  );
});

test("schema: override missing reason fails", () => {
  assert.throws(
    () => validateOverride({ from: VALID_BASELINE, to: VALID_BASELINE, reason: "" }),
    /non-empty string/,
  );
});

test("schema: null, array, and non-boolean overallMustNotDrop fail", () => {
  assert.throws(() => validateThresholds(null), /JSON object/);
  assert.throws(() => validateThresholds([]), /JSON object/);
  assert.throws(() => validateThresholds({ ...VALID_THRESHOLDS, overallMustNotDrop: "yes" }), /boolean/);
  assert.throws(() => validateBaseline(null), /JSON object/);
  assert.throws(() => validateBaseline({ web: { line: 1, branch: 1 } }), /exactly \{dotnet, web\}/);
  assert.throws(
    () => validateBaseline({ dotnet: null, web: { line: 1, branch: 1 } }),
    /must be an object with line and branch/,
  );
});

test("path keys: Api vs Seeder Program.cs do not collide", () => {
  const unknown = [];
  const api = canonicalCobertura("Desk.Api/Program.cs", ["/repo/src/"], "Desk.Api", unknown);
  const seeder = canonicalCobertura("Desk.Seeder/Program.cs", ["/repo/src/"], "Desk.Seeder", unknown);
  assert.equal(api, "src/Desk.Api/Program.cs");
  assert.equal(seeder, "src/Desk.Seeder/Program.cs");
  assert.notEqual(api, seeder);
  assert.equal(unknown.length, 0);
  const data = canonicalCobertura("ConnectionStrings.cs", ["/repo/src/Desk.Data/"], "Desk.Data", unknown);
  assert.equal(data, "src/Desk.Data/ConnectionStrings.cs");
});

test("path keys: lcov SF maps under web/src", () => {
  const unknown = [];
  assert.equal(canonicalLcov("src/app/app.ts", unknown), "web/src/app/app.ts");
  assert.equal(canonicalLcov("/x/web/src/main.ts", unknown), "web/src/main.ts");
  assert.equal(unknown.length, 0);
  assert.equal(canonicalLcov("mystery.ts", unknown), null);
});

test("ci.yml evaluates coverage from a base checkout directory", () => {
  const yml = readFileSync(join(repoRoot, ".github/workflows/ci.yml"), "utf8");
  assert.match(yml, /path: _base/);
  assert.doesNotMatch(yml, /path: _default/);
  assert.match(yml, /persist-credentials: false/);
  assert.match(yml, /^\s+GATE=_base\/perf\/coverage-gate\.mjs$/m);
  assert.doesNotMatch(yml, /GATE=perf\/coverage-gate\.mjs/);
  assert.doesNotMatch(yml, /if \[ -f _base\/perf\/coverage-gate\.mjs \]/);
  assert.doesNotMatch(yml, /elif \[ ! -f perf\/coverage-gate\.mjs \]/);
  assert.match(yml, /Never run the head copy/);
  assert.match(yml, /Bootstrap closed after #5/);
  assert.match(yml, /node "\$GATE"/);
  assert.match(yml, /--base-dir _base/);
  assert.match(yml, /--default-dir _base/);
  assert.ok(yml.includes('--default-sha "$BASE_SHA"'));
  assert.equal((yml.match(/echo "Bootstrap:/g) || []).length, 0);
  assert.doesNotMatch(yml, /coverage-bootstrap/);
  assert.match(yml, /path: _base[\s\S]*fetch-depth: 0/);
  assert.equal(existsSync(join(repoRoot, "perf/coverage-bootstrap.json")), false);
  assert.equal(existsSync(join(repoRoot, "perf/coverage-override.json")), false);
  assert.match(yml, /types: \[opened, synchronize, reopened, edited\]/);
  assert.match(yml, /github\.ref != 'refs\/heads\/main'/);
  assert.ok(yml.includes("GITHUB_EVENT_NAME: ${{ github.event_name }}"));
  assert.ok(yml.includes('--event "${GITHUB_EVENT_NAME:-}"'));
  assert.match(yml, /permissions:\s*\n\s*contents: read/);
  assert.doesNotMatch(yml, /pull_request_target/);
  const owners = readFileSync(join(repoRoot, ".github/CODEOWNERS"), "utf8");
  assert.match(owners, /\/tests\/testconfig\.json/);
  assert.match(owners, /\/\.gitleaks\.toml/);
  const agents = readFileSync(join(repoRoot, "AGENTS.md"), "utf8");
  assert.match(agents, /tests\/testconfig\.json/);
  assert.match(agents, /\.gitleaks\.toml/);
});

function setupPassRepo() {
  const dir = initRepo();
  write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
  write(
    dir,
    "cov/dotnet/a.cobertura.xml",
    cobertura({
      source: join(dir, "src"),
      pkg: "Desk.Api",
      filename: "Desk.Api/Hello.cs",
      lines: [
        [1, 1],
        [2, 1, "100% (2/2)"],
      ],
    }),
  );
  write(
    dir,
    "cov/web/lcov.info",
    lcov("src/app/app.ts", [[1, 1]], [
      [1, 0, 0, 1],
      [1, 0, 1, 1],
    ]),
  );
  write(dir, "web/src/app/app.ts", "export const x = 1;\n");
  headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
  write(dir, "perf/coverage-gate.mjs", THIS_GATE);
  write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
  const base = commit(dir, "base");
  return { dir, base };
}

test("BASE_SHA without the gate fails closed (no head fallback)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const base = commit(dir, "pre-gate");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("once base has the script, missing thresholds FAIL (no silent bootstrap)", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "base with script only");
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "cov/dotnet/a.cobertura.xml", "<coverage></coverage>");
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    commit(dir, "head adds json");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /missing .*thresholds|missing .*baseline|failing closed|refusing to bootstrap/);
  } finally {
    removeRepo(dir);
  }
});

test("head always-pass script is ignored: evaluation uses imported (base) logic", () => {
  const { dir, base } = setupPassRepo();
  try {
    const uncovered = Array.from({ length: 20 }, (_, i) => `    public int F${i}() => ${i};`).join("\n");
    write(dir, "src/Desk.New/BrandNew.cs", `namespace Desk.New;\npublic class BrandNew {\n${uncovered}\n}\n`);
    write(dir, "perf/coverage-gate.mjs", "console.log('hacked'); process.exit(0);\n");
    commit(dir, "head hacks gate and adds uncovered file");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /0%/);
  } finally {
    removeRepo(dir);
  }
});

test("uncovered changed src file counts as 0% and can fail the 80% diff gate", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Old.cs", "class Old {}\n");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Old.cs",
        lines: [[1, 1]],
      }),
    );
    write(
      dir,
      "cov/web/lcov.info",
      lcov("src/app/app.ts", [[1, 1]], [
        [1, 0, 0, 1],
        [1, 0, 1, 1],
      ]),
    );
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    const base = commit(dir, "base");
    const uncovered = Array.from({ length: 20 }, (_, i) => `    public int F${i}() => ${i};`).join("\n");
    write(dir, "src/Desk.New/BrandNew.cs", `namespace Desk.New;\npublic class BrandNew {\n${uncovered}\n}\n`);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "add uncovered file");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /counted as \*\*0%\*\*/);
    assert.match(r.output, /diff line/);
  } finally {
    removeRepo(dir);
  }
});

test("overall drop vs a base with the script fails", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 99, branch: 99 },
      web: { line: 99, branch: 99 },
    }));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base with high floor");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 50, branch: 0 },
      web: { line: 0, branch: 0 },
    }));
    commit(dir, "head drops coverage");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below BASE_SHA|behind measured|lowers the committed/);
  } finally {
    removeRepo(dir);
  }
});

test("overall line/branch drop vs BASE floor is reported when committed floor is not lowered", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 99, branch: 99 },
      web: { line: 99, branch: 99 },
    }));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base with high floor");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /overall line .* dropped below BASE_SHA/);
  } finally {
    removeRepo(dir);
  }
});

test("once base has the script, missing baseline FAIL (no silent bootstrap)", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "base with script and thresholds only");
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "cov/dotnet/a.cobertura.xml", "<coverage></coverage>");
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    commit(dir, "head adds baseline");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /missing .*thresholds|missing .*baseline|failing closed|refusing to bootstrap/);
  } finally {
    removeRepo(dir);
  }
});

test("PR that turns off overallMustNotDrop or lowers a min fails", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-thresholds.json", JSON.stringify({ ...VALID_THRESHOLDS, overallMustNotDrop: false }));
    commit(dir, "turn off no-drop");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /turns off overallMustNotDrop/);
  } finally {
    removeRepo(dir);
  }
});

test("PR that lowers diffLineMinPercent vs bootstrap defaults fails", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-thresholds.json", JSON.stringify({ ...VALID_THRESHOLDS, diffLineMinPercent: 50 }));
    commit(dir, "lower min");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /lowers diffLineMinPercent/);
  } finally {
    removeRepo(dir);
  }
});

test("PR that lowers diffBranchMinPercent vs bootstrap defaults fails", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-thresholds.json", JSON.stringify({ ...VALID_THRESHOLDS, diffBranchMinPercent: 50 }));
    commit(dir, "lower branch min");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /lowers diffBranchMinPercent/);
  } finally {
    removeRepo(dir);
  }
});

test("committed baseline behind measured fails once BASE has the script", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 90, branch: 90 }, web: { line: 90, branch: 90 } });
    const base = commit(dir, "base with floor 90");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 1, "100% (2/2)"],
        ],
      }),
    );
    write(
      dir,
      "cov/web/lcov.info",
      lcov("src/app/app.ts", [[1, 1]], [
        [1, 0, 0, 1],
        [1, 0, 1, 1],
      ]),
    );
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /behind measured/);
  } finally {
    removeRepo(dir);
  }
});

test("committed baseline above measured fails once BASE has the gate", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /above measured|dropped below BASE_SHA/);
  } finally {
    removeRepo(dir);
  }
});

test("head-side baseline lowering vs BASE_SHA fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "perf/coverage-baseline.json",
      JSON.stringify({
        dotnet: { line: 50, branch: 50 },
        web: { line: 50, branch: 50 },
      }),
    );
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below the BASE_SHA floor|lowers the committed baseline/);
  } finally {
    removeRepo(dir);
  }
});

test("once BASE has a floor, lowering the head JSON to match dropped coverage still fails", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 99, branch: 99 },
      web: { line: 99, branch: 99 },
    }));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base floor 99");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 50, branch: 0 },
      web: { line: 0, branch: 0 },
    }));
    commit(dir, "head drops coverage and lowers JSON to match");
    const r = runGate({
      root: dir,
      argv: gateArgs(base, ["--base-dir", dir]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below the BASE_SHA floor|dropped below BASE_SHA|lowers the committed/);
  } finally {
    removeRepo(dir);
  }
});

test("poisoned --base-dir JSON cannot replace the BASE_SHA floor", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 99, branch: 99 },
      web: { line: 99, branch: 99 },
    }));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base floor 99");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 50, branch: 0 },
      web: { line: 0, branch: 0 },
    }));
    commit(dir, "head drops coverage and lowers JSON");
    write(dir, "_base/perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "_base/perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "_base/perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 0, branch: 0 },
      web: { line: 0, branch: 0 },
    }));
    const r = runGate({
      root: dir,
      argv: gateArgs(base, ["--base-dir", "_base"]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below the BASE_SHA floor|below BASE_SHA \(50\.0\/0\.0 < 99\.0\/99\.0\)/);
  } finally {
    removeRepo(dir);
  }
});

test("empty --base-dir cannot force bootstrap once BASE_SHA has the script", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 99, branch: 99 },
      web: { line: 99, branch: 99 },
    }));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base floor 99");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 50, branch: 0 },
      web: { line: 0, branch: 0 },
    }));
    commit(dir, "head drops coverage and lowers JSON");
    mkdirSync(join(dir, "_empty"), { recursive: true });
    const r = runGate({
      root: dir,
      argv: gateArgs(base, ["--base-dir", "_empty"]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below the BASE_SHA floor|below BASE_SHA \(50\.0\/0\.0 < 99\.0\/99\.0\)/);
  } finally {
    removeRepo(dir);
  }
});

test("nested lcov.info is discovered", () => {
  const { dir, base } = setupPassRepo();
  try {
    const nested = join(dir, "cov/web/nested/lcov.info");
    mkdirSync(dirname(nested), { recursive: true });
    writeFileSync(nested, readFileSync(join(dir, "cov/web/lcov.info")));
    rmSync(join(dir, "cov/web/lcov.info"));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false);
  } finally {
    removeRepo(dir);
  }
});

test("unparseable head JSON fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-thresholds.json", "{not-json");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot parse head/);
  } finally {
    removeRepo(dir);
  }
});

test("unparseable JSON at BASE_SHA fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", "{not-json");
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "bad json at base");
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "cov/dotnet/a.cobertura.xml", "<coverage></coverage>");
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    commit(dir, "head fixes json");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot parse/);
  } finally {
    removeRepo(dir);
  }
});

test("cobertura Desk.X/filename and packageName fallbacks", () => {
  const unknown = [];
  assert.equal(canonicalCobertura("Desk.Api/Program.cs", [], "Desk.Api", unknown), "src/Desk.Api/Program.cs");
  assert.equal(canonicalCobertura("Hello.cs", [], "Desk.Api", unknown), "src/Desk.Api/Hello.cs");
  assert.equal(
    canonicalCobertura("Hello.cs", ["/repo/src/Desk.Api"], "Desk.Api", unknown),
    "src/Desk.Api/Hello.cs",
  );
  assert.equal(unknown.length, 0);
  const empty = summarizeDotnet("/no/such/coverage-dir", []);
  assert.equal(empty.line, 0);
  assert.equal(empty.branch, 0);
});

test("direct run of the gate script exits non-zero without a real BASE_SHA", () => {
  try {
    execFileSync(process.execPath, [join(repoRoot, "perf/coverage-gate.mjs"), "--base", "0000000000000000000000000000000000000000"], {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    assert.fail("expected non-zero exit");
  } catch (e) {
    assert.equal(e.status, 1);
    assert.match(String(e.stdout || "") + String(e.stderr || ""), /BASE_SHA is missing/);
  }
});

test("diff branch below 80% fails", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    const base = commit(dir, "base");
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() { if (true) {} } }\nvoid Extra() {}\n");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1, "0% (0/4)"],
          [2, 1],
        ],
      }),
    );
    write(
      dir,
      "cov/web/lcov.info",
      lcov("src/app/app.ts", [[1, 1]], [
        [1, 0, 0, 1],
        [1, 0, 1, 1],
      ]),
    );
    commit(dir, "add poorly covered branch");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /diff branch/);
  } finally {
    removeRepo(dir);
  }
});

test("missing BASE_SHA fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "README.md", "x\n");
    commit(dir, "init");
    const r = runGate({
      root: dir,
      argv: ["--base", "0000000000000000000000000000000000000000"],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /BASE_SHA is missing/);
  } finally {
    removeRepo(dir);
  }
});

test("unresolvable base commit fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "README.md", "x\n");
    commit(dir, "init");
    const r = runGate({
      root: dir,
      argv: ["--base", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot resolve base commit/);
  } finally {
    removeRepo(dir);
  }
});

test("widening tolerance vs bootstrap defaults fails", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-thresholds.json", JSON.stringify({ ...VALID_THRESHOLDS, baselineMatchTolerancePercent: 1 }));
    commit(dir, "widen tol");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /widens baselineMatchTolerancePercent/);
  } finally {
    removeRepo(dir);
  }
});

test("unknown cobertura filename fails loudly", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({ source: "/tmp", pkg: "Other", filename: "mystery.cs", lines: [[1, 1]] }),
    );
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot canonicalize/);
  } finally {
    removeRepo(dir);
  }
});

test("after bootstrap, deleting the gate script on head fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "base with gate");
    rmSync(join(dir, "perf/coverage-gate.mjs"));
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [[1, 1]],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assertGatePath(r.output, "fail/missing-head-gate");
    assert.match(r.output, /head is missing perf\/coverage-gate\.mjs/);
  } finally {
    removeRepo(dir);
  }
});

test("after bootstrap, renaming the gate script on head fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "base with gate");
    write(dir, "perf/coverage-gate.renamed.mjs", THIS_GATE);
    rmSync(join(dir, "perf/coverage-gate.mjs"));
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [[1, 1]],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assertGatePath(r.output, "fail/missing-head-gate");
    assert.match(r.output, /head is missing perf\/coverage-gate\.mjs/);
  } finally {
    removeRepo(dir);
  }
});

test("non-default PR base fails closed (retarget bypass)", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runGate({
      root: dir,
      argv: gateArgs(base, ["--base-ref", "old-main"]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assertGatePath(r.output, "fail/retarget");
    assert.match(r.output, /not the default branch 'main'/);
  } finally {
    removeRepo(dir);
  }
});

test("empty head baseline file fails closed and logs FAIL", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-baseline.json", "");
    let stdout = "";
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: (s) => {
        stdout += s;
      },
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(stdout, /\*\*FAIL\*\* cannot parse head/);
    assert.match(r.output, /cannot parse head/);
  } finally {
    removeRepo(dir);
  }
});

test("unknown baseline key on head fails closed and logs FAIL", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "perf/coverage-baseline.json",
      JSON.stringify({ ...VALID_BASELINE, extra: 1 }),
    );
    let stdout = "";
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: (s) => {
        stdout += s;
      },
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(stdout, /\*\*FAIL\*\*.*exactly \{dotnet, web\}/);
    assert.match(r.output, /exactly \{dotnet, web\}/);
  } finally {
    removeRepo(dir);
  }
});

test("CLI output includes coverage table, scope, and measured JSON", () => {
  const { dir, base } = setupPassRepo();
  try {
    let stdout = "";
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: (s) => {
        stdout += s;
      },
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false);
    assert.match(r.output, /## Coverage/);
    assert.match(r.output, /## Coverage scope/);
    assert.match(r.output, /\| Project \| Overall line \| Overall branch \| Diff line \| Diff branch \|/);
    assert.match(r.output, /Base SHA:/);
    assert.match(stdout, /Measured baseline JSON/);
    assert.match(stdout, /"dotnet"/);
    assert.ok(describeCoverageScope(dir).length >= 3);
  } finally {
    removeRepo(dir);
  }
});

test("valid scope-change override may lower the floor to measured; a real drop still needs {from,to,reason}", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    const from = { dotnet: { line: 99, branch: 99 }, web: { line: 99, branch: 99 } };
    const to = { dotnet: { line: 50, branch: 0 }, web: { line: 0, branch: 0 } };
    write(dir, "perf/coverage-baseline.json", JSON.stringify(from));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base floor 99");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(to));
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from,
        to,
        reason: "Documented change in measurement scope (fixture). Not a real coverage drop.",
      }),
    );
    commit(dir, "scope-change rebaseline");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false);
    assert.match(r.output, /Re-baseline override/);
    assert.match(r.output, /ONLY for a documented change in measurement scope/);
  } finally {
    removeRepo(dir);
  }
});

test("override with from not equal to the BASE_SHA floor fails", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    const from = { dotnet: { line: 99, branch: 99 }, web: { line: 99, branch: 99 } };
    const to = { dotnet: { line: 50, branch: 0 }, web: { line: 0, branch: 0 } };
    write(dir, "perf/coverage-baseline.json", JSON.stringify(from));
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    const base = commit(dir, "base floor 99");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(to));
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from: { dotnet: { line: 80, branch: 80 }, web: { line: 80, branch: 80 } },
        to,
        reason: "wrong from",
      }),
    );
    commit(dir, "bad override");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /ONLY for a documented change in measurement scope|from\/to must match/);
  } finally {
    removeRepo(dir);
  }
});

test("R5-F1 (c) / c-hack: restoring a modified gate after a delete fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "main has the gate");
    rmSync(join(dir, "perf/coverage-gate.mjs"));
    const deleted = commit(dir, "delete gate");
    write(dir, "perf/coverage-gate.mjs", "console.log('hacked'); process.exit(0);\n");
    commit(dir, "restore modified gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(deleted),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
    assert.equal(existsSync(join(dir, "_base/perf/coverage-gate.mjs")), false);
  } finally {
    removeRepo(dir);
  }
});

test("R5-F1 (d') / d': re-introducing the gate after a rewind fails closed (no tag)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const pre = commit(dir, "pre-gate");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "had the gate");
    git(dir, ["reset", "--hard", pre]);
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "re-introduce gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(pre),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

function writePassCoverage(dir) {
  write(
    dir,
    "cov/dotnet/a.cobertura.xml",
    cobertura({
      source: join(dir, "src"),
      pkg: "Desk.Api",
      filename: "Desk.Api/Hello.cs",
      lines: [
        [1, 1],
        [2, 1, "100% (2/2)"],
      ],
    }),
  );
  write(
    dir,
    "cov/web/lcov.info",
    lcov("src/app/app.ts", [[1, 1]], [
      [1, 0, 0, 1],
      [1, 0, 1, 1],
    ]),
  );
}

function writeStaleOverride(dir, to) {
  write(
    dir,
    "perf/coverage-override.json",
    JSON.stringify({
      from: { dotnet: { line: 98.3, branch: 97.1 }, web: { line: 81.8, branch: 100 } },
      to,
      reason: "Documented change in measurement scope (fixture leftover). Not a real coverage drop.",
    }),
  );
}

test("first push that introduces the gate fails closed (bootstrap closed after #5)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const before = commit(dir, "pre-gate main");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    writeStaleOverride(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "merge introducing gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("docs-only push after merge uses the BASE_SHA floor and ignores leftover override", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    writeStaleOverride(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const merged = commit(dir, "main already has the gate");
    write(dir, "README.md", "# docs only\n");
    commit(dir, "docs follow-up");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(merged),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false, r.output);
    assert.equal(r.bootstrapped, false);
    assert.doesNotMatch(r.output, /`from` must equal the BASE_SHA floor/);
    assertGatePath(r.output, "push/base");
    assertBaseSourceLabels(r.output, merged);
  } finally {
    removeRepo(dir);
  }
});

test("docs-only PR after merge ignores leftover override whose from no longer matches the floor", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    writeStaleOverride(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const main = commit(dir, "main after #5");
    write(dir, "docs/note.md", "docs only\n");
    commit(dir, "docs-only PR");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(main),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false, r.output);
    assert.doesNotMatch(r.output, /Re-baseline override/);
    assert.doesNotMatch(r.output, /`from` must equal the BASE_SHA floor/);
    assertGatePath(r.output, "pr/base");
    assertBaseSourceLabels(r.output, main);
  } finally {
    removeRepo(dir);
  }
});

test("PR cannot use the introducing-gate push path (R3-M2 models CI)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const before = commit(dir, "pre-gate");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "head has the gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("R5-M1 (a): override PR push passes when from equals the BASE_SHA floor", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    const from = { dotnet: { line: 99, branch: 99 }, web: { line: 99, branch: 99 } };
    const to = { dotnet: { line: 50, branch: 0 }, web: { line: 0, branch: 0 } };
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(from));
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "main floor 99");
    write(dir, "perf/coverage-baseline.json", JSON.stringify(to));
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from,
        to,
        reason: "Documented change in measurement scope (fixture). Not a real coverage drop.",
      }),
    );
    commit(dir, "approved override merge");
    write(
      dir,
      "cov/dotnet/a.cobertura.xml",
      cobertura({
        source: join(dir, "src"),
        pkg: "Desk.Api",
        filename: "Desk.Api/Hello.cs",
        lines: [
          [1, 1],
          [2, 0],
        ],
      }),
    );
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 0], [2, 0]]));
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false, r.output);
    assert.equal(r.bootstrapped, false);
    assert.match(r.output, /Re-baseline override/);
    assert.doesNotMatch(r.output, /`from` must equal the BASE_SHA floor/);
    assertGatePath(r.output, "push/base");
    assertBaseSourceLabels(r.output, before);
  } finally {
    removeRepo(dir);
  }
});

test("R5-M1 (b): direct push that lowers the baseline fails", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "main floor 100");
    write(dir, "perf/coverage-baseline.json", JSON.stringify({
      dotnet: { line: 50, branch: 50 },
      web: { line: 50, branch: 50 },
    }));
    commit(dir, "direct lower");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assertGatePath(r.output, "push/base");
    assert.match(r.output, /lowers the committed baseline|below BASE_SHA|documented change in measurement scope/);
  } finally {
    removeRepo(dir);
  }
});

test("P4: PR retargeted to a branch without the gate, with a hacked gate, fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const oldBase = commit(dir, "side branch never had the gate");
    git(dir, ["branch", "old-base"]);
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "main has the gate");
    write(dir, "perf/coverage-gate.mjs", "console.log('hacked'); process.exit(0);\n");
    commit(dir, "PR head hacks the gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(oldBase),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("P5: stale pre-gate BASE_SHA with a hacked gate fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const stale = commit(dir, "stale pre-gate base.sha");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "later main has the gate");
    write(dir, "perf/coverage-gate.mjs", "console.log('hacked'); process.exit(0);\n");
    commit(dir, "PR head hacks the gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(stale),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("P5'': PR bootstrap at 0/0 with no branch holding gate history fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const base = commit(dir, "only refs are pre-gate");
    assert.equal(git(dir, ["log", "--all", "--", "perf/coverage-gate.mjs"]), "");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    commit(dir, "PR introduces the gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

test("R5-M1 (b): direct push that zeroes the thresholds fails", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "main thresholds 80");
    write(
      dir,
      "perf/coverage-thresholds.json",
      JSON.stringify({
        diffLineMinPercent: 0,
        diffBranchMinPercent: 0,
        overallMustNotDrop: true,
        baselineMatchTolerancePercent: 0.5,
      }),
    );
    commit(dir, "zero thresholds");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /lowers diffLineMinPercent|lowers diffBranchMinPercent/);
  } finally {
    removeRepo(dir);
  }
});

test("override to must equal committed head baseline (say FAIL)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "floor 100");
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from: { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } },
        to: { dotnet: { line: 90, branch: 90 }, web: { line: 90, branch: 90 } },
        reason: "to does not match committed",
      }),
    );
    commit(dir, "bad to");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /`to` must equal the committed head baseline/);
  } finally {
    removeRepo(dir);
  }
});

test("override to must equal measured coverage (say FAIL)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    const floor = { dotnet: { line: 90, branch: 90 }, web: { line: 90, branch: 90 } };
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(floor));
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "floor 90");
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from: floor,
        to: floor,
        reason: "to matches committed but not measured",
      }),
    );
    commit(dir, "override unchanged committed");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /`to` must equal measured coverage/);
  } finally {
    removeRepo(dir);
  }
});

test("override from must equal the BASE_SHA floor when not bootstrapping (say FAIL)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const before = commit(dir, "floor 100");
    write(
      dir,
      "perf/coverage-override.json",
      JSON.stringify({
        from: { dotnet: { line: 90, branch: 90 }, web: { line: 90, branch: 90 } },
        to: { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } },
        reason: "from does not match floor",
      }),
    );
    commit(dir, "bad from");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(before),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /`from` must equal the BASE_SHA floor/);
  } finally {
    removeRepo(dir);
  }
});

test("push with empty --base-ref does not treat PR base as true", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const merged = commit(dir, "main has gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: pushArgs(merged, ["--default-sha", merged]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, false, r.output);
    assert.doesNotMatch(r.output, /PR base 'true'/);
    assertGatePath(r.output, "push/base");
    assertBaseSourceLabels(r.output, merged);
  } finally {
    removeRepo(dir);
  }
});

test("F2: committed baseline below the BASE_SHA floor is reported", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "perf/coverage-baseline.json",
      JSON.stringify({
        dotnet: { line: 10, branch: 10 },
        web: { line: 10, branch: 10 },
      }),
    );
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /below the BASE_SHA floor/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: cannot resolve default-branch tip fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runGate({
      root: dir,
      argv: [
        "--dotnet",
        "cov/dotnet",
        "--web",
        "cov/web",
        "--base",
        base,
        "--default-branch",
        "no-such-branch",
        "--base-ref",
        "",
        "--event",
        "push",
      ],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot resolve default branch 'no-such-branch'/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: unrelated BASE_SHA has no merge-base and fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    git(dir, ["checkout", "--orphan", "other"]);
    write(dir, "orphan.txt", "unrelated\n");
    const other = commit(dir, "unrelated history");
    git(dir, ["checkout", "main"]);
    const r = runGate({
      root: dir,
      argv: gateArgs(other),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot resolve merge-base|git merge-base/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: unparseable head override fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-override.json", "{not-json");
    commit(dir, "bad override");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot parse head perf\/coverage-override\.json/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: invalid override schema fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "perf/coverage-override.json", JSON.stringify({ reason: "no from/to" }));
    commit(dir, "invalid override");
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /exactly \{from, reason, to\}/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: unreadable testconfig is reported in coverage scope", () => {
  const dir = initRepo();
  try {
    write(dir, "tests/testconfig.json", "{not-json");
    const lines = describeCoverageScope(dir);
    assert.match(lines.join("\n"), /unreadable/);
    const missing = describeCoverageScope(join(dir, "no-such-root"));
    assert.match(missing.join("\n"), /not present/);
  } finally {
    removeRepo(dir);
  }
});

test("F2: poisoned base-dir JSON that git does not have fails closed", () => {
  const dir = initRepo();
  try {
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "src/Desk.Api/Hello.cs", "class Hello {}\n");
    const base = commit(dir, "base has gate only");
    write(dir, "perf/coverage-thresholds.json", JSON.stringify(VALID_THRESHOLDS));
    write(dir, "perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    write(dir, "cov/dotnet/a.cobertura.xml", "<coverage></coverage>");
    write(dir, "cov/web/lcov.info", lcov("src/app/app.ts", [[1, 1]]));
    commit(dir, "head adds json");
    write(dir, "_base/perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "_base/perf/coverage-thresholds.json", "{not-json");
    write(dir, "_base/perf/coverage-baseline.json", JSON.stringify(VALID_BASELINE));
    const r = runGate({
      root: dir,
      argv: gateArgs(base, ["--base-dir", "_base"]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot parse .* in base checkout|missing .*thresholds/);
  } finally {
    removeRepo(dir);
  }
});

test("hacked HEAD gate would pass if CI fell back; missing _base does not", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const base = commit(dir, "pre-gate");
    write(dir, "perf/coverage-gate.mjs", "console.log('hacked'); process.exit(0);\n");
    writePassCoverage(dir);
    assert.equal(existsSync(join(dir, "_base/perf/coverage-gate.mjs")), false);
    execFileSync(process.execPath, [join(dir, "perf/coverage-gate.mjs")], {
      cwd: dir,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assertMissingBaseGate(r);
  } finally {
    removeRepo(dir);
  }
});

// #169: --suites names the suites that ran; a skipped suite is skipped, not passed on stale data.
function runQuiet(dir, argv) {
  let stdout = "";
  const r = runGate({ root: dir, argv, stdoutWrite: (t) => (stdout += t), stderrWrite: () => {} });
  return { ...r, stdout };
}

test("parseSuites: default both, ordered subset, rejects empty/unknown/duplicates", () => {
  assert.deepEqual(parseSuites(undefined), ["dotnet", "web"]);
  assert.deepEqual(parseSuites("web"), ["web"]);
  assert.deepEqual(parseSuites(" web , dotnet "), ["dotnet", "web"]);
  for (const bad of ["", ",", "true", "java", "web,web", "dotnet,java"]) {
    assert.throws(() => parseSuites(bad), GateFailure, bad);
  }
});

test("--suites with an unknown suite fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runQuiet(dir, gateArgs(base, ["--suites", "dotnet,java"]));
    assert.equal(r.failed, true);
    assertGatePath(r.output, "fail/suites");
    assert.match(r.output, /--suites must be/);
  } finally {
    removeRepo(dir);
  }
});

test("--suites dotnet,web matches the default (both evaluated)", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runQuiet(dir, gateArgs(base, ["--suites", "dotnet,web"]));
    assert.equal(r.failed, false, r.output);
    assert.doesNotMatch(r.output, /skipped/i);
  } finally {
    removeRepo(dir);
  }
});

test("web-only run skips the dotnet gates instead of failing on missing data", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "web/src/app/app.ts", "export const x = 2;\n");
    commit(dir, "web change");
    rmSync(join(dir, "cov/dotnet"), { recursive: true, force: true });

    // Without --suites the missing dotnet data still counts as 0% and fails (unchanged behaviour).
    const legacy = runQuiet(dir, gateArgs(base));
    assert.equal(legacy.failed, true);
    assert.match(legacy.output, /dotnet overall line 0\.0% dropped/);

    const r = runQuiet(dir, gateArgs(base, ["--suites", "web"]));
    assert.equal(r.failed, false, r.output);
    assert.match(r.output, /\| dotnet \| skipped \| skipped \| skipped \| skipped \| 100\.0% \| 100\.0% \|/);
    assert.match(r.output, /\*\*Skipped:\*\* the dotnet suite did not run/);
    assert.match(r.output, /\| web \| 100\.0% \| 100\.0% \|/);
    assert.deepEqual(r.expected.dotnet, { line: 100, branch: 100 });
    assert.match(r.stdout, /Measured baseline JSON/);
  } finally {
    removeRepo(dir);
  }
});

test("a skipped suite whose sources changed fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() { } }\n");
    commit(dir, "api change");
    const r = runQuiet(dir, gateArgs(base, ["--suites", "web"]));
    assert.equal(r.failed, true);
    assert.match(r.output, /the dotnet suite did not run, but its sources changed: `src\/Desk\.Api\/Hello\.cs`/);
  } finally {
    removeRepo(dir);
  }
});

test("a suite that ran with no coverage data fails closed", () => {
  const { dir, base } = setupPassRepo();
  try {
    rmSync(join(dir, "cov/web"), { recursive: true, force: true });
    const r = runQuiet(dir, gateArgs(base, ["--suites", "web"]));
    assert.equal(r.failed, true);
    assert.match(r.output, /the web suite ran but has no coverage data/);
  } finally {
    removeRepo(dir);
  }
});

test("a skipped suite's committed baseline still may not drop below the floor", () => {
  const { dir, base } = setupPassRepo();
  try {
    headJson(dir, { dotnet: { line: 50, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "lower dotnet baseline");
    const r = runQuiet(dir, gateArgs(base, ["--suites", "web"]));
    assert.equal(r.failed, true);
    assert.match(r.output, /lowers the committed baseline/);
  } finally {
    removeRepo(dir);
  }
});

test("a skipped suite's committed baseline may not move without measurement (R173-10)", () => {
  const { dir } = setupPassRepo();
  try {
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 90, branch: 90 } });
    const floorBase = commit(dir, "floor web 90/90");
    rmSync(join(dir, "cov/web"), { recursive: true, force: true });

    const same = runQuiet(dir, gateArgs(floorBase, ["--suites", "dotnet"]));
    assert.equal(same.failed, false, same.output);

    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 95, branch: 90 } });
    commit(dir, "raise web baseline without running web");
    const raised = runQuiet(dir, gateArgs(floorBase, ["--suites", "dotnet"]));
    assert.equal(raised.failed, true);
    assert.match(raised.output, /web did not run, but its committed baseline differs from the BASE_SHA floor/);
  } finally {
    removeRepo(dir);
  }
});

test("an override needs every suite measured", () => {
  const { dir, base } = setupPassRepo();
  try {
    writeStaleOverride(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "override");
    const r = runQuiet(dir, gateArgs(base, ["--suites", "web"]));
    assert.equal(r.failed, true);
    assert.match(r.output, /coverage-override\.json changed, but the dotnet suite did not run/);
  } finally {
    removeRepo(dir);
  }
});

test("ci.yml passes --suites from the change flags", () => {
  const yml = readFileSync(join(repoRoot, ".github/workflows/ci.yml"), "utf8");
  assert.ok(yml.includes('--suites "$SUITES"'));
  assert.ok(yml.includes("RUN_API: ${{ needs.changes.outputs.run_api }}"));
  assert.ok(yml.includes("RUN_WEB: ${{ needs.changes.outputs.run_web }}"));
});
