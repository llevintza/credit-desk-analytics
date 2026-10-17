/**
 * Text drift checks (docs/agents/drift-checks.md): D12 (claude-review.yml), D17 (gate text),
 * D20 (workflow guard), D21 (action pins), D23 (links, anchors and the rev 14 planned-marker
 * states) and DEP (the DEP-R1 required-check invariant in the ADR-0023 shape). Line-anchored
 * and best-effort by design: workflow YAML is never parsed or evaluated, only matched.
 */
import { fold, matchesAny } from "./common.mjs";

const WF = ".github/workflows/";
const GOVERNANCE_WF = `${WF}agents-governance.yml`;
const RESERVED = ["agents-drift", "governance-paths", "ci-wiring"];
// The leading space skips the quoted examples in docs/agents/README.md rev 14.
// Split so this file is not itself a hit for the rev 14 `git grep`.
const MARKER = " (planned; arrives with the harness" + " build PR)";
const strip = (l) => l.replace(/^\s+/, "");

/** Lines that are not YAML comments. */
const code = (yml) => yml.split("\n").filter((l) => !/^\s*#/.test(l));

/** Jobs as {id, body: [lines], key(name) -> job-level value (multi-line `>-` joined)}. */
export function jobs(yml) {
  const lines = yml.split("\n");
  const start = lines.findIndex((l) => /^jobs:\s*$/.test(l));
  if (start < 0) return [];
  const out = [];
  for (let i = start + 1; i < lines.length && !/^\S/.test(lines[i]); i++) {
    const m = /^ {2}([^\s#][^:]*):\s*(#.*)?$/.exec(lines[i]);
    if (m) out.push({ id: m[1].replace(/^["']|["']$/g, ""), body: [] });
    else if (out.length) out[out.length - 1].body.push(lines[i]);
  }
  for (const j of out) {
    j.key = (k) => {
      const i = j.body.findIndex((l) => l.startsWith(`    ${k}:`));
      if (i < 0) return undefined;
      const first = j.body[i].slice(k.length + 5).trim();
      if (first && !/^[>|]-?$/.test(first)) return first;
      const rest = [];
      for (let n = i + 1; n < j.body.length && /^ {6}|^\s*$/.test(j.body[n]); n++) rest.push(j.body[n].trim());
      return rest.join(first === "" ? "\n" : " ").trim();
    };
  }
  return out;
}

/**
 * The checks below read workflows by layout, so a file must use the canonical layout or it
 * fails (closed): no quoted keys, anchors, aliases or merge keys; exactly `jobs:` (no trailing comment); job ids at
 * 2 spaces and job keys at exactly 4; no flow-style job, step or job key (but `needs: [..]`).
 * Valid YAML in another layout would otherwise slip past the line-anchored rules (#504 review).
 */
export function layoutProblems(yml) {
  const out = [];
  const lines = yml.split("\n");
  let inJobs = false;
  let firstKey = false;
  let seenId = false;
  lines.forEach((l, n) => {
    const at = `line ${n + 1}`;
    if (/^\s*(#.*)?$/.test(l)) return;
    if (/^\s*(-\s+)?["'][^"']*["']\s*:/.test(l)) out.push(`${at}: quoted mapping key`);
    if (/(^|\s)<<\s*:|:\s+[&*][A-Za-z_]|^\s*-\s+[&*][A-Za-z_]/.test(l)) out.push(`${at}: YAML anchor, alias or merge key`);
    if (/^\s*-\s*[{[]/.test(l)) out.push(`${at}: flow-style sequence item`);
    if (/^\S/.test(l)) {
      inJobs = /^jobs\s*:/.test(l);
      seenId = false;
      if (inJobs && !/^jobs:\s*$/.test(l)) out.push(`${at}: the jobs line must be exactly "jobs:" (no comment, no flow value)`);
      return;
    }
    if (!inJobs) return;
    const indent = /^ */.exec(l)[0].length;
    if (indent === 2) {
      if (!/^ {2}[A-Za-z_][\w-]*:\s*(#.*)?$/.test(l)) out.push(`${at}: a job id must be a plain key at 2 spaces`);
      firstKey = true;
      seenId = true;
      return;
    }
    if (!seenId) out.push(`${at}: the first line under jobs: must be a job id at 2 spaces`);
    if (indent < 4 || indent === 5 || (firstKey && indent !== 4)) out.push(`${at}: job keys must be at exactly 4 spaces`);
    firstKey = false;
    if (indent === 4 && /^ {4}[\w-]+:\s*[{[]/.test(l) && !/^ {4}needs:\s*\[[\w\s,-]*\]\s*$/.test(l)) out.push(`${at}: flow-style job key`);
  });
  return out;
}

/** NFKC, zero-width characters removed, case-folded: the D20 look-alike normalization. */
const norm = (s) => s.normalize("NFKC").replace(/[​-‏⁠﻿]/g, "").toLowerCase();

// D21, verbatim from drift-checks.md (rev 5 regex).
// The selector also catches a quoted key (#504 review), wider than the rev 5 text.
const USES_SELECT = /(^|[\s{,-])["']?uses["']?\s*:/;
const USES_OK = /^\s*(-\s+)?uses:\s+(\.\/[\w./-]+(\s+#.*)?|[\w.-]+\/[\w./-]+@[0-9a-f]{40}\s+#\s*v\d\S*.*)$/;

function governanceFile(yml, r) {
  const c = code(yml);
  const has = (re) => c.some((l) => re.test(l));
  if (!has(/^\s+types: \[opened, synchronize, reopened, edited, ready_for_review\]$/)) r.fail(`${GOVERNANCE_WF}: trigger types must be exactly [opened, synchronize, reopened, edited, ready_for_review]`);
  if (!has(/^\s+branches: \[main\]$/)) r.fail(`${GOVERNANCE_WF}: branches must be [main]`);
  if (!has(/^permissions: \{\}$/)) r.fail(`${GOVERNANCE_WF}: top-level permissions must be {}`);
  if (!has(/^concurrency:/)) r.fail(`${GOVERNANCE_WF}: concurrency is required`);
  for (const [re, what] of [
    [/secrets\./, "secrets."], [/^\s*environment:/, "environment:"], [/id-token/, "id-token"], [/actions\/cache/, "actions/cache"],
    [/upload-artifact/, "upload-artifact"], [/GITHUB_ENV|GITHUB_PATH|GITHUB_OUTPUT/, "GITHUB_ENV/PATH/OUTPUT"], [/^\s*cache:/, "a cache: key"],
    [/\bgit\s+fetch\b/, "git fetch"], [/github\.head_ref|github\.event\.pull_request\.(title|body|head\.ref|head\.label)/, "a PR string expression"],
  ]) if (has(re)) r.fail(`${GOVERNANCE_WF}: ${what} is not allowed`);
  // Leo, 2026-10-17: no PR ref or head SHA anywhere in this file (uses:, with:, env: or run:).
  for (const l of c) if (/head\.sha|head\.ref|head_ref|refs\/pull|merge_commit_sha|^\s+repository:/.test(l)) r.fail(`${GOVERNANCE_WF}: PR ref or head SHA (${strip(l)})`);
  const steps = c.join("\n").split(/\n\s+- (?=uses:|name:|run:)/);
  for (const s of steps) if (/uses: actions\/setup-node@/.test(s) && !/\n\s+package-manager-cache: false\n?/.test(`${s}\n`)) r.fail(`${GOVERNANCE_WF}: a setup-node step lacks package-manager-cache: false`);
  for (const j of jobs(yml)) {
    if (j.key("if") !== undefined) r.fail(`${GOVERNANCE_WF}: job ${j.id} has a job-level if: (a skipped required check counts as passing)`);
    if (j.key("timeout-minutes") === undefined) r.fail(`${GOVERNANCE_WF}: job ${j.id} has no timeout-minutes`);
    const perms = j.key("permissions");
    if (perms !== "contents: read\npull-requests: read") r.fail(`${GOVERNANCE_WF}: job ${j.id} permissions must be exactly contents: read, pull-requests: read`);
  }
}

/** The canonical gate block (D17 a): header + clauses 1–3, leading whitespace and one header "- " stripped. */
function gateAt(lines, i) {
  const block = [strip(lines[i]).replace(/^- /, ""), ...lines.slice(i + 1, i + 4).map(strip)];
  return block.every((l, n) => (n === 0 ? l.startsWith("The review gate (") : l.startsWith(`${n}. `))) ? block.join("\n") : null;
}
const gateLines = (text) => (text ?? "").split("\n").flatMap((l, i) => (/^\s*(- )?The review gate \(/.test(l) ? [i] : []));

/** Lines of a Markdown section from its heading to the next heading of the same or higher level. */
function section(text, heading) {
  const lines = (text ?? "").split("\n");
  const i = lines.findIndex((l) => l.startsWith(heading));
  if (i < 0) return null;
  const level = /^#+/.exec(heading)[0].length;
  const end = lines.findIndex((l, n) => n > i && /^#+ /.test(l) && /^#+/.exec(l)[0].length <= level);
  return lines.slice(i, end < 0 ? lines.length : end);
}

/** GitHub's heading anchor: lower case, punctuation dropped (letters, digits, `-`, `_` kept), spaces to `-`. */
export function slug(heading) {
  return heading
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s_-]/gu, "")
    .replace(/ /g, "-");
}

export function anchors(text) {
  const seen = new Map();
  const out = new Set();
  let fence = false;
  for (const l of (text ?? "").split("\n")) {
    if (/^(```|~~~)/.test(l)) fence = !fence;
    const m = !fence && /^#{1,6} (.+?)\s*#*\s*$/.exec(l);
    if (!m) continue;
    const s = slug(m[1]);
    const n = seen.get(s) ?? 0;
    seen.set(s, n + 1);
    out.add(n ? `${s}-${n}` : s);
  }
  return out;
}

/** Normalize `a/b/../c` against a directory; null if it climbs above the root. */
function join(dir, rel) {
  const parts = dir ? dir.split("/") : [];
  for (const seg of rel.split("/")) {
    if (seg === "..") {
      if (!parts.length) return null;
      parts.pop();
    } else if (seg !== "." && seg !== "") parts.push(seg);
  }
  return parts.join("/");
}

// Rev 14: each marker names one piece; the marker must go once that piece lands.
const PIECES = [
  [/agents-governance\.yml/, "agents-governance.yml", (h) => h.has(GOVERNANCE_WF)],
  [/disableAllHooks/, "disableAllHooks in claude-review.yml", (h) => /"disableAllHooks": true/.test(h.read(`${WF}claude-review.yml`) ?? "")],
  [/guard/, "the guard hook", (h) => h.has("scripts/agents/hooks/guard.mjs")],
  [/\.claude\/agents/, ".claude/agents", (h) => h.glob(".claude/agents/*.md").length > 0],
];

export const TEXT_CHECKS = {
  D12: ({ head }, r) => {
    const yml = head.read(`${WF}claude-review.yml`) ?? "";
    for (const m of ["# agents-drift:restore-step:begin", "# agents-drift:restore-step:end", "# agents-drift:claude-args", "# agents-drift:hook-canary"])
      if (!yml.split("\n").some((l) => strip(l) === m)) r.fail(`claude-review.yml lacks the marker ${m}`);
    if (!yml.includes(`--settings '{"disableAllHooks": true}'`)) r.fail(`claude-review.yml lacks --settings '{"disableAllHooks": true}'`);
    const denied = /--disallowedTools "([^"]*)"/.exec(yml)?.[1].split(",") ?? [];
    for (const t of ["Skill", "Agent", "Task", "Read(./.git/**)", "Grep(./.git/**)", "Glob(./.git/**)"]) if (!denied.includes(t)) r.fail(`claude-review.yml --disallowedTools lacks ${t}`);
    const headings = new Set(head.read("README.md")?.split("\n").flatMap((l) => /^#{2,3} (\d+(?:\.\d+)?)[. ]/.exec(l)?.[1] ?? []));
    for (const p of head.glob(["**/AGENTS.md", ".claude/skills/**/*.md", "docs/agents/**/*.md"]))
      for (const m of (head.read(p) ?? "").matchAll(/README §(\d+(?:\.\d+)?)/g)) if (!headings.has(m[1])) r.fail(`${p} cites README §${m[1]}, which is not a README heading`);
  },

  D17: ({ head, base }, r) => {
    const copies = [];
    const agents = head.read("AGENTS.md");
    const [item6, dod] = gateLines(agents);
    const readme = head.read("README.md");
    const s145 = section(readme, "### 14.5");
    const s15 = section(readme, "## 15.");
    const deploy = head.read("docs/deployment-setup.md");
    const where = [
      ["AGENTS.md item 6", agents?.split("\n"), item6, true],
      ["AGENTS.md DoD", agents?.split("\n"), dod, false],
      ["README §14.5", s145, s145 && gateLines(s145.join("\n"))[0], true],
      ["README §15", s15, s15 && gateLines(s15.join("\n"))[0], true],
      ["docs/deployment-setup.md", deploy?.split("\n"), gateLines(deploy)[0], true],
    ];
    for (const [name, lines, i, rc] of where) {
      const block = lines && i !== undefined ? gateAt(lines, i) : null;
      if (!block) {
        r.fail(`${name}: the gate block (header + clauses 1–3) is missing or malformed`);
        continue;
      }
      copies.push([name, block]);
      if (rc && !(lines[i + 4]?.trim() === "" && strip(lines[i + 5] ?? "").startsWith("Tech Coordinator merges and starts the next phase.")))
        r.fail(`${name}: the block must be followed by one empty line and "Tech Coordinator merges and starts the next phase."`);
      if (name === "AGENTS.md DoD") {
        if (lines[i - 1] !== "- CI is green: build, tests, lint, e2e, budgets (each once it exists).") r.fail("AGENTS.md DoD: the CI-is-green bullet must sit directly above the gate");
        if (!lines.slice(i + 4).some((l) => l.startsWith("- Tech Coordinator merges and starts the next phase."))) r.fail("AGENTS.md DoD: a later bullet must start with Tech Coordinator merges");
      }
    }
    const b145 = base ? section(base.read("README.md"), "### 14.5") : null;
    const want = b145 ? gateAt(b145, gateLines(b145.join("\n"))[0] ?? 0) : base ? null : copies[0]?.[1];
    for (const [name, block] of copies) if (block !== want) r.fail(`${name}: the gate block differs from base README §14.5 (gate changes are break-glass only)`);
  },

  D20: ({ head }, r) => {
    for (const p of head.glob(`${WF}*.y*ml`)) {
      const yml = head.read(p) ?? "";
      const c = code(yml);
      if (!c.some((l) => /^permissions:/.test(l))) r.fail(`${p}: no top-level permissions: block`);
      if (c.some((l) => /statuses:\s*write|checks:\s*write|write-all/.test(l))) r.fail(`${p}: grants statuses/checks write or write-all`);
      for (const m of layoutProblems(yml)) r.fail(`${p}: non-canonical layout (fails closed): ${m}`);
      if (fold(p) === fold(GOVERNANCE_WF)) {
        governanceFile(yml, r);
        continue;
      }
      if (c.some((l) => /pull_request_target/.test(l))) r.fail(`${p}: pull_request_target is allowed only in agents-governance.yml`);
      for (const j of jobs(yml)) {
        const name = j.key("name") ?? "";
        if (name.includes("${{")) r.fail(`${p}: job ${j.id} has an expression in its name`);
        if (RESERVED.some((n) => norm(j.id).includes(n) || norm(name).includes(n))) r.fail(`${p}: job ${j.id} uses a reserved base-check name`);
      }
    }
  },

  D21: ({ head }, r) => {
    for (const p of head.glob([`${WF}agents.yml`, GOVERNANCE_WF]))
      for (const l of code(head.read(p) ?? "")) if (USES_SELECT.test(l) && !USES_OK.test(l)) r.fail(`${p}: unpinned or malformed uses: ${strip(l)}`);
  },

  D23: ({ head, inputs }, r) => {
    const files = head.glob(["**/AGENTS.md", "docs/agents/**/*.md", ".claude/skills/**/*.md"]);
    for (const p of files) {
      const text = head.read(p) ?? "";
      const d = p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "";
      const plain = text.replace(/`[^`\n]*`/g, "");
      // Inline links, and reference-style definitions (`[ref]: path`, #504 nit).
      const targets = [...plain.matchAll(/\]\(([^)\s]+)\)/g), ...plain.matchAll(/^ {0,3}\[[^\]]+\]:\s*<?([^>\s]+)>?/gm)].map((m) => m[1]);
      for (const link of targets) {
        const [target, frag] = link.split("#");
        if (/^[a-z][\w+.-]*:/i.test(target)) continue; // URL or mailto
        const path = target === "" ? p : join(d, target);
        if (path === null || !(head.has(path) || head.paths.some((x) => x.startsWith(`${path}/`)))) {
          r.fail(`${p}: link ${link} does not resolve`);
          continue;
        }
        if (frag && /\.md$/.test(path) && matchesAny(path, inputs) && !anchors(head.read(path)).has(frag)) r.fail(`${p}: anchor #${frag} not found in ${path}`);
      }
      for (const l of text.split("\n").filter((x) => x.includes(MARKER))) {
        const before = l.slice(0, l.indexOf(MARKER));
        const piece = PIECES.map(([re, name, landed]) => [before.search(new RegExp(`${re.source}(?!.*${re.source})`)), name, landed]).filter(([at]) => at >= 0).sort((a, b) => b[0] - a[0])[0];
        if (!piece) r.fail(`${p}: a planned marker that names no known piece: ${strip(l).slice(0, 120)}`);
        else if (piece[2](head)) r.fail(`${p}: ${piece[1]} has landed; remove its "planned" marker`);
      }
    }
    const index = head.read("docs/agents/README.md") ?? "";
    for (const g of head.glob("docs/agents/guidelines/*.md")) if (!index.includes(`(guidelines/${g.split("/").pop()})`)) r.fail(`${g} is not linked from docs/agents/README.md`);
  },

  DEP: ({ head }, r) => {
    const ci = head.read(`${WF}ci.yml`);
    if (ci === null) return r.fail("ci.yml is missing");
    for (const j of jobs(ci)) {
      const cond = j.key("if");
      if (cond === undefined) continue;
      const needs = (j.key("needs") ?? "").replace(/[[\]\s]/g, "").split(",").filter(Boolean);
      const terms = /^\$\{\{([\s\S]*)\}\}$/.exec(cond.replace(/\s+/g, " ").trim())?.[1].split("&&").map((t) => t.trim()) ?? [];
      const flag = `needs.changes.outputs.run_${j.id.replace(/-/g, "_")} != 'false'`;
      const gated = terms.slice(2).map((t) => /^contains\(fromJSON\('\["success", "skipped"\]'\), needs\.([\w-]+)\.result\)$/.exec(t)?.[1]);
      const ok = terms[0] === "!cancelled()" && terms[1] === flag && needs.includes("changes") && !gated.includes(undefined) &&
        gated.slice().sort().join() === needs.filter((n) => n !== "changes").sort().join();
      if (!ok) r.fail(`ci.yml job ${j.id}: a job-level if: must be the exact ADR-0023 shape (!cancelled() && ${flag}, plus success/skipped for each other need)`);
    }
  },
};
