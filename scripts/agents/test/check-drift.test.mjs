import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { CHECKS, armed, main, runChecks, summary, treeView } from "../check-drift.mjs";
import { CHECK_ID, TREE_CHECKS, frontmatter, validateArming } from "../lib/checks-tree.mjs";
import { capture, scratch } from "./helpers.mjs";

const ARMING = JSON.parse(readFileSync(new URL("../arming.json", import.meta.url), "utf8"));
const ADAPTERS = JSON.parse(readFileSync(new URL("../adapters.json", import.meta.url), "utf8"));
const ROLES = "### 4. Commands and skills\n\n| Skill | x |\n|---|---|\n| `adr` | model |\n\n### 5. Next\n";
const SKILL = (name, description = "Write an ADR.") => `---\nname: ${name}\ndescription: "${description}"\n---\n# ${name}\n`;

/** A minimal tree that passes every check. Values are text; `{mode, sha}` objects are special entries. */
const GOOD = {
  "AGENTS.md": "# rules\n",
  "CLAUDE.md": "@AGENTS.md\n",
  "web/AGENTS.md": "# web\n",
  "web/CLAUDE.md": "@AGENTS.md\n",
  ".claude/skills/adr/SKILL.md": SKILL("adr"),
  "docs/agents/roles-and-skills.md": ROLES,
  "scripts/agents/arming.json": JSON.stringify(ARMING),
  ".env.example": "KEY=\n",
};

function tree(files) {
  const root = scratch();
  const listing = [];
  for (const [path, v] of Object.entries(files)) {
    if (v === null) continue;
    if (typeof v === "object") {
      listing.push({ path, type: v.mode === "160000" ? "commit" : "blob", sha: "0".repeat(40), ...v });
      continue;
    }
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), v);
    listing.push({ path, mode: "100644", type: "blob", sha: "1".repeat(40) });
  }
  return treeView(root, listing);
}

const check = (id, head, baseFiles) => {
  const h = tree({ ...GOOD, ...head });
  const b = baseFiles ? tree({ ...GOOD, ...baseFiles }) : null;
  const ctx = { head: h, base: b, arming: ARMING, armed: { on: new Set([id]), off: [] } };
  return runChecks(ctx, [id])[0];
};
const passes = (id, head = {}, b) => assert.deepEqual(check(id, head, b).fails, [], id);
const fails = (id, head, re, b) => {
  const r = check(id, head, b);
  assert.ok(r.fails.some((m) => re.test(m)), `${id}: expected a failure matching ${re}, got ${JSON.stringify(r.fails)}`);
};

test("the GOOD tree passes every tree check", () => {
  assert.ok(Object.keys(TREE_CHECKS).every((id) => id in CHECKS));
  for (const id of Object.keys(TREE_CHECKS)) passes(id, {}, {});
});

test("D1 root stub, D2 nested pairs, D16 every stub is exact", () => {
  fails("D1", { "CLAUDE.md": "@AGENTS.md" }, /exactly @AGENTS.md/);
  fails("D1", { "CLAUDE.md": null }, /exactly/);
  fails("D2", { "web/CLAUDE.md": null }, /web\/AGENTS.md has no sibling CLAUDE.md/);
  fails("D2", { "src/Desk.UserAdmin/AGENTS.md": "# a\n" }, /UserAdmin\/AGENTS.md has no sibling/);
  fails("D2", { "src/Desk.UserAdmin/CLAUDE.md": "@AGENTS.md\n" }, /orphan stub src\/Desk.UserAdmin\/CLAUDE.md/);
  fails("D16", { "web/CLAUDE.md": "@AGENTS.md\nmore\n" }, /web\/CLAUDE.md must be exactly/);
});

