#!/usr/bin/env node
// #355 (Helms, 2026-10-16): enforce ADR-0024's rule "no Pages site on the llevintza account loads untrusted
// third-party scripts" on what pages.yml publishes. Every Pages site on the account shares the SPA's origin, so a
// foreign script there could read the tab's sessionStorage refresh token and pass the API's CORS check.
//
// Usage: node --experimental-vm-modules .github/scripts/pages-script-check.mjs <dir> [--base <path>] [--no-csp]
//   --base: the site's base path (default /credit-desk-analytics/, pages.yml's --base-href).
//   --no-csp: don't require the meta CSP (ci.yml's web job checks the plain build, which has none; a CSP that is
//   there is still checked).
// Fails (exit 1, one ::error:: line per finding) when the published site:
//   - has a <script src> (or a script preload) that is not a file of the site itself;
//   - has an inline <script> body whose sha256 is not in INLINE_ALLOWLIST (empty: Angular emits none);
//   - has an inline event handler (on…=) or a javascript: URL in any HTML file;
//   - has a meta CSP whose script-src is anything but 'self' (pages.yml writes it), or none at all in index.html;
//   - has a JS module that imports anything but a relative file of the site (static, dynamic or importScripts),
//     a dynamic import whose specifier isn't a literal, or creates a <script> element.
// Lightweight on purpose: regexes over Angular's minified output, no dependencies, fail closed.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { pathToFileURL } from "node:url";
import vm from "node:vm";

// node --experimental-vm-modules: without the flag there is no module parser, and the check refuses to run.
const { SourceTextModule } = vm;
if (typeof SourceTextModule !== "function") {
  throw new Error("run with node --experimental-vm-modules (pages-script-check parses each JS file as a module)");
}

// sha256 (base64, CSP style) of each inline script body allowed to ship. Add one only with a review that says why.
export const INLINE_ALLOWLIST = new Set([]);

const walk = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });

const attr = (tag, name) => {
  const m = new RegExp(`\\s${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)'|([^\\s>]+))`, "i").exec(tag);
  return m ? (m[1] ?? m[2] ?? m[3]) : null;
};

