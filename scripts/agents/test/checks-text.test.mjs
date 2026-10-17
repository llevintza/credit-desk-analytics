import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { gitListing, runChecks, treeView } from "../check-drift.mjs";
import { TEXT_CHECKS, anchors, jobs, slug } from "../lib/checks-text.mjs";
import { scratch } from "./helpers.mjs";

const REPO = new URL("../../../", import.meta.url).pathname;
const real = (p) => readFileSync(join(REPO, p), "utf8");
const ADAPTERS = JSON.parse(real("scripts/agents/adapters.json"));
const MARK = " (planned; arrives with the harness" + " build PR)";

function view(files) {
  const root = scratch();
  const listing = [];
  for (const [path, text] of Object.entries(files)) {
    if (text === null) continue;
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), text);
    listing.push({ path, mode: "100644", type: "blob", sha: "1".repeat(40) });
  }
  return treeView(root, listing);
}
const run = (id, files, baseFiles) => {
  const ctx = { head: view(files), base: baseFiles && view(baseFiles), inputs: ADAPTERS.inputs, armed: { on: new Set([id]) } };
  return runChecks(ctx, [id])[0].fails;
};
const fails = (id, files, re, b) => {
  const f = run(id, files, b);
  assert.ok(f.some((m) => re.test(m)), `${id}: expected ${re}, got ${JSON.stringify(f)}`);
};
const passes = (id, files, b) => assert.deepEqual(run(id, files, b), [], id);

test("the real tree at HEAD passes every text check (D12 is disarmed on main)", () => {
  const head = treeView(REPO, gitListing(REPO));
  const ctx = { head, base: head, inputs: ADAPTERS.inputs, armed: { on: new Set(Object.keys(TEXT_CHECKS).filter((k) => k !== "D12")) } };
  for (const r of runChecks(ctx, Object.keys(TEXT_CHECKS))) assert.deepEqual(r.fails, [], r.id);
});

// D17: the five real gate copies, then one mutation per rule.
const GATE = { "AGENTS.md": real("AGENTS.md"), "README.md": real("README.md"), "docs/deployment-setup.md": real("docs/deployment-setup.md") };
const CLAUSE3 = "nothing removed or loosened.";
test("D17 gate text (a)–(d)", () => {
  passes("D17", GATE, GATE);
  passes("D17", GATE);
  fails("D17", { ...GATE, "docs/deployment-setup.md": GATE["docs/deployment-setup.md"].replace(CLAUSE3, "nothing removed.") }, /deployment-setup.md: the gate block differs/);
  const loosened = GATE["README.md"].replaceAll(CLAUSE3, "nothing removed.");
  fails("D17", { ...GATE, "README.md": loosened }, /README §14.5: the gate block differs from base/, GATE);
  fails("D17", { ...GATE, "AGENTS.md": GATE["AGENTS.md"].replace("\n\n   Tech Coordinator merges and starts", "\n   Tech Coordinator merges and starts") }, /item 6: the block must be followed by one empty line/);
  fails("D17", { ...GATE, "AGENTS.md": GATE["AGENTS.md"].replace("- CI is green: build, tests, lint, e2e, budgets (each once it exists).\n", "") }, /CI-is-green bullet/);
  fails("D17", { ...GATE, "AGENTS.md": GATE["AGENTS.md"].replace("- Tech Coordinator merges and starts the next phase.", "- TC merges.") }, /later bullet/);
  fails("D17", { ...GATE, "AGENTS.md": GATE["AGENTS.md"].replace("\n  2. coverlet", "\n  two. coverlet") }, /DoD: the gate block .* missing or malformed/);
  fails("D17", { ...GATE, "README.md": null }, /README §14.5: .*missing/);
  fails("D17", GATE, /differs from base/, { "README.md": "### 14.5 x\n" });
});

