import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { INLINE_ALLOWLIST, checkHtml, commandData, checkSite, parseArgs, selfFile } from "./pages-script-check.mjs";

// #355: the published Pages site loads no third-party or untrusted script (ADR-0024's one-origin rule).
const SCRIPT = fileURLToPath(new URL("pages-script-check.mjs", import.meta.url));
const fixture = (name) => fileURLToPath(new URL(`fixtures/${name}`, import.meta.url));
const read = (name) => readFileSync(new URL(`../workflows/${name}`, import.meta.url), "utf8");
const run = (...args) =>
  spawnSync(process.execPath, ["--disable-warning=ExperimentalWarning", "--experimental-vm-modules", SCRIPT, ...args], {
    encoding: "utf8",
  });
const site = (files) => {
  const dir = mkdtempSync(join(tmpdir(), "pages-check-"));
  for (const [name, body] of Object.entries(files)) {
    mkdirSync(join(dir, name, ".."), { recursive: true });
    writeFileSync(join(dir, name), body);
  }
  return dir;
};
const CSP = `<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self'">`;
const page = (body, head = CSP) => `<!doctype html><html><head>${head}<base href="/credit-desk-analytics/"></head><body>${body}</body></html>`;

test("the clean fixture passes: relative scripts, modulepreload, base-path URLs, import-like strings", () => {
  assert.deepEqual(checkSite(fixture("pages-good")), []);
});

test("the failing fixture reports every kind of untrusted script", () => {
  assert.deepEqual(checkSite(fixture("pages-bad")).sort(), [
    `404.html: <script src="../escape.js"> is outside the published site`,
    `404.html: no meta Content-Security-Policy`,
    `broken.js: not parseable as a module (Unexpected identifier 'is')`,
    `index.html: <link rel="modulepreload" href="https://cdn.example.test/preload.js"> is not this site`,
    `index.html: <script src="//cdn.example.test/protocol-relative.js"> is not this site`,
    `index.html: <script src="/other-site/main.js"> is outside /credit-desk-analytics/`,
    `index.html: <script src="https://cdn.example.test/tracker.js"> is not this site`,
    `index.html: <script src="missing.js"> is not a file in the published site`,
    `index.html: CSP script-src must be exactly 'self' (got 'self' 'unsafe-inline' https://cdn.example.test)`,
    `index.html: inline <script> (sha256-dFx2lw7WH12xHS5Wcjn8YALJDpkulJz3VWyb/WbaiqU=) is not in the allowlist`,
    `index.html: inline event handler onload= is inline script`,
    `index.html: javascript: URL at offset 471`,
    `main.js: creates a <script> element`,
    `main.js: dynamic import "https://cdn.example.test/lazy.js" is not a relative file of this site`,
    `main.js: import "https://cdn.example.test/lib.js" is not a relative file of this site`,
    `main.js: import "left-pad" is not a relative file of this site`,
    `main.js: import() with a non-literal specifier ("./chunk.js" + "?v=1"…)`,
    `main.js: import() with a non-literal specifier (name…)`,
    `main.js: importScripts "https://cdn.example.test/worker.js" is not a relative file of this site`,
  ]);
});

test("CLI: exit 0 on the clean fixture, 1 with one ::error:: per finding, 2 on bad usage", () => {
  const ok = run(fixture("pages-good"));
  assert.equal(ok.status, 0, ok.stderr);
  assert.match(ok.stdout, /^No untrusted scripts in /m);
  const bad = run(fixture("pages-bad"));
  assert.equal(bad.status, 1, bad.stderr);
  assert.equal(bad.stdout.match(/^::error::/gm).length, 19);
  assert.match(bad.stdout, /^19 untrusted-script finding\(s\) in /m);
  for (const args of [[], ["--bogus", "x"], ["a", "b"], ["x", "--base"], ["x", "--base", "no-slash"]]) {
    const r = run(...args);
    assert.equal(r.status, 2, `args ${JSON.stringify(args)}`);
    assert.match(r.stderr, /^usage: /);
  }
});

test("CLI: a newline or % in the site's text can't inject a workflow command", () => {
  assert.equal(commandData("a%b\r\n::add-mask::x"), "a%25b%0D%0A::add-mask::x");
  const dir = site({ "index.html": page(`<script src="https://x.test/a.js\n::add-mask::secret%"></script>`) });
  const r = run(dir);
  assert.equal(r.status, 1, r.stderr);
  assert.deepEqual(r.stdout.split("\n").filter((l) => l.startsWith("::")), [
    '::error::index.html: <script src="https://x.test/a.js%0A::add-mask::secret%25"> is not this site',
  ]);
});

