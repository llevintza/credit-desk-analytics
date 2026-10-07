import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  BOOTSTRAP_THRESHOLDS,
  GateFailure,
  canonicalCobertura,
  canonicalLcov,
  finitePercent,
  meetsFloor,
  parseArgs,
  runGate,
  summarizeDotnet,
  validateBaseline,
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
}

test("parseArgs reads flags", () => {
  assert.deepEqual(parseArgs(["--base", "abc", "--dotnet", "x"]), { base: "abc", dotnet: "x" });
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
  assert.match(yml, /persist-credentials: false/);
  assert.match(yml, /_base\/perf\/coverage-gate\.mjs/);
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
  return { dir, base };
}

test("bootstrap: base has no gate script, head copy is allowed", () => {
  const { dir, base } = setupPassRepo();
  try {
    const r = runGate({
      root: dir,
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /refusing to bootstrap/);
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
    const uncovered = Array.from({ length: 20 }, (_, i) => `    public int F${i}() => ${i};`).join("\n");
    write(dir, "src/Desk.New/BrandNew.cs", `namespace Desk.New;\npublic class BrandNew {\n${uncovered}\n}\n`);
    headJson(dir, { dotnet: { line: 100, branch: 100 }, web: { line: 100, branch: 100 } });
    commit(dir, "add uncovered file");
    const r = runGate({
      root: dir,
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /refusing to bootstrap/);
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /lowers diffBranchMinPercent/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("committed baseline behind measured fails", () => {
  const { dir, base } = setupPassRepo();
  try {
    write(
      dir,
      "perf/coverage-baseline.json",
      JSON.stringify({
        dotnet: { line: 90, branch: 90 },
        web: { line: 90, branch: 90 },
      }),
    );
    commit(dir, "stale baseline");
    const r = runGate({
      root: dir,
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /behind measured/);
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
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
      argv: ["--dotnet", "cov/dotnet", "--web", "cov/web", "--base", base],
      stdoutWrite: () => {},
      stderrWrite: () => {},
    });
    assert.equal(r.failed, true);
    assert.match(r.output, /cannot canonicalize/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
