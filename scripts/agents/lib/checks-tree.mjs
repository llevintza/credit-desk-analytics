/**
 * Tree-shape drift checks (docs/agents/drift-checks.md): D1–D3, D5–D9, D8a, D11, D13, D15,
 * D16 (imports and stubs), D18 (frontmatter and .mcp.json), D19, D22 and the presence rule P.
 * Each check gets the context from check-drift.mjs and reports through fail()/warn().
 */
import { CheckError, fail, fold, matchesAny } from "./common.mjs";
import { d18Harness } from "./checks-harness.mjs";
import { render } from "../generate.mjs";

const STUB = "@AGENTS.md\n";
const base = (p) => p.slice(p.lastIndexOf("/") + 1);
const dir = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/") + 1) : "");
const lineCount = (t) => t.split("\n").length - (t.endsWith("\n") ? 1 : 0);
const MEMORY_NAMES = new Set(["agents.md", "claude.md", "agent.md"]);

/** `---` frontmatter as {key: raw value}; only top-level `key:` lines matter here. */
export function frontmatter(text) {
  const m = /^---\n([\s\S]*?)\n---(?:\n|$)/.exec(text ?? "");
  if (!m) return null;
  const out = {};
  for (const l of m[1].split("\n")) {
    const kv = /^([A-Za-z][\w-]*):\s?(.*)$/.exec(l);
    if (kv) out[kv[1]] = kv[2].replace(/^"(.*)"$/, "$1").replace(/^'(.*)'$/, "$1");
  }
  return out;
}

// Check ids: D<n> with an optional letter suffix, or one of the named checks.
export const CHECK_ID = /^(D\d{1,2}[a-z]?|P|DEP)$/;

/**
 * arming.json must be exactly {alwaysArmed: [id…], subjects: {id: [glob…]}}: arrays of
 * non-empty strings, ids that match `known` (a Set or a RegExp), no duplicates, no other
 * keys. Anything else throws, so a string-for-array rewrite can't turn `includes` into a
 * substring test or arm single characters (#502 review). An unknown id goes in the error's
 * detail, never its message, so main() prints it only escaped (#505).
 */
export function validateArming(a, known) {
  const isKnown = (id) => (known instanceof RegExp ? known.test(id) : known.has(id));
  const strings = (xs, what) => {
    if (!Array.isArray(xs) || xs.some((x) => typeof x !== "string" || x === "") || new Set(xs).size !== xs.length)
      throw new CheckError(`arming.json: ${what} must be an array of distinct non-empty strings`);
    return xs;
  };
  if (typeof a !== "object" || a === null || Array.isArray(a)) throw new CheckError("arming.json must be an object");
  const keys = Object.keys(a).sort().join();
  if (keys !== "alwaysArmed,subjects") throw new CheckError("arming.json must have exactly alwaysArmed and subjects");
  for (const id of strings(a.alwaysArmed, "alwaysArmed")) if (!isKnown(id)) fail("arming.json: unknown check id", id);
  if (typeof a.subjects !== "object" || a.subjects === null || Array.isArray(a.subjects)) throw new CheckError("arming.json: subjects must be an object");
  for (const [id, globs] of Object.entries(a.subjects)) {
    if (!isKnown(id)) fail("arming.json: unknown check id", id);
    strings(globs, `subjects.${id}`);
  }
  return a;
}

