import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, normalize as normalizePath } from "node:path";
import {
  AREAS,
  JOBS,
  areasFor,
  changedPaths,
  classify,
  isDocs,
  isShared,
  main,
  outputs,
  parseArgs,
  render,
} from "./ci-changes.mjs";

const flagsOf = (...on) => Object.fromEntries(AREAS.map((a) => [a, on.includes(a)]));
const ALL = flagsOf(...AREAS);
const jobsOf = (...on) => Object.fromEntries(Object.keys(JOBS).map((j) => [j, on.includes(j)]));
const ALL_JOBS = jobsOf(...Object.keys(JOBS));

// One fixture per area (#169 acceptance criteria), each asserting the exact flag set.
const FIXTURES = [
  {
    name: "api",
    paths: ["src/Desk.Api/Program.cs", "tests/Desk.Api.Tests/PositionsTests.cs"],
    flags: flagsOf("api", "app"),
    jobs: jobsOf("run_api", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "api (UserAdmin CLI)",
    paths: ["src/Desk.UserAdmin/Program.cs"],
    flags: flagsOf("api", "app"),
    jobs: jobsOf("run_api", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "web",
    paths: ["web/src/app/app.ts", "web/package.json"],
    flags: flagsOf("web", "app"),
    jobs: jobsOf("run_web", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "db (seeder)",
    paths: ["src/Desk.Seeder/Program.cs", "tests/Desk.Seeder.Tests/SeedTests.cs"],
    flags: flagsOf("db", "app"),
    jobs: jobsOf("run_api", "run_db_tools", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "db (migration sets api and db)",
    paths: ["src/Desk.Data/Migrations/20261008_AddAudit.cs"],
    flags: flagsOf("api", "db", "app"),
    jobs: jobsOf("run_api", "run_db_tools", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "app (deploy)",
    paths: ["render.yaml", "deploy/AGENTS.txt"],
    flags: flagsOf("app"),
    jobs: jobsOf("run_compose_smoke", "run_e2e", "run_budgets"),
  },
  {
    name: "deploy/start.sh (input of the api suite)",
    paths: ["deploy/start.sh"],
    flags: flagsOf("api", "app"),
    jobs: jobsOf("run_api", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "app (e2e specs)",
    paths: ["e2e/tests/positions.spec.ts"],
    flags: flagsOf("app"),
    jobs: jobsOf("run_compose_smoke", "run_e2e", "run_budgets"),
  },
  {
    name: "perf",
    paths: ["perf/payload-size.mjs", "perf/positions.js"],
    flags: flagsOf("perf"),
    jobs: jobsOf("run_budgets"),
  },
  {
    name: "perf .NET benchmark (in CreditDesk.slnx, built by the api job)",
    paths: ["perf/GridBenchmark/Bench.cs", "perf/LoadBenchmark/LoadBenchmark.csproj", "perf/GridBenchmark/Strings.resx"],
    flags: flagsOf("api", "app", "perf"),
    jobs: jobsOf("run_api", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "mixed (web + perf)",
    paths: ["web/src/app/grid.ts", "perf/payload-size.mjs", "README.md"],
    flags: flagsOf("web", "app", "perf"),
    jobs: jobsOf("run_web", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "mixed (api + web)",
    paths: ["src/Desk.Api/Program.cs", "web/src/app/app.ts"],
    flags: flagsOf("api", "web", "app"),
    jobs: jobsOf("run_api", "run_web", "run_compose_smoke", "run_e2e", "run_budgets", "run_coverage"),
  },
  {
    name: "docs only",
    // #166's shape: AGENTS/CLAUDE files everywhere, including under .github/.
    paths: [
      "README.md",
      "docs/adr/0021-x.md",
      "docs/screenshots/a.png",
      ".claude/skills/adr/SKILL.md",
      "src/Desk.Api/AGENTS.md",
      "web/CLAUDE.md",
      ".github/AGENTS.md",
      ".github/pull_request_template.md",
    ],
    flags: flagsOf(),
    jobs: jobsOf(),
  },
];

for (const f of FIXTURES) {
  test(`fixture: ${f.name}`, () => {
    const r = classify(f.paths);
    assert.deepEqual(r.flags, f.flags);
    assert.deepEqual(r.jobs, f.jobs);
    assert.deepEqual(r.reasons, []);
  });
}

const SHARED = [
  ".github/workflows/ci.yml",
  ".github/scripts/ci-changes.mjs",
  "web/package-lock.json",
  "perf/package-lock.json",
  "src/Desk.Api/packages.lock.json",
  "Directory.Build.props",
  "tests/Directory.Build.props",
  "Directory.Packages.props",
  "global.json",
  "dotnet-tools.json",
  ".config/dotnet-tools.json",
  "CreditDesk.slnx",
  "Other.sln",
  "docker-compose.yml",
  "e2e/docker-compose.e2e.yml",
  "compose.override.yaml",
  "deploy/Dockerfile",
  "deploy/ci-images/gitleaks/Dockerfile",
  ".dockerignore",
  "perf/coverage-gate.mjs",
  "perf/coverage-baseline.json",
  "tests/testconfig.json",
  ".gitleaks.toml",
];

for (const p of SHARED) {
  test(`shared trigger sets every flag: ${p}`, () => {
    assert.equal(isShared(p), true);
    const r = classify(["README.md", p]);
    assert.deepEqual(r.flags, ALL);
    assert.deepEqual(r.jobs, ALL_JOBS);
    assert.deepEqual(r.reasons, [`shared trigger: ${p}`]);
  });
}

test("a path that matches no area sets every flag", () => {
  for (const p of [".gitignore", ".env.example", "src/Desk.NewThing/X.cs", "tests/Desk.New.Tests/T.cs", ".claude/settings.json", "Makefile"]) {
    const r = classify([p]);
    assert.deepEqual(r.flags, ALL, p);
    assert.deepEqual(r.reasons, [`matches no area: ${p}`]);
  }
});

test("an empty diff sets every flag", () => {
  const r = classify([]);
  assert.deepEqual(r.flags, ALL);
  assert.deepEqual(r.reasons, ["empty diff"]);
  assert.deepEqual(classify(["", "  "]).flags, ALL);
});

test("Markdown in a source tree is not docs, except AGENTS.md/CLAUDE.md (R173-09)", () => {
  assert.deepEqual(classify(["web/src/x.md"]).flags, flagsOf("web", "app"));
  assert.deepEqual(classify(["src/Desk.Api/Templates/mail.md"]).flags, flagsOf("api", "app"));
  assert.deepEqual(classify(["src/Desk.Api/AGENTS.md", "web/src/app/CLAUDE.md"]).flags, flagsOf());
  assert.deepEqual(classify(["web/README.md", "e2e/notes.md"]).flags, flagsOf());
});

test("paths are not trimmed: a trailing space is not docs", () => {
  const r = classify(["README.md "]);
  assert.deepEqual(r.flags, ALL);
  assert.deepEqual(r.reasons, ["matches no area: README.md "]);
});

test("paths are normalized", () => {
  assert.deepEqual(classify(["./web/src/a.ts"]).flags, flagsOf("web", "app"));
  assert.deepEqual(classify(["web\\src\\a.ts"]).flags, flagsOf("web", "app"));
});

test("areasFor and isDocs edges", () => {
  assert.deepEqual(areasFor(".github/x.md"), []);
  assert.equal(areasFor(".github/x.yml"), null);
  assert.deepEqual(areasFor("docs/x.png"), []);
  assert.deepEqual(areasFor("render.yaml"), ["app"]);
  assert.equal(areasFor("render.yaml.bak"), null);
  assert.equal(areasFor("webby/x.ts"), null);
  assert.equal(isDocs("src/Desk.Api/notes.mdx"), false);
  assert.equal(isShared("src/Desk.Api/Dockerfile.dev"), true);
  assert.equal(isShared("src/Desk.Api/compose-notes.txt"), false);
  assert.equal(isShared("perf/payload-size.mjs"), false);
});

test("every job maps only to known areas", () => {
  for (const areas of Object.values(JOBS)) for (const a of areas) assert.ok(AREAS.includes(a), a);
});

test("parseArgs", () => {
  assert.deepEqual(parseArgs(["--base", "a", "x", "--flag", "--head", "b"]), { base: "a", flag: "true", head: "b" });
  assert.deepEqual(parseArgs(["--last"]), { last: "true" });
});

test("outputs and render", () => {
  const r = classify(["web/src/a.ts"]);
  const out = outputs(r);
  assert.match(out, /^api=false$/m);
  assert.match(out, /^web=true$/m);
  assert.match(out, /^run_web=true$/m);
  assert.match(out, /^run_api=false$/m);
  assert.ok(out.endsWith("\n"));
  const md = render(r);
  assert.match(md, /\| `web` \| true \|/);
  assert.doesNotMatch(md, /Every flag is set/);
  assert.match(md, /1 changed path\(s\)\./);

  const many = classify(Array.from({ length: 25 }, (_, i) => `misc/${i}.txt`));
  const md2 = render(many);
  assert.match(md2, /Every flag is set/);
  assert.match(md2, /- matches no area: misc\/19\.txt/);
  assert.doesNotMatch(md2, /misc\/20\.txt/);
  assert.match(md2, /… and 5 more/);
});

const SHA_A = "a".repeat(40);
const SHA_B = "b".repeat(40);

test("main writes outputs and summary", () => {
  const writes = [];
  const deps = {
    changedPaths: (root, base, head) => {
      assert.equal(base, SHA_A);
      assert.equal(head, SHA_B);
      return ["perf/positions.js"];
    },
    appendFileSync: (file, text) => writes.push([file, text]),
  };
  const { result, summary } = main(["--base", SHA_A, "--head", SHA_B], { GITHUB_OUTPUT: "out", GITHUB_STEP_SUMMARY: "sum" }, deps);
  assert.deepEqual(result.flags, flagsOf("perf"));
  assert.equal(writes.length, 2);
  assert.equal(writes[0][0], "out");
  assert.match(writes[0][1], /^run_budgets=true$/m);
  assert.equal(writes[1][0], "sum");
  assert.equal(writes[1][1], summary);
});

test("main without GitHub env writes nothing", () => {
  const writes = [];
  main(["--base", SHA_A, "--head", SHA_B], {}, { changedPaths: () => ["web/a.ts"], appendFileSync: (...a) => writes.push(a) });
  assert.equal(writes.length, 0);
});

test("main rejects missing or short SHAs", () => {
  const deps = { changedPaths: () => [], appendFileSync: () => {} };
  assert.throws(() => main([], {}, deps), /full commit SHAs/);
  assert.throws(() => main(["--base", "abc", "--head", SHA_B], {}, deps), /full commit SHAs/);
  assert.throws(() => main(["--base", SHA_A, "--head", "HEAD"], {}, deps), /full commit SHAs/);
});

// Cleanup only, so a straggling writer can't fail a test (#265, as #249/#263 in coverage-gate.test.mjs).
// Each attempt walks the tree again: rmSync's own maxRetries only retries the rmdir and keeps failing
// once a late write lands in a directory it already emptied.
function removeRepo(dir, rm = rmSync) {
  for (let attempt = 1; ; attempt++) {
    try {
      rm(dir, { recursive: true, force: true, maxRetries: 2, retryDelay: 50 });
      return;
    } catch (err) {
      if (attempt >= 5 || (err.code !== "ENOTEMPTY" && err.code !== "EBUSY")) throw err;
    }
  }
}

test("removeRepo re-walks on ENOTEMPTY/EBUSY, at most 5 times, and rethrows anything else", () => {
  const fails = (codes) => {
    let calls = 0;
    const rm = () => {
      const code = codes[calls++];
      if (code) throw Object.assign(new Error(code), { code });
    };
    return { rm, calls: () => calls };
  };
  const twice = fails(["ENOTEMPTY", "EBUSY"]);
  removeRepo("x", twice.rm);
  assert.equal(twice.calls(), 3);
  const always = fails(Array(9).fill("ENOTEMPTY"));
  assert.throws(() => removeRepo("x", always.rm), { code: "ENOTEMPTY" });
  assert.equal(always.calls(), 5);
  const other = fails(["EACCES"]);
  assert.throws(() => removeRepo("x", other.rm), { code: "EACCES" });
  assert.equal(other.calls(), 1);
});

test("changedPaths diffs from the merge-base, lists both sides of a rename, and fails on a bad SHA", () => {
  const dir = mkdtempSync(join(tmpdir(), "ci-changes-"));
  try {
    const git = (...args) => execFileSync("git", ["-C", dir, ...args], { encoding: "utf8" }).trim();
    git("init", "-q", "-b", "main");
    git("config", "user.email", "t@example.com");
    git("config", "user.name", "t");
    git("config", "commit.gpgsign", "false");
    // `git commit` otherwise forks a detached `git maintenance run --auto` that can still be writing
    // .git/objects when the finally block removes the repo (ENOTEMPTY, #265).
    git("config", "maintenance.auto", "false");
    git("config", "gc.auto", "0");
    mkdirSync(join(dir, "web"), { recursive: true });
    writeFileSync(join(dir, "web", "a.ts"), "a\n");
    writeFileSync(join(dir, "README.md"), "r\n");
    git("add", ".");
    git("commit", "-q", "-m", "base");
    git("checkout", "-q", "-b", "feature");
    git("mv", "web/a.ts", "web/b.ts");
    git("commit", "-q", "-m", "rename");
    const head = git("rev-parse", "HEAD");
    git("checkout", "-q", "main");
    mkdirSync(join(dir, "src", "Desk.Api"), { recursive: true });
    writeFileSync(join(dir, "src", "Desk.Api", "x.cs"), "x\n");
    git("add", ".");
    git("commit", "-q", "-m", "main moved on");
    const base = git("rev-parse", "HEAD");

    // Only the PR side: main's own later change to src/ is not in the list.
    assert.deepEqual(changedPaths(dir, base, head).sort(), ["web/a.ts", "web/b.ts"]);
    assert.throws(() => changedPaths(dir, "f".repeat(40), head));
  } finally {
    removeRepo(dir);
  }
});

// The ci.yml wiring tests (parse-only) live in ci-wiring.test.mjs (ADR-0026); the tests
// below execute ci.yml step scripts, so they stay here, outside the protected file.
const CI = readFileSync(new URL("../workflows/ci.yml", import.meta.url), "utf8");
const jobBlock = (id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|(?![\\s\\S]))`, "m").exec(CI);
  assert.ok(m, `job ${id} not found in ci.yml`);
  return m[1];
};

// #213: the classifier and the coverage gate diff from one base, pinned once in the
// changes job. On #195 the event base was older than the merge commit's first parent,
// so the gate saw another PR's src/ change that the classifier (rightly) did not.
const stepRun = (block, id) => {
  const m = new RegExp(`^      - id: ${id}\\n([\\s\\S]*?)(?=^      - |(?![\\s\\S]))`, "m").exec(block);
  assert.ok(m, `step ${id} not found`);
  const run = /^ {8}run: \|\n((?: {10}.*\n|\n)+)/m.exec(m[1]);
  assert.ok(run, `step ${id} has no run block`);
  return { step: m[1], script: run[1].replace(/^ {10}/gm, "") };
};

test("the pinned base makes the gate's diff match the classifier's when main moved (#213, the #195 run)", () => {
  const { script } = stepRun(jobBlock("changes"), "base");
  const dir = mkdtempSync(join(tmpdir(), "ci-base-"));
  try {
    const git = (...args) => execFileSync("git", ["-C", dir, ...args], { encoding: "utf8" }).trim();
    git("init", "-q", "-b", "main");
    git("config", "user.email", "t@example.com");
    git("config", "user.name", "t");
    git("config", "commit.gpgsign", "false");
    git("config", "maintenance.auto", "false");
    git("config", "gc.auto", "0");
    const write = (path, text) => {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    };
    write("web/src/a.ts", "a\n");
    write("src/Desk.Data/Grid/GridSqlBuilder.cs", "g\n");
    git("add", ".");
    git("commit", "-q", "-m", "base");
    const eventBase = git("rev-parse", "HEAD");
    git("checkout", "-q", "-b", "pr");
    write("web/src/a.ts", "a2\n");
    git("commit", "-q", "-am", "web-only PR");
    const prHead = git("rev-parse", "HEAD");
    git("checkout", "-q", "main");
    write("src/Desk.Data/Grid/GridSqlBuilder.cs", "g2\n");
    git("commit", "-q", "-am", "another PR merged mid-run");
    const mainTip = git("rev-parse", "HEAD");
    // GITHUB_SHA: the merge commit GitHub built on the newer main.
    git("checkout", "-q", "--detach", mainTip);
    git("merge", "-q", "--no-ff", "--no-edit", prHead);

    const pin = (env) => {
      const out = join(dir, ".git", "out");
      const sum = join(dir, ".git", "sum");
      writeFileSync(out, "");
      const r = spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", script], {
        cwd: dir,
        encoding: "utf8",
        env: { PATH: process.env.PATH, GITHUB_OUTPUT: out, GITHUB_STEP_SUMMARY: sum, ...env },
      });
      assert.equal(r.status, 0, r.stderr);
      const lines = readFileSync(out, "utf8").trim().split("\n");
      assert.equal(lines.length, 1);
      return { base: lines[0].replace(/^base_sha=/, ""), stdout: r.stdout };
    };
    // The coverage gate: `git diff <merge-base of --base and HEAD> HEAD -- src web/src`.
    const gateFiles = (base) => {
      const mb = git("merge-base", base, "HEAD");
      return git("diff", "--name-only", mb, "HEAD", "--", "src", "web/src").split("\n").filter(Boolean).sort();
    };

    const pinned = pin({ EVENT_NAME: "pull_request", EVENT_BASE: eventBase, HEAD_SHA: prHead });
    assert.equal(pinned.base, mainTip, "the merge commit's first parent");
    assert.deepEqual(changedPaths(dir, pinned.base, prHead), ["web/src/a.ts"]);
    assert.deepEqual(gateFiles(pinned.base), ["web/src/a.ts"]);
    // The old wiring: the gate diffed from the event base and saw the other PR's file.
    assert.deepEqual(changedPaths(dir, eventBase, prHead), ["web/src/a.ts"]);
    assert.deepEqual(gateFiles(eventBase), ["src/Desk.Data/Grid/GridSqlBuilder.cs", "web/src/a.ts"]);

    // Anything unexpected keeps the event base (the pre-#213 behaviour), with a warning.
    const other = pin({ EVENT_NAME: "pull_request", EVENT_BASE: eventBase, HEAD_SHA: mainTip });
    assert.equal(other.base, eventBase);
    assert.match(other.stdout, /^::warning::HEAD is not the merge of /m);
    const unknown = "f".repeat(40);
    assert.equal(pin({ EVENT_NAME: "pull_request", EVENT_BASE: unknown, HEAD_SHA: prHead }).base, unknown);
    git("checkout", "-q", "--detach", prHead);
    assert.equal(pin({ EVENT_NAME: "pull_request", EVENT_BASE: eventBase, HEAD_SHA: prHead }).base, eventBase);
    // Push: event.before, and no git at all.
    assert.equal(pin({ EVENT_NAME: "push", EVENT_BASE: eventBase, HEAD_SHA: "" }).base, eventBase);
  } finally {
    removeRepo(dir);
  }
});

// #278 (#447 nit): when the changes job fails before it pins a base (or leaves no output), the coverage gate
// falls back to the event base, the pre-#213 range. That fallback must be visible (a ::warning::), and the
// step summary must say which base the gate actually diffed from.
test("coverage gate step: warns on the event-base fallback and labels its diff base (#278)", () => {
  const coverage = jobBlock("coverage");
  const m = /^      - name: Coverage gates\n([\s\S]*?)(?=^      - |(?![\s\S]))/m.exec(coverage);
  assert.ok(m, "Coverage gates step not found");
  assert.ok(m[1].includes("PINNED_BASE: ${{ needs.changes.outputs.base_sha }}"));
  assert.ok(m[1].includes("CHANGES_RESULT: ${{ needs.changes.result }}"));
  const script = /^ {8}run: \|\n((?: {10}.*\n|\n)+)/m.exec(m[1])[1].replace(/^ {10}/gm, "");

  const dir = mkdtempSync(join(tmpdir(), "ci-cov-"));
  try {
    const git = (...args) => execFileSync("git", ["-C", dir, ...args], { encoding: "utf8" }).trim();
    git("init", "-q", "-b", "main");
    git("config", "user.email", "t@example.com");
    git("config", "user.name", "t");
    git("config", "commit.gpgsign", "false");
    git("commit", "-q", "--allow-empty", "-m", "event base");
    const eventBase = git("rev-parse", "HEAD");
    git("commit", "-q", "--allow-empty", "-m", "merge commit's first parent");
    const pinnedBase = git("rev-parse", "HEAD");
    // No _base/perf/coverage-gate.mjs here, so the step stops (exit 1) right after it labels the base.
    const runStep = (env) => {
      const sum = join(dir, ".git", "sum");
      writeFileSync(sum, "");
      const r = spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", script], {
        cwd: dir,
        encoding: "utf8",
        env: { PATH: process.env.PATH, GITHUB_STEP_SUMMARY: sum, BASE_SHA: eventBase, RUN_API: "true", RUN_WEB: "true", ...env },
      });
      assert.equal(r.status, 1, r.stderr);
      assert.match(r.stdout, /^::error::BASE_SHA .* has no perf\/coverage-gate\.mjs/m);
      return { stdout: r.stdout, summary: readFileSync(sum, "utf8") };
    };

    const pinned = runStep({ PINNED_BASE: pinnedBase, DIFF_BASE: pinnedBase, CHANGES_RESULT: "success" });
    assert.doesNotMatch(pinned.stdout, /::warning::/);
    assert.match(pinned.summary, new RegExp(`Diff base \\(#213, pinned by the changes job\\): \`${pinnedBase}\``));
    assert.match(pinned.summary, new RegExp(`_base \\(gate, thresholds, floor\\): \`${eventBase}\``));

    for (const result of ["failure", "success"]) {
      const fallback = runStep({ PINNED_BASE: "", DIFF_BASE: eventBase, CHANGES_RESULT: result });
      assert.match(
        fallback.stdout,
        new RegExp(`^::warning::The changes job \\(${result}\\) left no base_sha; the coverage gate diffs from the event base ${eventBase}`, "m"),
      );
      assert.match(fallback.summary, new RegExp(`Diff base \\(#213, event-base fallback: the changes job left no base_sha\\): \`${eventBase}\``));
      assert.doesNotMatch(fallback.summary, /pinned by the changes job/);
    }
  } finally {
    removeRepo(dir);
  }
});

// Every file a .NET test project builds from or links must run the api job (R173-02).
test("every test-project input maps to the api job (or every flag)", () => {
  const repo = new URL("../..", import.meta.url).pathname;
  const projects = execFileSync("git", ["-C", repo, "ls-files", "tests/*.csproj", "tests/**/*.csproj"], { encoding: "utf8" })
    .split("\n")
    .filter(Boolean);
  assert.ok(projects.length >= 3, projects.join(","));
  let checked = 0;
  for (const proj of projects) {
    const xml = readFileSync(join(repo, proj), "utf8");
    for (const m of xml.matchAll(/<(ProjectReference|None|Content|Compile|EmbeddedResource)\b[^>]*\bInclude="([^"]+)"/g)) {
      const target = normalizePath(join(dirname(proj), m[2].replaceAll("\\", "/")));
      const probe = m[1] === "ProjectReference" ? join(dirname(target), "X.cs") : target;
      const areas = areasFor(probe);
      assert.ok(areas === null || areas.some((a) => JOBS.run_api.includes(a)), `${proj} -> ${probe} maps to [${areas}]`);
      checked++;
    }
  }
  assert.ok(checked >= 6, `checked ${checked} inputs`);
});

// Every project in CreditDesk.slnx is built by the api job, so its files must run it (R173-06).
test("every solution project maps to the api job (or every flag)", () => {
  const repo = new URL("../..", import.meta.url).pathname;
  const slnx = readFileSync(join(repo, "CreditDesk.slnx"), "utf8");
  const paths = [...slnx.matchAll(/Path="([^"]+)"/g)].map((m) => m[1].replaceAll("\\", "/"));
  assert.ok(paths.length >= 9, paths.join(","));
  for (const p of paths) {
    for (const probe of [join(dirname(p), "X.cs"), join(dirname(p), "Strings.resx"), p]) {
      const areas = areasFor(probe);
      assert.ok(areas === null || areas.some((a) => JOBS.run_api.includes(a)), `${probe} maps to [${areas}]`);
    }
  }
  assert.deepEqual(areasFor("perf/payload-size.mjs"), ["perf"]);
});