const GOV = `name: agents-governance
on:
  pull_request_target:
    types: [opened, synchronize, reopened, edited, ready_for_review]
    branches: [main]
permissions: {}
concurrency:
  group: g-\${{ github.event.pull_request.number }}
  cancel-in-progress: true
jobs:
  agents-drift-base:
    name: agents-drift (base)
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    permissions:
      contents: read
      pull-requests: read
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
      - uses: actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0
        with:
          node-version: 22.12.0
          package-manager-cache: false
      - run: node scripts/agents/governance.mjs
`;
const W = ".github/workflows/";
const OTHER = "name: ci\non: [pull_request]\npermissions:\n  contents: read\njobs:\n  build:\n    name: build\n    runs-on: ubuntu-24.04\n";
const gov = (text) => ({ [`${W}agents-governance.yml`]: text, [`${W}ci.yml`]: OTHER });

test("D20 agents-governance.yml restrictions", () => {
  passes("D20", gov(GOV));
  const bad = {
    "trigger types": GOV.replace(", ready_for_review]", "]"),
    branches: GOV.replace("[main]", "[main, dev]"),
    "top-level permissions": GOV.replace("permissions: {}", "permissions:\n  contents: read"),
    concurrency: GOV.replace(/concurrency:\n.*\n.*\n/, ""),
    "secrets.": GOV.replace("node scripts", "X=${{ secrets.K }} node scripts"),
    "environment:": GOV.replace("    timeout-minutes: 5", "    environment: production\n    timeout-minutes: 5"),
    "id-token": GOV.replace("      pull-requests: read", "      pull-requests: read\n      id-token: write"),
    "actions/cache": GOV.replace("      - run:", "      - uses: actions/cache@0000000000000000000000000000000000000000 # v4\n      - run:"),
    "upload-artifact": GOV.replace("      - run:", "      - uses: actions/upload-artifact@0000000000000000000000000000000000000000 # v4\n      - run:"),
    GITHUB_ENV: GOV.replace("node scripts", 'echo x >> "$GITHUB_ENV"; node scripts'),
    "cache: key": GOV.replace("package-manager-cache: false", "package-manager-cache: false\n          cache: npm"),
    "git fetch": GOV.replace("node scripts", "git fetch origin; node scripts"),
    "PR string": GOV.replace("node scripts", 'echo "${{ github.event.pull_request.title }}"; node scripts'),
    "head SHA": GOV.replace("persist-credentials: false", "persist-credentials: false\n          ref: ${{ github.event.pull_request.head.sha }}"),
    "refs/pull": GOV.replace("node scripts", "x refs/pull/1/head; node scripts"),
    "package-manager-cache": GOV.replace("\n          package-manager-cache: false", ""),
    "job-level if": GOV.replace("    timeout-minutes: 5", "    if: always()\n    timeout-minutes: 5"),
    timeout: GOV.replace("    timeout-minutes: 5\n", ""),
    "job permissions": GOV.replace("      pull-requests: read\n", "      pull-requests: write\n"),
  };
  for (const [what, text] of Object.entries(bad)) assert.ok(run("D20", gov(text)).length > 0, what);
});

test("D20 every other workflow: no _target, reserved names, no write-all/statuses/checks, top-level permissions", () => {
  const job = (id, name) => `name: x\non: [pull_request]\npermissions:\n  contents: read\njobs:\n  ${id}:\n    name: ${name}\n    runs-on: ubuntu-24.04\n`;
  fails("D20", { [`${W}x.yml`]: "on:\n  pull_request_target:\npermissions: {}\n" }, /pull_request_target is allowed only/);
  fails("D20", { [`${W}x.yml`]: job("lint", "agents-drift (base)") }, /reserved base-check name/);
  fails("D20", { [`${W}x.yml`]: job("lint", "Governance-Paths (base)") }, /reserved/);
  fails("D20", { [`${W}x.yml`]: job("lint", "agents\u200b-drift (base)") }, /reserved/, "zero-width look-alike");
  fails("D20", { [`${W}x.yml`]: job("lint", "ａgents-drift (base)") }, /reserved/, "NFKC look-alike");
  fails("D20", { [`${W}x.yml`]: job("ci-wiring", "w") }, /reserved/);
  fails("D20", { [`${W}x.yml`]: job("lint", "${{ matrix.n }}") }, /expression in its name/);
  fails("D20", { [`${W}x.yml`]: "on: push\njobs:\n  a:\n    runs-on: x\n" }, /no top-level permissions/);
  for (const g of ["statuses: write", "checks: write", "write-all"]) fails("D20", { [`${W}x.yml`]: `on: push\npermissions:\n  ${g}\n` }, /grants/);
  passes("D20", { [`${W}claude-review.yml`]: `${OTHER}  # agents-drift:restore-step:begin\n  # agents-drift:hook-canary\n` });
  passes("D20", { [`${W}x.yml`]: "# pull_request_target in a comment\non: push\npermissions: {}\n" });
});