/** Drop fenced code blocks and inline code spans before looking for `@path` imports (D16). */
const prose = (t) => t.replace(/^(```|~~~)[\s\S]*?^\1/gm, "").replace(/`[^`\n]*`/g, "");

export const TREE_CHECKS = {
  D1: ({ head }, r) => {
    if (head.read("CLAUDE.md") !== STUB) r.fail("root CLAUDE.md must be exactly @AGENTS.md and a newline");
  },

  D2: ({ head }, r) => {
    const nested = head.paths.filter((p) => p.includes("/") && (base(p) === "AGENTS.md" || base(p) === "CLAUDE.md"));
    for (const p of nested) {
      const sibling = dir(p) + (base(p) === "AGENTS.md" ? "CLAUDE.md" : "AGENTS.md");
      if (!head.has(sibling)) r.fail(base(p) === "AGENTS.md" ? `${p} has no sibling CLAUDE.md stub` : `orphan stub ${p}`);
    }
  },

  D3: ({ head }, r) => {
    for (const p of head.paths) {
      const f = fold(p);
      if (MEMORY_NAMES.has(fold(base(p))) && base(p) !== "AGENTS.md" && base(p) !== "CLAUDE.md") r.fail(`forbidden memory-file name ${p}`);
      if (fold(base(p)) === "claude.local.md") r.fail(`committed ${p}`);
      if (f === ".claude/claude.md") r.fail(`${p} is not allowed (root stub only)`);
      if (f === ".claude/settings.local.json") r.fail(`committed ${p} (local Claude settings merge above settings.json)`);
      if (/(^|\/)\.claude\//.test(f) && !f.startsWith(".claude/")) r.fail(`nested .claude/ directory: ${p}`);
    }
  },

  D5: ({ head }, r) => {
    for (const p of head.glob(".claude/rules/**")) r.fail(`.claude/rules/ must be absent: ${p}`);
  },

  D6: ({ head }, r) => {
    for (const p of head.glob([".github/copilot-instructions.md", ".github/instructions/**", ".github/prompts/**"])) r.fail(`Copilot instruction file ${p}`);
  },

  D7: ({ head, manifest = [] }, r) => {
    for (const p of head.glob(".grok/**")) if (!matchesAny(p, [".grok/agents/*.md"])) r.fail(`.grok/ must be absent (Grok not enabled): ${p}`);
    // H8: only the byte-exact generated .cursor/hooks.json may exist (the running checker's
    // generator renders it from the PR's manifest and sources; fallback (ii) drops the output).
    const hooks = head.glob(".cursor/hooks.json");
    if (!hooks.length) return;
    let want;
    try {
      want = render(manifest, head.read);
    } catch {
      want = new Map(); // a renderer the running generator doesn't know: nothing is generated
    }
    for (const p of hooks) if (!want.has(p) || want.get(p) !== head.read(p)) r.fail(`hand-written ${p} (only the generated file is allowed)`);
  },

  D8: ({ head }, r) => {
    let total = 0;
    const names = new Set();
    for (const p of head.glob(".claude/skills/*/SKILL.md")) {
      const folder = p.split("/")[2];
      const fm = frontmatter(head.read(p));
      if (!fm) {
        r.fail(`${p} has no frontmatter`);
        continue;
      }
      if (fm.name !== folder) r.fail(`${p}: name must equal the folder name`);
      const len = (fm.description ?? "").length;
      if (!len || len > 200) r.fail(`${p}: description must be 1–200 characters (is ${len})`);
      total += len;
      names.add(fold(folder));
    }
    if (total > 1500) r.fail(`skill descriptions total ${total} characters (max 1500)`);
    for (const p of head.glob([".cursor/skills/*/**", ".agents/skills/*/**", ".codex/skills/*/**", ".grok/skills/*/**"]))
      if (names.has(fold(p.split("/")[2]))) r.fail(`${p} shadows the .claude skill of the same name`);
    for (const p of head.glob([".claude/commands/**", ".cursor/commands/**"])) r.fail(`legacy command file ${p} (use skills)`);
  },

  D8a: ({ head }, r) => {
    const doc = head.read("docs/agents/roles-and-skills.md");
    const section = /^### 4\. Commands and skills[\s\S]*?(?=^#{1,3} |(?![\s\S]))/m.exec(doc ?? "");
    if (!section) return r.fail("docs/agents/roles-and-skills.md has no '### 4. Commands and skills' section");
    const table = new Set([...section[0].matchAll(/^\| `([\w-]+)` \|/gm)].map((m) => m[1]));
    const folders = new Set(head.glob(".claude/skills/*/SKILL.md").map((p) => p.split("/")[2]));
    for (const n of table) if (!folders.has(n)) r.fail(`skill table names ${n}, but .claude/skills/${n}/ is missing`);
    for (const n of folders) if (!table.has(n)) r.fail(`.claude/skills/${n}/ is missing from the roles-and-skills.md §4 table`);
  },

  D9: ({ head }, r) => {
    const seen = new Map();
    for (const p of head.glob(".claude/agents/*.md")) {
      const name = frontmatter(head.read(p))?.name;
      if (!name) r.fail(`${p} has no name`);
      else if (seen.has(fold(name))) r.fail(`subagent name ${name} is used by ${seen.get(fold(name))} and ${p}`);
      else seen.set(fold(name), p);
    }
    for (const p of head.glob(".cursor/agents/**")) r.fail(`.cursor/agents/ must be absent: ${p}`);
  },

  D11: ({ head }, r) => {
    for (const p of head.paths) if (fold(p) === ".claudeignore") r.fail(`${p} must be absent`);
  },

  D13: ({ head }, r) => {
    const root = lineCount(head.read("AGENTS.md") ?? "") + lineCount(head.read("CLAUDE.md") ?? "");
    if (root > 150) r.fail(`root metric ${root} lines (max 150)`);
    else if (root > 130) r.warn(`root metric ${root} lines (warn above 130)`);
    for (const p of head.paths.filter((x) => x.includes("/") && base(x) === "AGENTS.md")) {
      const n = lineCount(head.read(p) ?? "");
      if (n > 100) r.fail(`${p}: ${n} lines (max 100)`);
      else if (n > 60) r.warn(`${p}: ${n} lines (warn above 60)`);
    }
  },

  // Every symlink fails: a linked directory would hide the steering files behind it (#498).
  D15: ({ head, base: b }, r) => {
    const baseSubs = new Map((b?.entries ?? []).filter((e) => e.mode === "160000").map((e) => [e.path, e.sha]));
    for (const e of head.entries) {
      if (e.mode === "120000") r.fail(`symlink in the tree (none are allowed): ${e.path}`);
      if (e.mode === "160000" && baseSubs.get(e.path) !== e.sha) r.fail(`new or changed submodule ${e.path}`);
    }
  },

  D16: ({ head }, r) => {
    for (const p of head.glob(["**/CLAUDE.md"])) if (head.read(p) !== STUB) r.fail(`${p} must be exactly @AGENTS.md and a newline`);
    for (const p of head.glob(["**/AGENTS.md", "docs/agents/**/*.md"]))
      for (const m of prose(head.read(p) ?? "").matchAll(/(?:^|\s)@([\w./-]+)/g)) {
        const target = m[1].replace(/\.+$/, ""); // a sentence's full stop is not part of the path
        if (/[./]/.test(target)) r.fail(`${p}: @ import "@${target}" outside a code span`);
      }
  },

  D18: ({ head }, r) => {
    d18Harness(head, r);
    for (const p of head.glob([".claude/skills/**/*.md", ".claude/agents/**/*.md"])) {
      const fm = frontmatter(head.read(p)) ?? {};
      for (const k of ["hooks", "allowed-tools"]) if (k in fm) r.fail(`${p}: frontmatter key ${k}: is not allowed`);
    }
    const mcp = head.read(".mcp.json");
    if (mcp !== null) {
      const servers = Object.keys(JSON.parse(mcp).mcpServers ?? {});
      for (const s of servers) r.fail(`.mcp.json server ${s} is not on the checker's allowlist`);
    }
  },

  D19: ({ head }, r) => {
    for (const p of head.paths) if (/(^|\/)\.env($|\.)/i.test(p) && base(p) !== ".env.example") r.fail(`tracked env file ${p}`);
  },

  D22: ({ head, base: b, arming }, r) => {
    const text = head.read("scripts/agents/arming.json");
    if (text === null) return r.fail("scripts/agents/arming.json is missing (missing is a fail, never empty)");
    let pr;
    try {
      pr = validateArming(JSON.parse(text), CHECK_ID);
    } catch (e) {
      return r.fail(`the PR's arming.json is malformed: ${e.message}${e.detail === undefined ? "" : ` ${JSON.stringify(e.detail)}`}`);
    }
    const main = b ? validateArming(JSON.parse(b.read("scripts/agents/arming.json")), CHECK_ID) : arming;
    for (const id of main.alwaysArmed) if (!pr.alwaysArmed.includes(id)) r.fail(`arming.json drops always-armed ${id}`);
    for (const [id, globs] of Object.entries(main.subjects))
      for (const g of globs) if (!(pr.subjects[id] ?? []).includes(g)) r.fail(`arming.json drops subject ${g} of ${id}`);
  },

  P: ({ head, base: b, arming }, r) => {
    if (!b) return;
    const subjects = Object.values(arming.subjects).flat();
    for (const p of b.glob(subjects)) if (!head.hasFolded(p)) r.fail(`${p} is on main and missing from the PR (removal is break-glass only)`);
  },
};
