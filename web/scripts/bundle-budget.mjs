#!/usr/bin/env node
// README §10: initial JS < 500 KB compressed — CI fails above it (#76). angular.json budgets measure raw bytes;
// this measures what the browser downloads: Brotli at the quality the API serves (BROTLI_QUALITY) of every script and
// stylesheet the built index.html loads up front. Lazy chunks (the AG Grid page) are reported, not budgeted.
// Fails closed (#145): anything that would leave the budget unchecked exits 1 instead of passing.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, posix, resolve, sep } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';

const dir = process.argv[2] ?? 'dist/web/browser';
const limitKb = 500;
// The quality the API serves (#203): src/Desk.Api/Program.cs sets BrotliCompressionProviderOptions.Level =
// CompressionLevel.Fastest, which .NET maps to Brotli quality 1. Change both together. q1 output is 9–18% larger
// than q4 on minified bundles, so measuring at a higher quality would under-report the wire bytes.
const BROTLI_QUALITY = 1;
const fail = (message) => {
  console.error(`FAIL: ${message}`);
  process.exit(1);
};

// index.html paths may not leave the dist dir (`../…`); only files the build emitted are measured.
const root = resolve(dir);
const read = (file) => {
  const path = resolve(root, file);
  if (!path.startsWith(root + sep)) fail(`${file} resolves outside ${root}; refusing to read it.`);
  if (!existsSync(path)) fail(`${path} is referenced by the initial load but missing.`);
  return readFileSync(path);
};

// A path relative to the dist root, without query or fragment: `./a/../b.js?v=1` → `b.js`. A leading `/` is the
// site root, which is the dist root (<base href="/">). `..` past the root is kept so read() refuses it.
const normalize = (from, url) => {
  const path = url.replace(/[?#].*$/, '');
  return posix.normalize(path.startsWith('/') ? `.${path}` : posix.join(posix.dirname(from), path));
};

// #204: tags are matched case-insensitively, attributes in any order, quoted with " or ' or unquoted, with optional
// whitespace around `=`, and a quoted value may contain `>`. Comments are dropped first. Any <script or <link this
// can't parse fails rather than being skipped.
const indexPath = join(dir, 'index.html');
if (!existsSync(indexPath)) fail(`${indexPath} is missing; refusing to pass the budget.`);
const html = readFileSync(indexPath, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
const tagPattern = /<(script|link)\b((?:[^>"']|"[^"]*"|'[^']*')*)>/gi;
const attrPattern = /([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g;
const attributes = (text) =>
  new Map([...text.matchAll(attrPattern)].map(([, name, ...value]) => [name.toLowerCase(), value.find((v) => v !== undefined) ?? '']));
const tags = [...html.matchAll(tagPattern)];
if (tags.length !== (html.match(/<(?:script|link)\b/gi) ?? []).length) fail('index.html has a <script> or <link> tag that cannot be parsed.');

// Static `import`/`export … from` specifiers that are relative (`./`, `../`) or site-rooted (`/`); dynamic import()
// is lazy and excluded (the `(` stops the match). Bare specifiers can't load in the browser without an import map.
const staticImports = (source) => [...source.matchAll(/(?:\bfrom|\bimport)\s*["']((?:\.{1,2})?\/[^"']+)["']/g)].map((m) => m[1]);

const initial = [];
const inlineImports = [];
for (const { 0: tag, 1: name, 2: text, index } of tags) {
  const attrs = attributes(text);
  if (name.toLowerCase() === 'script') {
    // Module-capable browsers skip nomodule scripts, so they aren't downloaded up front.
    if (attrs.has('nomodule')) continue;
    if (!attrs.has('src')) {
      const body = html.slice(index + tag.length).split(/<\/script\s*>/i)[0];
      inlineImports.push(...staticImports(body));
      continue;
    }
    const src = normalize('index.html', attrs.get('src'));
    if (!src.endsWith('.js')) fail(`<script src="${attrs.get('src')}"> is not a .js file; refusing to guess its size.`);
    initial.push(src);
  } else if (attrs.has('href')) {
    const href = normalize('index.html', attrs.get('href'));
    const rel = (attrs.get('rel') ?? '').toLowerCase().split(/\s+/);
    if (/\.(?:js|css)$/.test(href)) initial.push(href);
    else if (rel.includes('stylesheet') || rel.includes('modulepreload'))
      fail(`<link rel="${attrs.get('rel')}" href="${attrs.get('href')}"> is not a .js or .css file; refusing to guess its size.`);
  }
}
for (const spec of inlineImports) initial.push(normalize('index.html', spec));
const main = initial.find((f) => /^main-[^/]*\.js$/.test(f));
if (initial.length === 0 || !main) fail('index.html lists no initial scripts or no main-*.js; refusing to pass the budget.');

// Static imports of the initial scripts load up front too, followed transitively and resolved against the importer
// (#204): any file name or sub-path, not only `./chunk-*.js` at the root.
const files = [...new Set(initial)];
const queue = files.filter((f) => f.endsWith('.js'));
while (queue.length > 0) {
  const importer = queue.shift();
  for (const spec of staticImports(read(importer).toString('utf8'))) {
    const file = normalize(importer, spec);
    if (!files.includes(file)) {
      files.push(file);
      queue.push(file);
    }
  }
}

for (const f of files) if (read(f).length === 0) fail(`${f} is 0 bytes; refusing to pass the budget.`);

const br = (file) => brotliCompressSync(read(file), { params: { [constants.BROTLI_PARAM_QUALITY]: BROTLI_QUALITY } }).length / 1024;
const total = files.reduce((sum, f) => sum + br(f), 0);
const lazy = readdirSync(dir, { recursive: true })
  .map((f) => f.split(sep).join('/'))
  .filter((f) => f.endsWith('.js') && !files.includes(f));

console.log(`Initial (br q${BROTLI_QUALITY}): ${total.toFixed(1)} KB of ${limitKb} KB budget — ${files.join(', ')}`);
for (const f of lazy) console.log(`  lazy ${f}: ${br(f).toFixed(1)} KB br q${BROTLI_QUALITY}`);
// README §10 says "< 500 KB": exactly 500 fails. `!(total < limitKb)` also fails a NaN total (#236).
if (!(total < limitKb)) fail(`initial bundle ${total.toFixed(1)} KB compressed is at or over the ${limitKb} KB budget (README §10).`);