test("D21 pins: the 15 rev 5 rows", () => {
  const f = (line) => run("D21", { [`${W}agents.yml`]: `jobs:\n  a:\n    steps:\n${line}\n` });
  const SHA = "3d3c42e5aac5ba805825da76410c181273ba90b1";
  const ok = [`      - uses: actions/checkout@${SHA} # v7.0.1`, `      - uses: ./.github/actions/x`, `      - uses: ./.github/actions/x # local`, `        uses: actions/setup-node@${SHA} #v7`];
  const bad = [
    "      - uses: actions/checkout@v4", `      - uses: actions/checkout@${SHA.slice(0, 7)} # v7`, `      - uses: actions/checkout@${SHA.toUpperCase()} # v7`,
    "      - uses: actions/checkout@main # v7", "      - uses: docker://alpine:3", `      - uses: actions/checkout@${SHA}`, `      - uses: "actions/checkout@${SHA}" # v7`,
    `      - {uses: actions/checkout@${SHA} # v7}`, `      - uses: actions/checkout@${SHA} # 7.0.1`, `      - uses:   'x/y@${SHA}' # v1`, `      - uses: x@${SHA} # v1`,
  ];
  for (const l of ok) assert.deepEqual(f(l), [], l);
  for (const l of bad) assert.ok(f(l).length, l);
  assert.equal(ok.length + bad.length, 15);
});

