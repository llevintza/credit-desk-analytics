import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  BOOTSTRAP_FILE,
  BOOTSTRAP_THRESHOLDS,
  GateFailure,
  canonicalCobertura,
  canonicalLcov,
  describeCoverageScope,
  finitePercent,
  meetsFloor,
  parseArgs,
  runGate,
  summarizeDotnet,
  validateBaseline,
  validateBootstrap,
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
  return dir;
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
  write(dir, BOOTSTRAP_FILE, JSON.stringify({ allowOnce: true }));
}

function gateArgs(base, extra = []) {
  return [
    "--dotnet",
    "cov/dotnet",
    "--web",
    "cov/web",
    "--base",
    base,
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
  validateBootstrap({ allowOnce: true });
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
  assert.match(yml, /path: _default/);
  assert.match(yml, /persist-credentials: false/);
  assert.ok(yml.includes("ref: ${{ github.sha }}"));
  assert.match(yml, /_base\/perf\/coverage-gate\.mjs/);
  assert.match(yml, /--base-dir _base/);
  assert.match(yml, /--default-dir _base/);
  assert.ok(yml.includes('--default-sha "$BASE_SHA"'));
  assert.match(yml, /deleting or renaming the gate fails closed/);
  assert.equal((yml.match(/echo "Bootstrap:/g) || []).length, 1);
  assert.match(yml, /types: \[opened, synchronize, reopened, edited\]/);
  assert.match(yml, /github\.ref != 'refs\/heads\/main'/);
  assert.ok(yml.includes("GITHUB_EVENT_NAME: ${{ github.event_name }}"));
  assert.ok(yml.includes('--event "${GITHUB_EVENT_NAME:-}"'));
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
  const base = commit(dir, "base");
  // Head must have the gate during bootstrap; default-sha stays at `base` (no gate).
  write(dir, "perf/coverage-gate.mjs", THIS_GATE);
  write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
  return { dir, base };
}

test("bootstrap: base has no gate script, head copy is allowed", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.bootstrapped, true);
    assert.equal(r.failed, false);
    assert.match(r.output, /Bootstrap/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    const base = commit(dir, "base");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
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
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /below main|below main's floor|behind measured|lowers the committed/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /overall line .* dropped below main/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
  }
});

test("bootstrap: committed baseline above measured is a Note, not FAIL", () => {
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
      argv: gateArgs(base, ["--base-dir", "_base"]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.bootstrapped, true);
    assert.equal(r.failed, false);
    assert.match(r.output, /cannot relax overall\/diff/);
    assert.match(r.output, /Floor: \*\*0 \/ 0\*\*/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("bootstrap: head-side baseline lowering does not change overall/diff pass/fail", () => {
  const { dir, base } = setupPassRepo();
  try {
    const run = () =>
      runGate({
        root: dir,
        argv: gateArgs(base),
        stdoutWrite: () => {},
        stderrWrite: () => {},
      });
    const high = run();
    write(
      dir,
      "perf/coverage-baseline.json",
      JSON.stringify({
        dotnet: { line: 50, branch: 50 },
        web: { line: 50, branch: 50 },
      }),
    );
    const low = run();
    assert.equal(high.bootstrapped, true);
    assert.equal(low.bootstrapped, true);
    assert.equal(high.failed, low.failed);
    assert.equal(low.failed, false);
    assert.match(low.output, /cannot relax overall\/diff/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /below the default-branch floor|dropped below main|below main's floor|below BASE_SHA/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /below the default-branch floor|below BASE_SHA \(50\.0\/0\.0 < 99\.0\/99\.0\)/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /below the default-branch floor|below BASE_SHA \(50\.0\/0\.0 < 99\.0\/99\.0\)/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    const base = commit(dir, "base");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
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
    headJson(dir, { dotnet: { line: 100, branch: 0 }, web: { line: 100, branch: 100 } });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /head is missing perf\/coverage-gate\.mjs/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /head is missing perf\/coverage-gate\.mjs/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /not the default branch 'main'/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
  }
});

test("override with from not equal to the default-branch floor fails", () => {
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
    rmSync(dir, { recursive: true, force: true });
  }
});

test("missing bootstrap allowOnce is not treated as script-missing bootstrap", () => {
  const { dir, base } = setupPassRepo();
  try {
    rmSync(join(dir, BOOTSTRAP_FILE));
    const r = runGate({
      root: dir,
      argv: gateArgs(base),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /explicit one-time signal/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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

test("first push that introduces the gate bootstraps (event.before has no gate)", () => {
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
    assert.equal(r.failed, false, r.output);
    assert.equal(r.bootstrapped, true);
    assert.match(r.output, /introducing push|Bootstrap/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.doesNotMatch(r.output, /`from` must equal the default-branch floor/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.doesNotMatch(r.output, /`from` must equal the default-branch floor/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("PR cannot use the introducing-gate push path (R3-M2)", () => {
  const dir = initRepo();
  try {
    write(dir, "src/Desk.Api/Hello.cs", "class Hello { void M() {} }\n");
    write(dir, "web/src/app/app.ts", "export const x = 1;\n");
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    const before = commit(dir, "pre-gate");
    write(dir, "perf/coverage-gate.mjs", THIS_GATE);
    write(dir, "tests/testconfig.json", readFileSync(join(repoRoot, "tests/testconfig.json"), "utf8"));
    const head = commit(dir, "default now has the gate");
    writePassCoverage(dir);
    const r = runGate({
      root: dir,
      argv: gateArgs(before, ["--default-sha", head]),
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /base is missing perf\/coverage-gate\.mjs/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    write(dir, BOOTSTRAP_FILE, JSON.stringify({ allowOnce: true }));
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
    assert.doesNotMatch(r.output, /`from` must equal the default-branch floor/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /lowers the committed baseline|below BASE_SHA|documented change in measurement scope/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    rmSync(dir, { recursive: true, force: true });
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
    write(dir, BOOTSTRAP_FILE, JSON.stringify({ allowOnce: true }));
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
    rmSync(dir, { recursive: true, force: true });
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
    assert.match(r.output, /`from` must equal the default-branch floor/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