// A URL that names a file of the published site: relative (no scheme, not protocol-relative) or under the base
// path, and the file exists in the site. Returns the problem, or null when it's self.
export const selfFile = (url, { root, fromDir, base }) => {
  const u = url.trim();
  if (u === "") return "empty URL";
  if (/^[a-z][a-z0-9+.-]*:/i.test(u) || u.startsWith("//") || u.startsWith("\\")) return "not this site";
  const path = u.split(/[?#]/)[0];
  let file;
  if (path.startsWith("/")) {
    if (!path.startsWith(base)) return `outside ${base}`;
    file = resolve(root, path.slice(base.length));
  } else {
    file = resolve(fromDir, path);
  }
  if (file !== root && !file.startsWith(root + sep)) return "outside the published site";
  if (!existsSync(file)) return "not a file in the published site";
  return null;
};

export function checkHtml(html, { file, root, base, requireCsp = true }) {
  const findings = [];
  const where = relative(root, file);
  // HTML resolves relative URLs against <base href>; pages.yml sets it to the base path, which is the site root.
  const fromDir = attr(/<base\s[^>]*>/i.exec(html)?.[0] ?? "", "href") ? root : dirname(file);
  const ctx = { root, fromDir, base };

  for (const m of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)) {
    const tag = `<script${m[1]}>`;
    const src = attr(tag, "src");
    if (src !== null) {
      const why = selfFile(src, ctx);
      if (why) findings.push(`${where}: <script src="${src}"> is ${why}`);
      if (m[2].trim() !== "") findings.push(`${where}: <script src="${src}"> also has an inline body`);
      continue;
    }
    const hash = `sha256-${createHash("sha256").update(m[2]).digest("base64")}`;
    if (!INLINE_ALLOWLIST.has(hash)) findings.push(`${where}: inline <script> (${hash}) is not in the allowlist`);
  }
  // An opening tag the pattern above didn't close is a script nobody checked.
  const opened = html.match(/<script\b/gi)?.length ?? 0;
  const closed = html.match(/<\/script\s*>/gi)?.length ?? 0;
  if (opened !== closed) findings.push(`${where}: ${opened} <script> tags but ${closed} </script>`);

  for (const m of html.matchAll(/<link\b[^>]*>/gi)) {
    const rel = (attr(m[0], "rel") ?? "").toLowerCase().split(/\s+/);
    const as = (attr(m[0], "as") ?? "").toLowerCase();
    const scripty = rel.includes("modulepreload") || (rel.includes("preload") && /^(script|worker)$/.test(as));
    const href = attr(m[0], "href");
    if (scripty && href !== null) {
      const why = selfFile(href, ctx);
      if (why) findings.push(`${where}: <link rel="${rel.join(" ")}" href="${href}"> is ${why}`);
    }
  }
  for (const m of html.matchAll(/<[a-z][\w-]*\b[^>]*?\s(on[a-z]+)\s*=/gi)) {
    findings.push(`${where}: inline event handler ${m[1]}= is inline script`);
  }
  for (const m of html.matchAll(/=\s*["']?\s*javascript:/gi)) {
    findings.push(`${where}: javascript: URL at offset ${m.index}`);
  }

  const csps = [...html.matchAll(/<meta\b[^>]*http-equiv\s*=\s*["']?content-security-policy[^>]*>/gi)];
  if (csps.length === 0 && requireCsp && where === "index.html") findings.push(`${where}: no meta Content-Security-Policy`);
  for (const m of csps) {
    const policy = attr(m[0], "content") ?? "";
    const scriptSrc = /(?:^|;)\s*script-src\s+([^;]*)/i.exec(policy)?.[1].trim();
    if (scriptSrc !== "'self'") findings.push(`${where}: CSP script-src must be exactly 'self' (got ${scriptSrc ?? "none"})`);
  }
  return findings;
}

export function checkJs(js, { file, root, base }) {
  const findings = [];
  const where = relative(root, file);
  const ctx = { root, fromDir: dirname(file), base };
  const spec = (s, kind) => {
    // Modules import each other by relative path; anything else (a URL, a bare package, an absolute path) isn't self.
    if (!/^\.\.?\//.test(s)) return findings.push(`${where}: ${kind} "${s}" is not a relative file of this site`);
    const why = selfFile(s, ctx);
    if (why) findings.push(`${where}: ${kind} "${s}" is ${why}`);
  };
  // Static imports come from V8's own module parser, so a string that looks like an import isn't one.
  let mod;
  try {
    mod = new SourceTextModule(js, { identifier: where });
  } catch (e) {
    return [`${where}: not parseable as a module (${e.message})`];
  }
  for (const s of mod.moduleRequests?.map((r) => r.specifier) ?? mod.dependencySpecifiers) spec(s, "import");
  // Dynamic loads aren't in that list: match them in the source and fail closed on anything not a plain literal.
  const lit = String.raw`(?:"([^"\n]*)"|'([^'\n]*)'|\x60([^\x60$\n]*)\x60)`;
  for (const m of js.matchAll(/(?<![.\w"'\x60$])(import|importScripts)\s*\(\s*([^)]{0,200})/g)) {
    const arg = new RegExp(String.raw`^${lit}\s*[,)]`).exec(m[2] + ")");
    if (!arg) findings.push(`${where}: ${m[1]}() with a non-literal specifier (${m[2].slice(0, 40)}…)`);
    else if (m[1] === "import") spec(arg[1] ?? arg[2] ?? arg[3], "dynamic import");
    else spec(arg[1] ?? arg[2] ?? arg[3], "importScripts");
  }
  if (/createElement\s*\(\s*["'\x60]script["'\x60]\s*\)/i.test(js)) findings.push(`${where}: creates a <script> element`);
  return findings;
}

export function checkSite(dir, { base = "/credit-desk-analytics/", requireCsp = true } = {}) {
  const root = resolve(dir);
  const files = walk(root);
  const findings = [];
  if (!files.some((f) => relative(root, f) === "index.html")) findings.push("index.html: missing");
  for (const file of files) {
    if (/\.html?$/i.test(file)) findings.push(...checkHtml(readFileSync(file, "utf8"), { file, root, base, requireCsp }));
    else if (/\.(m?js)$/i.test(file)) findings.push(...checkJs(readFileSync(file, "utf8"), { file, root, base }));
  }
  return findings;
}

// Findings quote the site's own text (URLs, specifiers, parser errors): escape it as @actions/core does, so a newline
// in an attribute can't start a workflow command of its own.
export const commandData = (s) => s.replaceAll("%", "%25").replaceAll("\r", "%0D").replaceAll("\n", "%0A");

export function parseArgs(argv) {
  const opts = { dir: null, base: "/credit-desk-analytics/", requireCsp: true };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--no-csp") opts.requireCsp = false;
    else if (argv[i] === "--base" && /^\/(.*\/)?$/.test(argv[i + 1] ?? "")) opts.base = argv[++i];
    else if (!argv[i].startsWith("-") && opts.dir === null) opts.dir = argv[i];
    else return null;
  }
  return opts.dir === null ? null : opts;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const opts = parseArgs(process.argv.slice(2));
  if (!opts) {
    console.error("usage: pages-script-check.mjs <dir> [--base /path/] [--no-csp]");
    process.exit(2);
  }
  const findings = checkSite(opts.dir, opts);
  for (const f of findings) console.log(`::error::${commandData(f)}`);
  console.log(findings.length ? `${findings.length} untrusted-script finding(s) in ${opts.dir}` : `No untrusted scripts in ${opts.dir}.`);
  process.exit(findings.length ? 1 : 0);
}