test("D23 links, anchors, the guidelines index and the rev 14 marker states", () => {
  const base = { "docs/agents/README.md": "# Index\n\n## Rev 14 (H21): markers\n[g](guidelines/a.md)\n", "docs/agents/guidelines/a.md": "# a\n" };
  passes("D23", { ...base, "web/AGENTS.md": "[r](../docs/agents/README.md#rev-14-h21-markers) [u](https://x.y/z) [s](#top)\n# Top\n" });
  fails("D23", { ...base, "web/AGENTS.md": "[r](../docs/agents/nope.md)\n" }, /does not resolve/);
  fails("D23", { ...base, "web/AGENTS.md": "[r](../../../x.md)\n" }, /does not resolve/);
  fails("D23", { ...base, "web/AGENTS.md": "[r](../docs/agents/README.md#nope)\n" }, /anchor #nope not found/);
  passes("D23", { ...base, "web/AGENTS.md": "[r](../docs/agents/)\n`[x](nowhere.md)`\n" });
  fails("D23", { ...base, "docs/agents/guidelines/b.md": "x" }, /b.md is not linked/);

  const marked = (piece) => ({ ...base, "AGENTS.md": `The ${piece}${MARK} does x.\n` });
  passes("D23", marked("agents-governance.yml"));
  fails("D23", { ...marked("agents-governance.yml"), [`${W}agents-governance.yml`]: "x" }, /agents-governance.yml has landed; remove its "planned" marker/);
  passes("D23", marked("guard hook"));
  fails("D23", { ...marked("guard hook"), "scripts/agents/hooks/guard.mjs": "x" }, /the guard hook has landed/);
  fails("D23", { ...marked("`disableAllHooks`"), [`${W}claude-review.yml`]: `--settings '{"disableAllHooks": true}'` }, /disableAllHooks in claude-review.yml has landed/);
  fails("D23", { ...marked(".claude/agents"), ".claude/agents/r.md": "x" }, /\.claude\/agents has landed/);
  fails("D23", marked("widget"), /names no known piece/);
  passes("D23", { ...base, "AGENTS.md": "the literal suffix `(planned; arrives with the harness" + " build PR)`\n" });
  // The nearest piece before the marker wins: guard is planned even though agents-governance.yml landed.
  passes("D23", { ...base, "AGENTS.md": `agents-governance.yml runs; the guard hook${MARK}.\n`, [`${W}agents-governance.yml`]: "x" });
});

test("anchors: GitHub slugs, duplicates, fences", () => {
  assert.equal(slug("Revision 14 (H21, Helms 2026-10-16): planned markers"), "revision-14-h21-helms-2026-10-16-planned-markers");
  assert.equal(slug("`gh`: canonical [form](x.md) only"), "gh-canonical-form-only");
  assert.deepEqual([...anchors("# A\n## A\n```\n# B\n```\n### Ünï_x #\n")], ["a", "a-1", "ünï_x"]);
});

const CI = real(`${W}ci.yml`);
test("DEP: job-level if: only in the exact ADR-0023 shape", () => {
  passes("DEP", { [`${W}ci.yml`]: CI });
  fails("DEP", { [`${W}ci.yml`]: CI.replace("run_api != 'false'", "run_api == 'true'") }, /job api: a job-level if: must be the exact ADR-0023 shape/);
  fails("DEP", { [`${W}ci.yml`]: CI.replace("${{ !cancelled() && needs.changes.outputs.run_web", "${{ needs.changes.outputs.run_web") }, /job web/);
  fails("DEP", { [`${W}ci.yml`]: CI.replace("      && needs.changes.outputs.run_budgets != 'false'\n", "      && needs.changes.outputs.run_e2e != 'false'\n") }, /job budgets/);
  fails("DEP", { [`${W}ci.yml`]: CI.replace("      && contains(fromJSON('[\"success\", \"skipped\"]'), needs.web.result) }}\n    steps:\n      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:", " }}\n    steps:\n      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:") }, /job/);
  fails("DEP", { [`${W}ci.yml`]: CI.replace("  secrets:\n", "  secrets:\n    if: github.event_name == 'push'\n") }, /job secrets/);
  passes("DEP", { [`${W}ci.yml`]: CI.replace("      - name: Gate script tests", "      - if: failure()\n        run: echo x\n      - name: Gate script tests") }, "a step-level if: is fine");
  fails("DEP", {}, /ci.yml is missing/);
});

test("jobs(): ids, job-level keys, multi-line values", () => {
  const j = jobs("on: x\njobs:\n  a:\n    name: A\n    if: >-\n      x\n      && y\n    permissions:\n      contents: read\n  'b':\n    needs: [a]\nafter: 1\n");
  assert.deepEqual(j.map((x) => x.id), ["a", "b"]);
  assert.equal(j[0].key("if"), "x && y");
  assert.equal(j[0].key("permissions"), "contents: read");
  assert.equal(j[1].key("needs"), "[a]");
  assert.equal(j[1].key("name"), undefined);
  assert.deepEqual(jobs("on: push\n"), []);
});

test("D12 claude-review.yml once armed", () => {
  const good = [
    "# agents-drift:restore-step:begin", "# agents-drift:restore-step:end", "# agents-drift:claude-args", "# agents-drift:hook-canary",
    `--settings '{"disableAllHooks": true}'`, '--disallowedTools "Read(./.git/**),Read(./.review-base/.git/**),Skill,Agent,Task"',
  ].join("\n");
  const files = { [`${W}claude-review.yml`]: good, "README.md": "## 14. CI\n### 14.5 Review\n", "AGENTS.md": "README §14.5 and §14\n" };
  passes("D12", files);
  fails("D12", { ...files, [`${W}claude-review.yml`]: good.replace("# agents-drift:hook-canary", "") }, /lacks the marker # agents-drift:hook-canary/);
  fails("D12", { ...files, [`${W}claude-review.yml`]: good.replace("disableAllHooks", "x") }, /lacks --settings/);
  fails("D12", { ...files, [`${W}claude-review.yml`]: good.replace(",Task", "") }, /lacks Task/);
  fails("D12", { ...files, "AGENTS.md": "See README §99.\n" }, /cites README §99/);
});
