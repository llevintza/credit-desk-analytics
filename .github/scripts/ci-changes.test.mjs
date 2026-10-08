import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
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

test("changedPaths diffs from the merge-base, lists both sides of a rename, and fails on a bad SHA", () => {
  const dir = mkdtempSync(join(tmpdir(), "ci-changes-"));
  try {
    const git = (...args) => execFileSync("git", ["-C", dir, ...args], { encoding: "utf8" }).trim();
    git("init", "-q", "-b", "main");
    git("config", "user.email", "t@example.com");
    git("config", "user.name", "t");
    git("config", "commit.gpgsign", "false");
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
    rmSync(dir, { recursive: true, force: true });
  }
});

// ci.yml must wire every JOBS flag to its job, keep always-run jobs unconditional,
// and never use workflow-level paths filters (required checks must always report).
const CI = readFileSync(new URL("../workflows/ci.yml", import.meta.url), "utf8");
const jobBlock = (id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|(?![\\s\\S]))`, "m").exec(CI);
  assert.ok(m, `job ${id} not found in ci.yml`);
  return m[1];
};
const JOB_IDS = {
  run_api: "api",
  run_web: "web",
  run_db_tools: "db-tools",
  run_compose_smoke: "compose-smoke",
  run_e2e: "e2e",
  run_budgets: "budgets",
  run_coverage: "coverage",
};

test("ci.yml: each heavy job needs changes and skips only on its own flag being 'false'", () => {
  assert.deepEqual(Object.keys(JOB_IDS).sort(), Object.keys(JOBS).sort());
  for (const [flag, id] of Object.entries(JOB_IDS)) {
    const block = jobBlock(id);
    assert.match(block, /^    needs: (changes|\[changes, api, web\])$/m, id);
    assert.match(block, /^    if: /m, id);
    assert.ok(block.includes("!cancelled()"), `${id} keeps reporting when a needed job is skipped`);
    assert.ok(block.includes(`needs.changes.outputs.${flag} != 'false'`), `${id} uses ${flag}`);
    if (block.includes("needs: [changes, api, web]")) {
      assert.ok(block.includes(`contains(fromJSON('["success", "skipped"]'), needs.api.result)`), id);
      assert.ok(block.includes(`contains(fromJSON('["success", "skipped"]'), needs.web.result)`), id);
    }
    assert.match(jobBlock("changes"), new RegExp(`^      ${flag}: \\$\\{\\{ steps\\.classify\\.outputs\\.${flag} \\}\\}$`, "m"));
  }
});

test("ci.yml: always-run jobs have no needs and no job-level if", () => {
  for (const id of ["secrets", "workflows", "gate-tests"]) {
    const block = jobBlock(id);
    assert.doesNotMatch(block, /^    (needs|if):/m, id);
  }
  assert.ok(jobBlock("gate-tests").includes(".github/scripts/ci-changes.test.mjs"));
});

test("ci.yml: no workflow-level paths filters; classifier runs from the base checkout on PRs only", () => {
  const on = CI.slice(CI.indexOf("\non:"), CI.indexOf("\nconcurrency:"));
  assert.doesNotMatch(on, /paths(-ignore)?:/);
  const changes = jobBlock("changes");
  assert.ok(changes.includes("CLASSIFIER=_base/.github/scripts/ci-changes.mjs"));
  assert.ok(changes.includes("ref: ${{ github.event.pull_request.base.sha }}"));
  assert.ok(changes.includes("fetch-depth: 0"));
  assert.match(changes, /- id: classify\n        if: github\.event_name == 'pull_request'/);
  assert.doesNotMatch(CI, /pull_request_target/);
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

// Any job that needs a job that can be skipped must say how it handles the skip (R173-07,
// R173-12): without a job-level `!cancelled()` it is skipped silently whenever a needed job is.
const SKIPPABLE = new Set(["changes", ...Object.values(JOB_IDS)]);

/** Job ids and their raw blocks, from the `jobs:` section of a workflow. */
function workflowJobs(yml) {
  const lines = yml.slice(yml.indexOf("\njobs:\n") + 7).split("\n");
  const jobs = new Map();
  let current = null;
  for (const line of lines) {
    if (/^\s*(#.*)?$/.test(line)) {
      if (current) jobs.get(current).push(line);
      continue;
    }
    if (/^\S/.test(line)) break;
    const head = /^  ([A-Za-z_][\w-]*):\s*$/.exec(line);
    if (head) jobs.set((current = head[1]), []);
    else if (/^  \S/.test(line)) assert.fail(`unparsed job key: ${line}`);
    else if (current) jobs.get(current).push(line);
  }
  return [...jobs].map(([id, body]) => [id, body]);
}

/** `needs:` in inline (`x`, `[a, b]`) or block-list form; anything else fails, never skips. */
function jobNeeds(id, body) {
  const at = body.findIndex((l) => /^    needs:/.test(l));
  if (at < 0) return [];
  const value = body[at].replace(/^    needs:/, "").trim();
  if (/^\[[^\]]*\]$/.test(value)) {
    const xs = value.slice(1, -1).split(",").map((s) => s.trim()).filter(Boolean);
    for (const x of xs) if (!/^[\w-]+$/.test(x)) assert.fail(`${id}: unparsed needs entry ${x}`);
    return xs;
  }
  if (/^[\w-]+$/.test(value)) return [value];
  if (value === "") {
    const items = [];
    // Only the next key ends the list; anything else inside it fails.
    for (const l of body.slice(at + 1)) {
      if (/^ {0,4}[^\s#]/.test(l)) break;
      if (l.trim() === "") continue;
      const item = /^      - ([\w-]+)\s*$/.exec(l);
      if (!item) assert.fail(`${id}: unparsed needs item: ${l}`);
      items.push(item[1]);
    }
    if (items.length) return items;
  }
  assert.fail(`${id}: unparsed needs: ${body[at]}`);
}

/** The job-level `if:` only (plus folded continuation lines), never a step-level one or a comment. */
function jobIf(body) {
  const at = body.findIndex((l) => /^    if:/.test(l));
  if (at < 0) return "";
  const out = [body[at].replace(/^    if:/, "")];
  for (const l of body.slice(at + 1)) {
    if (!/^      /.test(l)) break;
    out.push(l);
  }
  return out.join("\n");
}

function checkSkippableDependents(yml) {
  let dependents = 0;
  for (const [id, body] of workflowJobs(yml)) {
    const skippable = jobNeeds(id, body).filter((n) => SKIPPABLE.has(n));
    if (!skippable.length) continue;
    dependents++;
    const cond = jobIf(body);
    assert.ok(cond.includes("!cancelled()"), `${id} needs [${skippable}] but its job-level if has no !cancelled()`);
    for (const n of skippable.filter((x) => x !== "changes")) {
      assert.ok(
        cond.includes(`contains(fromJSON('["success", "skipped"]'), needs.${n}.result)`),
        `${id} must accept success or skipped (and nothing else) from ${n} in its job-level if`,
      );
    }
  }
  return dependents;
}

test("ci.yml: every job that needs a skippable job has an explicit !cancelled() if", () => {
  assert.ok(workflowJobs(CI).length >= 11);
  assert.ok(checkSkippableDependents(CI) >= 7);
});

test("dependency checker: block-list needs, step-level ifs and comments do not pass", () => {
  const wf = (job) => `name: x\non: push\njobs:\n  api:\n    runs-on: ubuntu-latest\n${job}`;
  const blockList = "  summary:\n    runs-on: ubuntu-latest\n    needs:\n      - api\n    steps:\n      - run: echo\n";
  assert.throws(() => checkSkippableDependents(wf(blockList)), /summary needs \[api\]/);
  const stepIf =
    "  summary:\n    needs: [api]\n    # !cancelled() contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result)\n" +
    "    steps:\n      - if: ${{ !cancelled() && contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result) }}\n        run: echo\n";
  assert.throws(() => checkSkippableDependents(wf(stepIf)), /summary needs \[api\]/);
  const good =
    "  summary:\n    needs:\n      - api\n    if: >-\n      ${{ !cancelled()\n      && contains(fromJSON('[\"success\", \"skipped\"]'), needs.api.result) }}\n    steps:\n      - run: echo\n";
  assert.equal(checkSkippableDependents(wf(good)), 1);
  assert.throws(() => checkSkippableDependents(wf("  summary:\n    needs: ${{ fromJSON('[]') }}\n")), /summary: unparsed needs/);
  assert.throws(() => checkSkippableDependents(wf("  summary:\n    needs:\n    steps: []\n")), /summary: unparsed needs/);
});

test("dependency checker: unrecognised needs and job-key forms fail, never skip (R205-01)", () => {
  const wf = (job) => `name: x\non: push\njobs:\n  api:\n    runs-on: ubuntu-latest\n${job}`;
  const onlyCancelled = "    if: ${{ !cancelled() }}\n    steps:\n      - run: echo\n";
  const cases = [
    ["comment between items", "    needs:\n      - changes\n      # x\n      - api\n", /summary: unparsed needs item/],
    ["quoted item", '    needs:\n      - changes\n      - "api"\n', /summary: unparsed needs item/],
    ["trailing-comment item", "    needs:\n      - changes\n      - api # x\n", /summary: unparsed needs item/],
    ["quoted flow entry", "    needs: [changes, 'api']\n", /summary: unparsed needs entry 'api'/],
  ];
  for (const [name, needs, err] of cases) {
    assert.throws(() => checkSkippableDependents(wf(`  summary:\n${needs}${onlyCancelled}`)), err, name);
  }
  assert.throws(() => checkSkippableDependents(wf(`  summary: # x\n    needs: [api]\n${onlyCancelled}`)), /unparsed job key/);
  assert.throws(
    () => checkSkippableDependents(wf(`# a column-0 comment\n  summary:\n    needs: [api]\n${onlyCancelled}`)),
    /summary must accept success or skipped/,
  );
});