test("D3 forbidden memory and local-settings files (case variants, nested .claude)", () => {
  for (const p of ["agents.md", "web/agents.md", "Claude.md", "AGENT.md", "src/Desk.UserAdmin/claude.md", "Agents.md"]) fails("D3", { [p]: "x" }, /forbidden memory-file name/);
  fails("D3", { "CLAUDE.local.md": "x" }, /committed CLAUDE.local.md/);
  fails("D3", { ".claude/CLAUDE.md": "x" }, /root stub only/);
  fails("D3", { ".claude/settings.local.json": "{}" }, /local Claude settings/);
  fails("D3", { ".Claude/Settings.Local.json": "{}" }, /local Claude settings/);
  fails("D3", { "web/.claude/settings.json": "{}" }, /nested \.claude\//);
});

test("D5 .claude/rules, D6 Copilot files, D7 .grok and hand-written hooks.json, D11 .claudeignore", () => {
  fails("D5", { ".claude/rules/a.md": "x" }, /rules\/ must be absent/);
  fails("D6", { ".github/copilot-instructions.md": "x" }, /Copilot/);
  fails("D6", { ".github/prompts/a.md": "x" }, /Copilot/);
  fails("D7", { ".grok/config.toml": "x" }, /\.grok\/ must be absent/);
  fails("D7", { ".grok/hooks/a.sh": "x" }, /\.grok\/ must be absent/);
  passes("D7", { ".grok/agents/a.md": "x" });
  fails("D7", { ".cursor/hooks.json": "{}" }, /hand-written/);
  fails("D11", { ".ClaudeIgnore": "x" }, /must be absent/);
});

test("D8 skills: name = folder, description limits, shadowing, legacy commands", () => {
  fails("D8", { ".claude/skills/adr/SKILL.md": SKILL("adr2") }, /name must equal the folder/);
  fails("D8", { ".claude/skills/adr/SKILL.md": SKILL("adr", "x".repeat(201)) }, /1–200 characters \(is 201\)/);
  fails("D8", { ".claude/skills/adr/SKILL.md": "# no frontmatter\n" }, /no frontmatter/);
  const many = Object.fromEntries(Array.from({ length: 8 }, (_, i) => [`.claude/skills/s${i}/SKILL.md`, SKILL(`s${i}`, "y".repeat(190))]));
  fails("D8", many, /total 1533 characters/);
  fails("D8", { ".cursor/skills/ADR/SKILL.md": "x" }, /shadows/);
  passes("D8", { ".cursor/skills/other/SKILL.md": "x" });
  fails("D8", { ".claude/commands/x.md": "x" }, /legacy command/);
  fails("D8", { ".cursor/commands/x.md": "x" }, /legacy command/);
});

test("D8a skill table = skill folders", () => {
  fails("D8a", { ".claude/skills/seeding/SKILL.md": SKILL("seeding") }, /seeding\/ is missing from the roles-and-skills/);
  fails("D8a", { ".claude/skills/adr/SKILL.md": null }, /names adr, but \.claude\/skills\/adr\/ is missing/);
  fails("D8a", { "docs/agents/roles-and-skills.md": "# none\n" }, /no '### 4/);
  passes("D8a", { "docs/agents/roles-and-skills.md": ROLES.replace("\n### 5. Next\n", "") });
});

test("D9 unique subagent names, no .cursor/agents/", () => {
  const agent = (n) => `---\nname: ${n}\n---\n`;
  passes("D9", { ".claude/agents/a.md": agent("a"), ".claude/agents/b.md": agent("b") });
  fails("D9", { ".claude/agents/a.md": agent("x"), ".claude/agents/b.md": agent("X") }, /subagent name X is used by/);
  fails("D9", { ".claude/agents/a.md": "no fm" }, /has no name/);
  fails("D9", { ".cursor/agents/a.md": "x" }, /\.cursor\/agents\/ must be absent/);
});

test("D13 budgets: root 150 (warn 130), nested 100 (warn 60), incl. the UserAdmin node", () => {
  const lines = (n) => "x\n".repeat(n);
  assert.deepEqual(check("D13", { "AGENTS.md": lines(129) }).warns, []);
  assert.match(check("D13", { "AGENTS.md": lines(129) + "y" }).warns[0], /root metric 131/);
  fails("D13", { "AGENTS.md": lines(150) }, /root metric 151 lines \(max 150\)/);
  assert.match(check("D13", { "src/Desk.UserAdmin/AGENTS.md": lines(61) }).warns[0], /61 lines \(warn above 60\)/);
  fails("D13", { "src/Desk.UserAdmin/AGENTS.md": lines(101) }, /101 lines \(max 100\)/);
});

test("D15 any symlink, new or changed submodules", () => {
  for (const p of ["docs/agents/x.md", ".github/workflows/ci.yml", ".claude", "docs/agents", "src/Desk.Api", "web/public/x.svg"])
    fails("D15", { [p]: { mode: "120000" } }, /symlink in the tree/);
  fails("D15", { "vendor/lib": { mode: "160000", sha: "2".repeat(40) } }, /new or changed submodule/, {});
  passes("D15", { "vendor/lib": { mode: "160000", sha: "2".repeat(40) } }, { "vendor/lib": { mode: "160000", sha: "2".repeat(40) } });
  fails("D15", { "vendor/lib": { mode: "160000", sha: "3".repeat(40) } }, /changed submodule/, { "vendor/lib": { mode: "160000", sha: "2".repeat(40) } });
});

test("D16 no @path imports outside code spans", () => {
  fails("D16", { "web/AGENTS.md": "Read @docs/agents/workflow.md first.\n" }, /@ import "@docs\/agents\/workflow.md"/);
  fails("D16", { "docs/agents/x.md": "@../README.md\n" }, /outside a code span/);
  passes("D16", { "web/AGENTS.md": "Use `@AGENTS.md` and\n```text\n@AGENTS.md\n```\nmail me@example or @leo.\n" });
});

test("D18 frontmatter hooks/allowed-tools and the .mcp.json allowlist", () => {
  fails("D18", { ".claude/skills/adr/SKILL.md": "---\nname: adr\nhooks: x\n---\n" }, /key hooks:/);
  fails("D18", { ".claude/agents/r.md": "---\nname: r\nallowed-tools: Bash\n---\n" }, /key allowed-tools:/);
  fails("D18", { ".mcp.json": JSON.stringify({ mcpServers: { gh: { command: "npx" } } }) }, /server gh is not on the checker's allowlist/);
  passes("D18", { ".mcp.json": "{}" });
});

test("D19 tracked env files", () => {
  for (const p of [".env", ".env.dev", "web/.env.prod", "x/.ENV.local"]) fails("D19", { [p]: "x" }, /tracked env file/);
  passes("D19", { "web/.env.example": "x", ".envrc.md": "x", "env.txt": "x" });
});

test("D22 arming.json may only grow; P keeps subjects present on main", () => {
  fails("D22", { "scripts/agents/arming.json": null }, /missing is a fail/);
  fails("D22", { "scripts/agents/arming.json": JSON.stringify({ ...ARMING, alwaysArmed: ARMING.alwaysArmed.slice(1) }) }, /drops always-armed D1/);
  fails("D22", { "scripts/agents/arming.json": JSON.stringify({ ...ARMING, subjects: { D2: ["*/**/AGENTS.md"] } }) }, /drops subject \*\/\*\*\/CLAUDE.md of D2/);
  fails("D22", { "scripts/agents/arming.json": JSON.stringify({ alwaysArmed: [], subjects: {} }) }, /drops/, {});
  passes("D22", { "scripts/agents/arming.json": JSON.stringify({ ...ARMING, alwaysArmed: [...ARMING.alwaysArmed, "D99"] }) }, {});
  fails("P", { "web/AGENTS.md": null, "web/CLAUDE.md": null }, /web\/AGENTS.md is on main and missing from the PR/, {});
  passes("P", { "Web/AGENTS.md": "x", "web/AGENTS.md": null }, {});
  passes("P", { "web/AGENTS.md": null }); // no base: P has nothing to compare
});

test("arming: subject-armed checks run only when the subject is on main or in the PR", () => {
  const noNested = tree({ "AGENTS.md": "x" });
  assert.deepEqual(armed(ARMING, noNested, null).off, ["D2", "D12"]);
  const marked = tree({ ".github/workflows/claude-review.yml": "  # agents-drift:claude-args\n" });
  assert.ok(armed(ARMING, noNested, marked).on.has("D12"), "D12 is armed by a marker in main's claude-review.yml");
  assert.ok(armed(ARMING, noNested, tree(GOOD)).on.has("D2"), "armed by main");
  assert.ok(armed(ARMING, tree(GOOD), null).on.has("D2"), "armed by the PR");
});

test("frontmatter and the summary escaping", () => {
  assert.equal(frontmatter("no"), null);
  assert.deepEqual(frontmatter("---\nname: 'a'\n  nested: x\n---"), { name: "a" });
  const s = summary([{ id: "D3", fails: ["forbidden <x>|y"], warns: ["w`"] }], ["D2"]);
  assert.match(s, /1 checks run, 1 failed/);
  assert.match(s, /Disarmed \(subject absent on main and PR; success with a notice\): D2/);
  assert.ok(s.includes("**D3 fail:** forbidden &lt;x&gt;&#124;y") && s.includes("D3 warn: w&#96;"));
});

test("treeView fails closed on unsafe listing paths and unreadable listed files", () => {
  assert.throws(() => treeView(scratch(), [{ path: "../x", type: "blob" }]), (e) => e.message === "unsafe path in listing" && e.detail === "../x");
  const v = treeView(scratch(), [{ path: "AGENTS.md", mode: "100644", type: "blob" }]);
  assert.throws(() => v.read("AGENTS.md"), /ENOENT/);
  assert.equal(v.read("absent.md"), null);
});

function gitRepo(files) {
  const d = scratch();
  for (const [p, t] of Object.entries(files)) {
    mkdirSync(dirname(join(d, p)), { recursive: true });
    writeFileSync(join(d, p), t);
  }
  const git = (...a) => execFileSync("git", ["-C", d, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", ...a], { stdio: "pipe" });
  git("init", "-q");
  git("add", "-A");
  git("commit", "-q", "-m", "x");
  return d;
}

// The text checks need the real gate copies and ci.yml.
// Root AGENTS.md links into the whole repo, so its links are dropped here (checks-text.test covers D23).
const REAL = Object.fromEntries(["AGENTS.md", "README.md", "docs/deployment-setup.md", ".github/workflows/ci.yml"].map((p) => [p, readFileSync(new URL(`../../../${p}`, import.meta.url), "utf8")]));
REAL["AGENTS.md"] = REAL["AGENTS.md"].replace(/\]\([^)]*\)/g, "]");

test("main: a real checkout against a base checkout, the summary file, exit codes", () => {
  const own = { ...REAL, "scripts/agents/arming.json": JSON.stringify(ARMING), "scripts/agents/adapters.json": JSON.stringify(ADAPTERS) };
  const baseDir = gitRepo({ ...GOOD, ...own });
  const sum = join(scratch(), "s.md");
  const ok = capture();
  assert.equal(main(["--root", gitRepo({ ...GOOD, ...own }), "--base", baseDir], { GITHUB_STEP_SUMMARY: sum }, { write: ok.write }), 0);
  assert.match(ok.text(), /^agents-drift: 22 checks, 0 failed; disarmed: D12\n/);
  assert.match(readFileSync(sum, "utf8"), /22 checks run, 0 failed/);

  const bad = capture();
  const head = gitRepo({ ...GOOD, ...own, "web/AGENTS.md": "x\n".repeat(61), ".env.dev": "x" });
  assert.equal(main(["--root", head, "--base", baseDir], {}, { write: bad.write }), 1);
  assert.match(bad.text(), /"D13 warn: web\/AGENTS.md: 61 lines[\s\S]*"D19 fail: tracked env file \.env\.dev"[\s\S]*::error::agents-drift failed: D19\n$/);
});

test("main: --paths listing (the base-job form), no base, disarmed notice, usage and errors", () => {
  const d = scratch();
  for (const [p, t] of Object.entries(REAL)) {
    mkdirSync(dirname(join(d, p)), { recursive: true });
    writeFileSync(join(d, p), t);
  }
  writeFileSync(join(d, "CLAUDE.md"), "@AGENTS.md\n");
  mkdirSync(join(d, "scripts/agents"), { recursive: true });
  writeFileSync(join(d, "scripts/agents/arming.json"), JSON.stringify(ARMING));
  mkdirSync(join(d, "docs/agents"), { recursive: true });
  writeFileSync(join(d, "docs/agents/roles-and-skills.md"), "### 4. Commands and skills\n");
  const paths = join(scratch(), "paths.json");
  const blob = (path) => ({ path, mode: "100644", type: "blob", sha: "1".repeat(40), size: 1 });
  writeFileSync(paths, JSON.stringify([...Object.keys(REAL).map(blob), blob("CLAUDE.md"), blob("scripts/agents/arming.json"), blob("docs/agents/roles-and-skills.md"), { path: "docs", type: "tree" }]));
  const c = capture();
  assert.equal(main(["--root", d, "--paths", paths], {}, { write: c.write }), 0);
  assert.match(c.text(), /disarmed: D2, D12/);

  const u = capture();
  assert.equal(main([], {}, { write: u.write }), 1);
  assert.match(u.text(), /usage/);
  const e = capture();
  assert.equal(main(["--root", scratch()], {}, { write: e.write }), 1);
  assert.match(e.text(), /lookup or parse error \(fail closed\)/);
  const r = spawnSync(process.execPath, [fileURLToPath(new URL("../check-drift.mjs", import.meta.url))], { encoding: "utf8" });
  assert.equal(r.status, 1);
});

test("D22 strict arming.json shape: strings for arrays, unknown ids, extra keys, duplicates (#502 review)", () => {
  const as = (x) => ({ "scripts/agents/arming.json": JSON.stringify(x) });
  const bad = {
    "space-joined string": { alwaysArmed: ARMING.alwaysArmed.join(" "), subjects: ARMING.subjects },
    "subject globs as a string": { alwaysArmed: ARMING.alwaysArmed, subjects: { D2: ARMING.subjects.D2.join(" ") } },
    "unknown id": { alwaysArmed: [...ARMING.alwaysArmed, "X1"], subjects: ARMING.subjects },
    "unknown subject id": { alwaysArmed: ARMING.alwaysArmed, subjects: { ...ARMING.subjects, "../x": ["a"] } },
    "extra key": { ...ARMING, disarmed: ["D1"] },
    "missing key": { alwaysArmed: ARMING.alwaysArmed },
    duplicate: { alwaysArmed: [...ARMING.alwaysArmed, "D1"], subjects: ARMING.subjects },
    "empty glob": { alwaysArmed: ARMING.alwaysArmed, subjects: { D2: [""] } },
    "subjects array": { alwaysArmed: ARMING.alwaysArmed, subjects: [] },
    "not an object": [ARMING],
  };
  for (const [what, x] of Object.entries(bad)) fails("D22", as(x), /arming.json is malformed/, {});
  fails("D22", { "scripts/agents/arming.json": "null" }, /malformed: arming.json must be an object/);
  assert.throws(() => validateArming({ alwaysArmed: ["D2x"], subjects: {} }, new Set(["D1"])), /unknown check id D2x/);
  assert.ok(CHECK_ID.test("D8a") && CHECK_ID.test("DEP") && !CHECK_ID.test("D8 ") && !CHECK_ID.test("d1"));
});

test("main: a malformed or unknown-id trusted arming.json fails closed", () => {
  for (const arming of [{ alwaysArmed: "D1 D3", subjects: {} }, { alwaysArmed: ["D99"], subjects: {} }]) {
    const own = { "scripts/agents/arming.json": JSON.stringify(arming), "scripts/agents/adapters.json": JSON.stringify(ADAPTERS) };
    const d = gitRepo({ ...GOOD, ...own });
    const c = capture();
    assert.equal(main(["--root", d, "--base", d], {}, { write: c.write }), 1);
    assert.match(c.text(), /^::error::agents-drift: arming.json/);
  }
});

test("main: an unsafe --paths entry is printed only escaped; a --root inside another repo fails", () => {
  const paths = join(scratch(), "paths.json");
  writeFileSync(paths, JSON.stringify([{ path: "a\n::warning::x", mode: "100644", type: "blob" }]));
  const c = capture();
  assert.equal(main(["--root", scratch(), "--paths", paths], {}, { write: c.write }), 1);
  assert.match(c.text(), /^::error::agents-drift: unsafe path in listing\n::stop-commands::\w+\n"a\\n::warning::x"\n/);
  const repo = gitRepo({ "AGENTS.md": "x\n", "sub/f.txt": "y" });
  const n = capture();
  assert.equal(main(["--root", join(repo, "sub")], {}, { write: n.write }), 1);
  assert.match(n.text(), /must be the top of a git work tree/);
});
