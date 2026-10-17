/**
 * Harness adapter checks (docs/agents/drift-checks.md): D4 (.cursor rules), D10 (Claude vs
 * Cursor permissions), D14 (generated outputs: lock, headers, regen-diff, and the R3-F4
 * supersets when the PR changes the generator) and D18's settings and hook parts. The
 * generator that renders here is the RUNNING checker's (main's, in the base job); the PR's
 * settings, map and sources are data passed through `head.read`.
 */
import { LOCK, render, renderCursorCli } from "../generate.mjs";
import {
  ASK, BARE_PUSH, CLAUDE_CARVE_OUTS, CURSOR_FORBIDDEN_DENY, HOOK_ALLOWLIST, MAX_ALLOW, MAX_ALLOW_CURSOR, MIN_DENY, MIN_DENY_CURSOR,
  SETTINGS_FORBIDDEN_KEYS,
} from "./harness-policy.mjs";

const SETTINGS = ".claude/settings.json";
const CLI = ".cursor/cli.json";
const GENERATOR = "scripts/agents/generate.mjs";
const CI_LINE = (h) => `if (process.env.CI || process.env.GITHUB_ACTIONS === 'true') { markCiHookRan('${h}'); process.exit(0); }`;

const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);

/**
 * A present file must parse to a plain object: `null`, `false`, `0`, `""` or an array would
 * otherwise read as "no settings" and skip every rule (#513 review). Absent is null.
 */
const json = (view, p, r) => {
  const t = view?.read(p) ?? null;
  if (t === null) return null;
  let v;
  try {
    v = JSON.parse(t);
  } catch {
    r.fail(`${p} is not valid JSON`);
    return undefined;
  }
  if (!isObj(v)) {
    r.fail(`${p} must be a JSON object`);
    return undefined;
  }
  return v;
};
/** The permission lists, as string arrays; a malformed list reads as empty (shape() fails it). */
const perms = (s) => {
  const p = isObj(s?.permissions) ? s.permissions : {};
  const list = (k) => (Array.isArray(p[k]) ? p[k].filter((e) => typeof e === "string") : []);
  return { allow: list("allow"), ask: list("ask"), deny: list("deny") };
};

// D18 allowlists (#513 review): any other settings key fails closed, so a new Claude Code key
// that runs code or loosens permissions (defaultMode, additionalDirectories, enabledPlugins,
// awsCredentialExport, enableAllProjectMcpServers, …) needs a reviewed checker change first.
const SETTINGS_KEYS = { "": ["$schema", "permissions", "hooks"], permissions: ["allow", "ask", "deny"] };
const CLI_KEYS = { "": ["permissions"], permissions: ["allow", "deny"] };

/** Only the allowlisted keys, and each permission list an array of strings. */
function shape(v, p, keys, r) {
  for (const k of Object.keys(v)) if (!keys[""].includes(k)) r.fail(`${p}: key ${k} is not allowed`);
  if (v.permissions === undefined) return;
  if (!isObj(v.permissions)) return r.fail(`${p}: permissions must be an object`);
  for (const [k, list] of Object.entries(v.permissions)) {
    if (!keys.permissions.includes(k)) r.fail(`${p}: permissions.${k} is not allowed`);
    else if (!Array.isArray(list) || list.some((e) => typeof e !== "string")) r.fail(`${p}: permissions.${k} must be an array of strings`);
  }
}

/** Bare and `**\/` forms of a Read/Edit glob are the same rule (Helms/CR ruling 2). */
const norm = (e) => e.replace(/^(Read|Edit|Write)\((!?)(?!\*\*\/)([^)]*)\)$/, "$1($2**/$3)");

/**
 * `e` is in `set`, or a broader prefix entry covers it: `Tool(prefix *)` covers every
 * `Tool(prefix …)`. Only a trailing ` *` counts, the form whose meaning is plain in every
 * harness; a mid-string `*` never covers anything (fail closed, and linear: #513 review).
 * Read/Edit/Write globs match exactly.
 */
export function covered(e, set) {
  const n = norm(e);
  return set.some((s) => {
    const x = norm(s);
    if (x === n) return true;
    const m = /^(Bash|Shell)\(([^*]*) \*\)$/.exec(x);
    return m !== null && n.startsWith(`${m[1]}(${m[2]} `);
  });
}

const missing = (need, have) => need.filter((e) => !covered(e, have));
const extra = (have, cap) => have.filter((e) => !cap.some((c) => norm(c) === norm(e)));