test("CLI: refuses to run without --experimental-vm-modules (no parser, no pass)", () => {
  const r = spawnSync(process.execPath, [SCRIPT, fixture("pages-good")], { encoding: "utf8" });
  assert.notEqual(r.status, 0);
  assert.match(r.stderr, /run with node --experimental-vm-modules/);
});

test("parseArgs: dir, --base and --no-csp", () => {
  assert.deepEqual(parseArgs(["d"]), { dir: "d", base: "/credit-desk-analytics/", requireCsp: true });
  assert.deepEqual(parseArgs(["--no-csp", "--base", "/", "d"]), { dir: "d", base: "/", requireCsp: false });
  assert.equal(parseArgs(["--base", "/x", "d"]), null);
});

test("selfFile: only files of the published site are self", () => {
  const root = site({ "a.js": "", "sub/b.js": "" });
  const ctx = { root, fromDir: root, base: "/credit-desk-analytics/" };
  assert.equal(selfFile("a.js", ctx), null);
  assert.equal(selfFile("./sub/b.js#x", ctx), null);
  assert.equal(selfFile("/credit-desk-analytics/sub/b.js", ctx), null);
  assert.equal(selfFile(" ", ctx), "empty URL");
  for (const u of ["https://x.test/a.js", "HTTP://x.test/a.js", "data:text/javascript,1", "blob:x", "//x.test/a.js", "\\\\x\\a.js"]) {
    assert.equal(selfFile(u, ctx), "not this site", u);
  }
  assert.equal(selfFile("/a.js", ctx), "outside /credit-desk-analytics/");
  assert.equal(selfFile("/credit-desk-analytics/../a.js", ctx), "outside the published site");
  assert.equal(selfFile("sub", ctx), null, "a directory inside the site resolves");
  assert.equal(selfFile("nope.js", ctx), "not a file in the published site");
});

test("HTML: a missing CSP fails index.html and 404.html unless --no-csp; other pages don't need one", () => {
  const plain = site({ "index.html": page("", ""), "other.html": page("", "") });
  assert.deepEqual(checkSite(plain), ["index.html: no meta Content-Security-Policy"]);
  assert.deepEqual(checkSite(plain, { requireCsp: false }), []);
  // #278 (#450 nit): every deep link lands on 404.html, so it is an entry point too. A reordered pages.yml step
  // that copies index.html before the CSP is written must fail, not ship a 404.html without one.
  const deepLink = site({ "index.html": page(""), "404.html": page("", ""), "sub/404.html": page("", "") });
  assert.deepEqual(checkSite(deepLink), ["404.html: no meta Content-Security-Policy"]);
  assert.deepEqual(checkSite(deepLink, { requireCsp: false }), []);
  const noScriptSrc = site({ "index.html": page("", `<meta http-equiv=Content-Security-Policy content="default-src 'self'">`) });
  assert.deepEqual(checkSite(noScriptSrc, { requireCsp: false }), ["index.html: CSP script-src must be exactly 'self' (got none)"]);
});

test("HTML: missing index.html, a script with src and a body, an unclosed script", () => {
  const dir = site({ "a.js": "", "x.html": page(`<script src="a.js">alert(1)</script><script src='a.js'`) });
  assert.deepEqual(checkSite(dir).sort(), [
    "index.html: missing",
    'x.html: 2 <script> tags but 1 </script>',
    'x.html: <script src="a.js"> also has an inline body',
  ]);
});

test("HTML: an allowlisted inline script passes, and only that exact body", () => {
  const html = page("<script>1</script>");
  const opts = { file: "/s/index.html", root: "/s", base: "/" };
  const [finding] = checkHtml(html, opts);
  const hash = /\((sha256-[^)]+)\)/.exec(finding)[1];
  INLINE_ALLOWLIST.add(hash);
  try {
    assert.deepEqual(checkHtml(html, opts), []);
    assert.equal(checkHtml(page("<script>2</script>"), opts).length, 1);
  } finally {
    INLINE_ALLOWLIST.delete(hash);
  }
  assert.equal(INLINE_ALLOWLIST.size, 0, "the shipped allowlist is empty");
});

test("HTML: without <base href>, relative URLs resolve from the page's own directory", () => {
  const dir = site({ "index.html": page(""), "a.js": "", "sub/page.html": `<script src="../a.js"></script><script src="a.js"></script>` });
  assert.deepEqual(checkSite(dir), ['sub/page.html: <script src="a.js"> is not a file in the published site']);
});

