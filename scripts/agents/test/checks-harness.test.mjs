import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { runChecks, treeView } from "../check-drift.mjs";
import { render, renderCursorCli } from "../generate.mjs";
import { covered } from "../lib/checks-harness.mjs";
import { MIN_DENY_CURSOR } from "../lib/harness-policy.mjs";
import { SETTINGS, scratch } from "./helpers.mjs";

const ADAPTERS = JSON.parse(readFileSync(new URL("../adapters.json", import.meta.url), "utf8"));
const MAP = readFileSync(new URL("../permission-map.json", import.meta.url), "utf8");
const GITLEAKS = readFileSync(new URL("../../../.gitleaks.toml", import.meta.url), "utf8");
const S = ".claude/settings.json";
const C = ".cursor/cli.json";

/** A tree with the given sources plus every output the generator renders for them. */
function withOutputs(files) {
  const read = (p) => files[p] ?? null;
  try {
    return { ...files, ...Object.fromEntries(render(ADAPTERS.outputs, read)) };
  } catch {
    return files; // an unmapped entry: D10 reports it, the outputs stay as they were
  }
}
const GOOD = withOutputs({ [S]: JSON.stringify(SETTINGS), "scripts/agents/permission-map.json": MAP, ".gitleaks.toml": GITLEAKS, "scripts/agents/generate.mjs": "// generator\n" });

function view(files, sha = {}) {
  const root = scratch();
  const listing = [];
  for (const [path, text] of Object.entries(files)) {
    if (text === null) continue;
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), text);
    listing.push({ path, mode: "100644", type: "blob", sha: sha[path] ?? "1".repeat(40) });
  }
  return treeView(root, listing);
}
const run = (id, files, baseFiles, sha) => {
  const ctx = { head: view(files, sha), base: baseFiles && view(baseFiles), inputs: ADAPTERS.inputs, manifest: ADAPTERS.outputs, armed: { on: new Set([id]) } };
  return runChecks(ctx, [id])[0];
};
const fails = (id, files, re, b, sha) => {
  const f = run(id, files, b, sha).fails;
  assert.ok(f.some((m) => re.test(m)), `${id}: expected ${re}, got ${JSON.stringify(f)}`);
};
const passes = (id, files, b) => assert.deepEqual(run(id, files, b).fails, [], id);
const settings = (fn) => {
  const s = structuredClone(SETTINGS);
  fn(s.permissions, s);
  return withOutputs({ ...GOOD, [S]: JSON.stringify(s) });
};

test("first introduction: the B1b settings and its generated outputs pass D4, D10, D14, D18", () => {
  for (const id of ["D4", "D10", "D14", "D18"]) passes(id, GOOD);
  passes("D10", {}, null);
});

test("D10 Claude side: MIN_DENY, the H11 bare push, carve-outs, allow and ask caps, the map", () => {
  fails("D10", settings((p) => p.deny.splice(p.deny.indexOf("Bash(*curl*)"), 1)), /deny lacks Bash\(\*curl\*\) \(MIN_DENY\)/);
  fails("D10", settings((p) => p.deny.splice(p.deny.indexOf("Bash(git push *--force-*)"), 1)), /lacks Bash\(git push \*--force-\*\)/);
  fails("D10", settings((p) => p.deny.splice(p.deny.indexOf("Bash(gh -*)"), 1)), /lacks Bash\(gh -\*\)/);
  fails("D10", settings((p) => p.deny.splice(p.deny.indexOf("Bash(git push)"), 1)), /exact Bash\(git push\) \(H11\)/);
  fails("D10", settings((p) => p.deny.push("Read(!**/secrets/x)")), /new ! carve-out loosens/);
  fails("D10", settings((p) => p.allow.push("Bash(rm *)")), /allow Bash\(rm \*\) is not within MAX_ALLOW/);
  fails("D10", settings((p) => p.ask.pop()), /ask lacks Edit\(\.\/scripts\/agents\/\*\*\)/);
  const broadMap = JSON.stringify({ ...JSON.parse(MAP), "Bash(git push *)": "Shell(git:push *)" });
  const broad = (fn) => withOutputs({ ...settings(fn), "scripts/agents/permission-map.json": broadMap });
  passes("D10", broad((p) => p.deny.push("Bash(git push *)")), "a broad Claude deny is fine");
  passes("D10", broad((p) => p.deny.splice(p.deny.indexOf("Bash(git push * main)"), 1, "Bash(git push *)")), "broad covers narrow");
  passes("D10", settings((p) => (p.deny[0] = "Read(.env)")), "bare = **/");
  const unmapped = settings((p) => p.deny.push("Bash(rm -rf *)"));
  fails("D10", unmapped, /Bash\(rm -rf \*\) has no permission-map.json entry/);
  fails("D10", { ...GOOD, "scripts/agents/permission-map.json": "{ x" }, /permission-map.json is not valid JSON/);
  fails("D10", { ...GOOD, [S]: "{ x" }, /settings.json is not valid JSON/);
});

