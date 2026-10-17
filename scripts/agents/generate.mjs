#!/usr/bin/env node
/**
 * Render the harness adapters from their canonical sources (docs/agents/adapters.md,
 * roles-and-skills.md "Generator and generated headers"). `node:` builtins only.
 *
 *   node scripts/agents/generate.mjs [--check] [--root <dir>]
 *
 * The outputs and their sources are declared in adapters.json ("outputs"). A missing source
 * renders to a missing output. --check writes nothing and exits 1 if any committed output
 * (or generated.lock.json) differs from what the sources render. Sources are DATA: parsed
 * with JSON.parse or line by line, never imported or run.
 */
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { BARE_PUSH } from "./lib/harness-policy.mjs";

export const LOCK = "scripts/agents/generated.lock.json";
const header = (sources) => `# GENERATED from ${sources.join(", ")} by scripts/agents/generate.mjs — do not edit`;

class RenderError extends Error {}

/** Claude permissions -> Cursor CLI permissions (adapters.md mapping table). */
export function renderCursorCli(settingsText, mapText) {
  const perms = JSON.parse(settingsText).permissions ?? {};
  const map = JSON.parse(mapText);
  const one = (entry) => {
    const rw = /^(Read|Edit)\((.*)\)$/.exec(entry);
    if (rw) return rw[2].startsWith("!") ? null : `${rw[1] === "Edit" ? "Write" : "Read"}(${rw[2]})`;
    const web = /^WebFetch\(domain:([^)]+)\)$/.exec(entry);
    if (web) return `WebFetch(${web[1]})`;
    if (entry === BARE_PUSH) return null; // H11: Claude only
    if (!(entry in map)) throw new RenderError(`no permission-map.json entry for ${entry}`);
    return map[entry];
  };
  const list = (xs) => (xs ?? []).map(one).filter((x) => x !== null);
  return `${JSON.stringify({ permissions: { allow: list(perms.allow), deny: list(perms.deny) } }, null, 2)}\n`;
}

/** `.cursorignore` from the Claude `Read(...)` denies; `!` carve-outs are dropped (over-deny). */
export function renderCursorignore(settingsText) {
  const deny = JSON.parse(settingsText).permissions?.deny ?? [];
  const globs = deny.flatMap((e) => /^Read\(([^!].*)\)$/.exec(e)?.[1] ?? []);
  return `${[header([".claude/settings.json"]), ...globs].join("\n")}\n`;
}

/** The guard's secret patterns, one `id<TAB>regex` per `[[rules]]` block of `.gitleaks.toml`. */
export function parseGitleaksRules(toml) {
  const rules = [];
  let cur = null;
  for (const line of toml.split("\n")) {
    if (/^\s*\[\[rules\]\]\s*$/.test(line)) rules.push((cur = {}));
    else if (/^\s*\[/.test(line)) cur = null;
    else if (cur) {
      const m = /^\s*(id|regex)\s*=\s*('''(.*)'''|"((?:[^"\\]|\\.)*)"|'([^']*)')\s*$/.exec(line);
      if (m) cur[m[1]] = m[3] ?? (m[4] !== undefined ? JSON.parse(`"${m[4]}"`) : m[5]);
    }
  }
  for (const r of rules) if (!r.id || !r.regex) throw new RenderError("a .gitleaks.toml rule lacks a one-line id or regex");
  return rules;
}

export function renderSecretPatterns(toml) {
  const rules = parseGitleaksRules(toml);
  return `${[header([".gitleaks.toml"]), ...rules.map((r) => `${r.id}\t${r.regex}`)].join("\n")}\n`;
}

const RENDERERS = {
  "cursor-cli": (src) => renderCursorCli(src[0], src[1]),
  cursorignore: (src) => renderCursorignore(src[0]),
  "secret-patterns": (src) => renderSecretPatterns(src[0]),
};

export const loadManifest = (path = fileURLToPath(new URL("./adapters.json", import.meta.url))) => JSON.parse(readFileSync(path, "utf8")).outputs;

/**
 * sha256 over each source's path and bytes, in manifest order (the D14 lock entry). The
 * `sha256:` prefix keeps a bare 64-hex value off gitleaks' generic-api-key rule.
 */
export const sourceHash = (sources, read) =>
  `sha256:${createHash("sha256").update(sources.map((s) => `${s}\0${read(s)}\0`).join("")).digest("hex")}`;

/**
 * Render every output whose sources all exist. `read(path)` returns text or null; it is the
 * only way the generator sees the tree, so the base job can pass PR data in.
 */
export function render(manifest, read) {
  const files = new Map();
  const lock = {};
  for (const o of manifest) {
    const src = o.sources.map(read);
    if (src.some((s) => s === null)) continue;
    const fn = RENDERERS[o.render];
    if (!fn) throw new RenderError(`unknown renderer ${o.render}`);
    files.set(o.path, fn(src));
    lock[o.path] = sourceHash(o.sources, read);
  }
  if (files.size) files.set(LOCK, `${JSON.stringify(lock, null, 2)}\n`);
  return files;
}

/** Outputs (and the lock) whose committed text differs from the rendering, or that should not exist. */
export function diff(manifest, read) {
  const want = render(manifest, read);
  const paths = [...manifest.map((o) => o.path), LOCK];
  return paths.filter((p) => (want.get(p) ?? null) !== read(p));
}

export function main(argv, { write = (s) => process.stdout.write(s) } = {}) {
  try {
    const i = argv.indexOf("--root");
    const root = resolve(i >= 0 ? argv[i + 1] : ".");
    const read = (p) => (existsSync(join(root, p)) ? readFileSync(join(root, p), "utf8") : null);
    const manifest = loadManifest();
    if (argv.includes("--check")) {
      const stale = diff(manifest, read);
      for (const p of stale) write(`::error::generated file out of date: ${p} (run node scripts/agents/generate.mjs)\n`);
      if (!stale.length) write("agents generate: every generated file is up to date\n");
      return stale.length ? 1 : 0;
    }
    const want = render(manifest, read);
    for (const p of [...manifest.map((o) => o.path), LOCK]) {
      const target = join(root, p);
      if (want.has(p)) {
        mkdirSync(dirname(target), { recursive: true });
        writeFileSync(target, want.get(p));
      } else rmSync(target, { force: true });
    }
    write(`agents generate: wrote ${want.size} files\n`);
    return 0;
  } catch (err) {
    write(`::error::agents generate: ${err instanceof RenderError || err instanceof SyntaxError ? err.message : "unexpected error"}\n`);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