function claudeChecks(head, base, map, r) {
  const s = json(head, SETTINGS, r);
  if (s === undefined) return;
  if (s === null) {
    if (base?.has(SETTINGS)) r.fail(`${SETTINGS} is on main and missing from the PR`);
    return;
  }
  shape(s, SETTINGS, SETTINGS_KEYS, r);
  const b = json(base, SETTINGS, r);
  const { deny = [], allow = [], ask = [] } = perms(s);
  const carve = deny.filter((e) => /^\w+\(!/.test(e));
  const denies = deny.filter((e) => !carve.includes(e));
  const okCarve = b ? perms(b).deny.filter((e) => /^\w+\(!/.test(e)) : CLAUDE_CARVE_OUTS;
  for (const e of carve) if (!okCarve.includes(e)) r.fail(`${SETTINGS}: a new ! carve-out loosens the deny list: ${e}`);
  const floor = b ? perms(b).deny.filter((e) => !/^\w+\(!/.test(e)) : MIN_DENY;
  for (const e of missing(floor, denies)) r.fail(`${SETTINGS}: deny lacks ${e} (${b ? "on main" : "MIN_DENY"})`);
  if (!deny.includes(BARE_PUSH)) r.fail(`${SETTINGS}: deny lacks the exact ${BARE_PUSH} (H11)`);
  for (const e of extra(allow, b ? perms(b).allow : MAX_ALLOW)) r.fail(`${SETTINGS}: allow ${e} is not ${b ? "on main" : "within MAX_ALLOW"}`);
  for (const e of missing(b ? perms(b).ask : ASK, ask)) r.fail(`${SETTINGS}: ask lacks ${e}`);
  for (const e of [...deny, ...allow]) if (/^Bash\(/.test(e) && e !== BARE_PUSH && !(map && e in map)) r.fail(`${SETTINGS}: ${e} has no permission-map.json entry`);
  return s;
}

function cursorChecks(head, base, settings, mapText, r) {
  const c = json(head, CLI, r);
  if (c === undefined) return;
  if (c === null) {
    if (base?.has(CLI)) r.fail(`${CLI} is on main and missing from the PR`);
    return;
  }
  shape(c, CLI, CLI_KEYS, r);
  const b = json(base, CLI, r);
  const { deny = [], allow = [] } = perms(c);
  for (const e of [...deny, ...allow]) if (/^Shell\([^:)]* /.test(e)) r.fail(`${CLI}: ${e} uses the space form; only Shell(cmd:args) (U9)`);
  for (const e of CURSOR_FORBIDDEN_DENY) if (deny.includes(e)) r.fail(`${CLI}: ${e} is a broad push deny (H7, H11)`);
  for (const e of missing(b ? perms(b).deny : MIN_DENY_CURSOR, deny)) r.fail(`${CLI}: deny lacks ${e} (${b ? "on main" : "MIN_DENY_CURSOR"})`);
  for (const e of extra(allow, b ? perms(b).allow : MAX_ALLOW_CURSOR)) r.fail(`${CLI}: allow ${e} is not ${b ? "on main" : "within MAX_ALLOW_CURSOR"}`);
  if (settings && mapText !== null) {
    let mapped;
    try {
      mapped = JSON.parse(renderCursorCli(JSON.stringify(settings), mapText)).permissions;
    } catch {
      return; // the missing map entry is reported by the Claude checks
    }
    for (const e of missing(mapped.deny, deny)) r.fail(`${CLI}: deny lacks the mapped Claude deny ${e}`);
    for (const e of extra(allow, mapped.allow)) r.fail(`${CLI}: allow ${e} is not a mapped Claude allow`);
  }
}

const lines = (t) => (t ?? "").split("\n").filter((l) => l && !l.startsWith("#"));
const ids = (t) => new Map(lines(t).map((l) => [l.slice(0, l.indexOf("\t")), l.slice(l.indexOf("\t") + 1)]));

/** R3-F4: when the PR changes the generator, its outputs may change format but never drop an entry. */
function superset(path, committed, rendered, onMain, r) {
  if (path === CLI) {
    const c = JSON.parse(committed).permissions;
    for (const src of [rendered, onMain].filter(Boolean)) {
      const want = JSON.parse(src).permissions;
      for (const e of missing(want.deny, c.deny)) r.fail(`${path}: the changed generator dropped deny ${e}`);
      for (const e of extra(c.allow, want.allow)) r.fail(`${path}: the changed generator widened allow ${e}`);
    }
  } else if (path.endsWith("secret-patterns.txt")) {
    const c = ids(committed);
    for (const [id, re] of [...ids(onMain), ...ids(rendered)]) if (c.get(id) !== (ids(rendered).get(id) ?? re)) r.fail(`${path}: the changed generator dropped or changed rule ${id}`);
  } else for (const l of [...lines(rendered), ...lines(onMain)]) if (!lines(committed).includes(l)) r.fail(`${path}: the changed generator dropped line ${l}`);
}

export const HARNESS_CHECKS = {
  D4: ({ head, manifest }, r) => {
    for (const p of head.glob(["**/.cursor/rules/**"])) {
      if (!/^\.cursor\/rules\/areas\/[^/]+\.mdc$/.test(p)) r.fail(`${p}: .cursor rules may exist only as root .cursor/rules/areas/*.mdc`);
      else if (!manifest.some((o) => o.path === p)) r.fail(`${p} is not a generated output (cursorAreaRules is off)`);
      // Any alwaysApply other than a plain `false` fails: True, yes, "true", `alwaysApply :` (#513 nit).
      for (const m of (head.read(p) ?? "").matchAll(/^\s*["']?alwaysApply["']?\s*:\s*(.*?)\s*(#.*)?$/gim))
        if (m[1] !== "false") r.fail(`${p}: alwaysApply must be false`);
    }
  },

  D10: ({ head, base }, r) => {
    const mapText = head.read("scripts/agents/permission-map.json");
    let map = null;
    try {
      map = mapText === null ? null : JSON.parse(mapText);
    } catch {
      r.fail("scripts/agents/permission-map.json is not valid JSON");
    }
    const settings = claudeChecks(head, base, map, r);
    cursorChecks(head, base, settings, mapText, r);
  },

  D14: ({ head, base, manifest, inputs }, r) => {
    const outputs = new Set([...manifest.map((o) => o.path), LOCK]);
    for (const p of head.glob(inputs)) {
      const first = (head.read(p) ?? "").split("\n", 1)[0];
      if (/^(#|<!--) GENERATED from /.test(first) && !outputs.has(p)) r.fail(`${p} has a generated header but is not a manifest output`);
    }
    if (base?.has(GENERATOR) && !head.has(GENERATOR)) return r.fail(`${GENERATOR} is on main and missing from the PR`);
    const changed = base && head.entry(GENERATOR)?.sha !== base.entry(GENERATOR)?.sha;
    const want = render(manifest, head.read);
    for (const p of outputs) {
      const committed = head.read(p);
      if (!changed) {
        if ((want.get(p) ?? null) !== committed) r.fail(`${p} differs from what the generator renders (run node scripts/agents/generate.mjs)`);
        continue;
      }
      const onMain = base.read(p);
      if (onMain !== null && committed === null) r.fail(`${p} is on main and the PR removed it`);
      if (committed === null || p === LOCK) continue;
      if (want.has(p) || onMain !== null) superset(p, committed, want.get(p) ?? null, onMain, r);
      if ((want.get(p) ?? null) !== committed) r.warn(`${p} differs from main's generator output (generator changed; supersets checked)`);
    }
  },
};

/**
 * D18's harness parts (always armed): no risky settings keys, every hook command on the exact
 * allowlist (settings.json and .cursor/hooks.json), and in format/quick-test the CI no-op on
 * line 1 with only `node:` static imports (R3-N5, R4-N7).
 */
export function d18Harness(head, r) {
  const s = json(head, SETTINGS, r);
  if (s) {
    for (const k of SETTINGS_FORBIDDEN_KEYS) if (k in s) r.fail(`${SETTINGS}: key ${k} is not allowed`);
    if (s.hooks !== undefined && !isObj(s.hooks)) r.fail(`${SETTINGS}: hooks must be an object`);
    for (const [event, groups] of Object.entries(s.hooks ?? {}))
      for (const g of [].concat(groups))
        for (const h of [].concat(g?.hooks ?? [])) if (!HOOK_ALLOWLIST.includes(h?.command)) r.fail(`${SETTINGS}: a ${event} hook command is not on the allowlist`);
  }
  const ch = json(head, ".cursor/hooks.json", r);
  for (const [event, list] of Object.entries(ch?.hooks ?? {}))
    for (const h of [].concat(list)) if (!HOOK_ALLOWLIST.includes(h?.command)) r.fail(`.cursor/hooks.json: a ${event} hook command is not on the allowlist`);
  for (const h of ["format", "quick-test"]) {
    const p = `scripts/agents/hooks/${h}.mjs`;
    const t = head.read(p);
    if (t === null) continue;
    if (t.split("\n", 1)[0] !== CI_LINE(h)) r.fail(`${p}: line 1 must be exactly the CI no-op statement`);
    if (!/^function markCiHookRan\(/m.test(t)) r.fail(`${p}: markCiHookRan must be a hoisted function declaration`);
    for (const m of t.matchAll(/^\s*import\b[^;]*?from\s+["']([^"']+)["']|^\s*import\s+["']([^"']+)["']/gm))
      if (!(m[1] ?? m[2]).startsWith("node:")) r.fail(`${p}: only node: imports (${m[1] ?? m[2]})`);
    if (/\bimport\s*\(/.test(t)) r.fail(`${p}: no dynamic import()`);
  }
}