test("D10 against base: head deny ⊇ base, allow ⊆ base, no new carve-out", () => {
  const base = settings((p) => p.deny.push("Bash(npm publish *)"));
  fails("D10", GOOD, /deny lacks Bash\(npm publish \*\) \(on main\)/, base);
  fails("D10", settings((p) => p.allow.push("Bash(git log *)")), /allow Bash\(git log \*\) is not on main/, GOOD);
  passes("D10", settings((p) => p.allow.pop()), GOOD);
});

const cli = (fn) => {
  const c = JSON.parse(GOOD[C]);
  fn(c.permissions);
  return { ...GOOD, [C]: JSON.stringify(c) };
};

test("D10 Cursor side: space form, broad push, MIN_DENY_CURSOR, mapped denies and allows", () => {
  fails("D10", cli((p) => p.deny.push("Shell(gh pr merge)")), /space form/);
  fails("D10", cli((p) => p.deny.push("Shell(git:push)")), /broad push deny \(H7, H11\)/);
  fails("D10", cli((p) => p.deny.push("Shell(git:push*)")), /broad push deny/);
  fails("D10", cli((p) => p.deny.splice(p.deny.indexOf("Shell(curl)"), 1)), /deny lacks Shell\(curl\)/);
  fails("D10", cli((p) => p.allow.push("Shell(git:log *)")), /allow Shell\(git:log \*\) is not within MAX_ALLOW_CURSOR/);
  const extraClaude = withOutputs({ ...GOOD, [S]: JSON.stringify({ ...SETTINGS, permissions: { ...SETTINGS.permissions, deny: [...SETTINGS.permissions.deny, "Bash(npm publish *)"] } }), "scripts/agents/permission-map.json": JSON.stringify({ ...JSON.parse(MAP), "Bash(npm publish *)": "Shell(npm:publish *)" }) });
  passes("D10", extraClaude);
  fails("D10", { ...extraClaude, [C]: GOOD[C] }, /deny lacks the mapped Claude deny Shell\(npm:publish \*\)/);
  const fewer = settings((p) => p.allow.pop());
  fails("D10", { ...fewer, [C]: GOOD[C] }, /allow Shell\(git:diff \*\) is not a mapped Claude allow/);
  passes("D10", { [C]: JSON.stringify({ permissions: { deny: [...MIN_DENY_CURSOR], allow: [] } }) }, "a cli.json-only first introduction against the Cursor caps");
});

test("D14: regen-diff, headers, the lock; supersets when the generator changed", () => {
  fails("D14", { ...GOOD, ".cursorignore": `${GOOD[".cursorignore"]}extra\n` }, /\.cursorignore differs from what the generator renders/);
  fails("D14", { ...GOOD, "scripts/agents/generated.lock.json": "{}\n" }, /generated.lock.json differs/);
  fails("D14", { ...GOOD, ".cursor/cli.json": null }, /cli.json differs/);
  fails("D14", { ...GOOD, "docs/agents/x.md": "<!-- GENERATED from y by scripts/agents/generate.mjs — do not edit -->\n" }, /has a generated header but is not a manifest output/);
  const gen = { "scripts/agents/generate.mjs": "2".repeat(40) };
  const reformatted = { ...GOOD, [C]: JSON.stringify(JSON.parse(GOOD[C])) };
  const r = run("D14", reformatted, GOOD, gen);
  assert.deepEqual(r.fails, [], "a new format that keeps every entry passes");
  assert.match(r.warns[0], /generator changed; supersets checked/);
  const dropDeny = cli((p) => p.deny.splice(p.deny.indexOf("Shell(wget)"), 1));
  fails("D14", dropDeny, /dropped deny Shell\(wget\)/, GOOD, gen);
  fails("D14", { ...GOOD, ".cursorignore": GOOD[".cursorignore"].replace("**/secrets/**\n", "") }, /dropped line \*\*\/secrets\/\*\*/, GOOD, gen);
  const sp = "scripts/agents/hooks/secret-patterns.txt";
  fails("D14", { ...GOOD, [sp]: GOOD[sp].split("\n").filter((l) => !l.startsWith("postgres-uri")).join("\n") }, /dropped or changed rule postgres-uri-password/, GOOD, gen);
  fails("D14", { ...GOOD, [C]: null }, /cli.json is on main and the PR removed it/, GOOD, gen);
  fails("D14", cli((p) => p.allow.push("Shell(git:log *)")), /widened allow Shell\(git:log \*\)/, GOOD, gen);
});