test("HTML: script preloads are checked, other preloads and links aren't", () => {
  const dir = site({
    "index.html": page(
      `<link rel="preload" as="script" href="https://x.test/a.js"><link rel="preload" as="worker" href="w.js">` +
        `<link rel="preload" as="font" href="https://x.test/f.woff2"><link rel="modulepreload">`,
    ),
  });
  assert.deepEqual(checkSite(dir).sort(), [
    'index.html: <link rel="preload" href="https://x.test/a.js"> is not this site',
    'index.html: <link rel="preload" href="w.js"> is not a file in the published site',
  ]);
});

test("JS: export-from, importScripts with a relative file, and a module that escapes the site", () => {
  const dir = site({
    "index.html": page(""),
    "a.js": `export * from "./b.js"; export { c } from "../outside.js";`,
    "b.js": `importScripts('./a.js'); importScripts(x);`,
    "c.mjs": `import "/credit-desk-analytics/a.js";`,
  });
  assert.deepEqual(checkSite(dir).sort(), [
    'a.js: import "../outside.js" is outside the published site',
    "b.js: importScripts() with a non-literal specifier (x…)",
    'c.mjs: import "/credit-desk-analytics/a.js" is not a relative file of this site',
  ]);
});

const stepBlock = (yml, name) => {
  const start = yml.indexOf(`- name: ${name}`);
  assert.ok(start >= 0, `step "${name}" not found`);
  const rest = yml.slice(start + 1);
  const end = rest.search(/\n {6}- /);
  return end < 0 ? yml.slice(start) : yml.slice(start, start + 1 + end);
};

test("pages.yml: the check runs on the final site (after the CSP and 404.html), before the upload", () => {
  const pages = read("pages.yml");
  const name = "No untrusted scripts in the published site (#355)";
  const step = stepBlock(pages, name);
  assert.match(step, /working-directory: web\/dist\/web\/browser/);
  assert.match(
    step,
    /run: node --disable-warning=ExperimentalWarning --experimental-vm-modules "\$GITHUB_WORKSPACE\/\.github\/scripts\/pages-script-check\.mjs" \. --base \/credit-desk-analytics\/$/m,
  );
  assert.doesNotMatch(step, /continue-on-error|--no-csp|\|\| true/);
  const at = pages.indexOf(`- name: ${name}`);
  assert.ok(pages.indexOf("- name: Meta CSP and the deep-link fallback") < at);
  assert.ok(at < pages.indexOf("uses: actions/upload-pages-artifact@"));
});

test("ci.yml: the web job checks its build on every PR, and gate-tests runs this suite with coverage", () => {
  const ci = read("ci.yml");
  const web = ci.slice(ci.indexOf("\n  web:\n"), ci.indexOf("\n  coverage:\n"));
  const step = stepBlock(web, "No untrusted scripts in the build (#355)");
  // #278 (#450 nit): the check comes from the base checkout, like the classifier, so a PR that edits
  // pages-script-check.mjs cannot relax its own PR check. Never the head copy, and no fallback to it.
  assert.match(step, /^ {10}CHECK=\$GITHUB_WORKSPACE\/_base\/\.github\/scripts\/pages-script-check\.mjs$/m);
  assert.match(
    step,
    /^ {10}node --disable-warning=ExperimentalWarning --experimental-vm-modules "\$CHECK" dist\/web\/browser --base \/ --no-csp$/m,
  );
  assert.match(step, /if \[ ! -f "\$CHECK" \]; then\n {12}echo "::error::[^"]*"\n {12}exit 1\n/);
  assert.doesNotMatch(step, /\.\.\/\.github\/scripts\/pages-script-check\.mjs|continue-on-error|\|\| true/);
  const base = stepBlock(web, "Base checkout for the script check (#278)");
  assert.match(base, /uses: actions\/checkout@[0-9a-f]{40} # v/);
  assert.match(base, /ref: \$\{\{ github\.event\.pull_request\.base\.sha \|\| github\.event\.before \}\}/);
  assert.match(base, /path: _base/);
  assert.match(base, /persist-credentials: false/);
  assert.ok(web.indexOf("- name: Base checkout for the script check (#278)") < web.indexOf("- name: No untrusted scripts in the build (#355)"));
  assert.ok(web.indexOf("- run: npm run build") < web.indexOf("- name: No untrusted scripts in the build (#355)"));
  const gate = stepBlock(ci, "Pages script check tests (#355, ≥80% line/branch)");
  assert.match(gate, /--experimental-vm-modules/);
  assert.match(gate, /--test-coverage-include=\.github\/scripts\/pages-script-check\.mjs/);
  assert.match(gate, /\.github\/scripts\/pages-script-check\.test\.mjs/);
});