test("D4: .cursor rules only as generated root areas/*.mdc, never alwaysApply: true", () => {
  fails("D4", { ".cursor/rules/x.mdc": "x" }, /only as root \.cursor\/rules\/areas/);
  fails("D4", { "web/.cursor/rules/areas/x.mdc": "x" }, /only as root/);
  fails("D4", { ".cursor/rules/areas/web.mdc": "x" }, /not a generated output/);
  fails("D4", { ".cursor/rules/areas/web.mdc": "---\nalwaysApply: true\n---\n" }, /alwaysApply: true/);
});

const LINE1 = (h) => `if (process.env.CI || process.env.GITHUB_ACTIONS === 'true') { markCiHookRan('${h}'); process.exit(0); }`;
test("D18 harness parts: settings keys, the hook allowlist, the format/quick-test CI line and imports", () => {
  const hooked = (cmd) => settings((p, s) => (s.hooks = { PreToolUse: [{ matcher: "Bash", hooks: [{ type: "command", command: cmd }] }] }));
  passes("D18", hooked("node scripts/agents/hooks/guard.mjs"));
  fails("D18", hooked("node scripts/agents/hooks/guard.mjs && x"), /PreToolUse hook command is not on the allowlist/);
  fails("D18", settings((p, s) => (s.env = { A: "1" })), /key env is not allowed/);
  fails("D18", settings((p, s) => (s.apiKeyHelper = "x")), /key apiKeyHelper/);
  fails("D18", { ".cursor/hooks.json": JSON.stringify({ version: 1, hooks: { beforeShellExecution: [{ command: "sh x" }] } }) }, /\.cursor\/hooks.json: a beforeShellExecution hook command/);
  const fmt = (body) => ({ "scripts/agents/hooks/format.mjs": body });
  const ok = `${LINE1("format")}\nimport { appendFileSync } from "node:fs";\nfunction markCiHookRan(h) { appendFileSync("x", h); }\n`;
  passes("D18", fmt(ok));
  fails("D18", fmt(ok.replace("'format'", "'quick-test'")), /line 1 must be exactly the CI no-op/);
  fails("D18", fmt(`${ok}import x from "./x.mjs";\n`), /only node: imports \(\.\/x\.mjs\)/);
  fails("D18", fmt(`${ok}const y = await import("node:fs");\n`), /no dynamic import/);
  fails("D18", fmt(ok.replace("function markCiHookRan", "const markCiHookRan = function")), /hoisted function declaration/);
  fails("D18", { ".cursor/hooks.json": "{" }, /hooks.json is not valid JSON/);
});

test("covered(): exact, bare = **/, and a broader * entry", () => {
  assert.ok(covered("Read(.env)", ["Read(**/.env)"]));
  assert.ok(covered("Bash(git push * main)", ["Bash(git push *)"]));
  assert.ok(!covered("Bash(git push * main)", ["Bash(git push origin)"]));
  assert.ok(!covered("Read(**/.env)", ["Read(**)"]), "Read/Edit globs only match exactly");
  assert.ok(covered("Shell(curl)", ["Shell(curl)"]));
  assert.equal(renderCursorCli(JSON.stringify(SETTINGS), MAP), GOOD[C]);
});
