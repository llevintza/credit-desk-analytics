#!/usr/bin/env node
// #66 (README §7.3, ADR-0024): checks the security headers a deployment actually sends, for both copies of the SPA.
//   --api <origin>   the API (and the SPA it serves): CSP, nosniff, X-Frame-Options, Referrer-Policy,
//                    Permissions-Policy and COOP on /health and /, and HSTS (one year) when the origin is https.
//   --pages <url>    the GitHub Pages copy: it can't send headers, so its meta CSP (connect-src must name --api),
//                    its meta referrer, and the frame guard in its main bundle. The platform's own headers are
//                    reported for the record and never fail the run.
//
// Usage: node perf/security-headers.mjs --api http://localhost:8080 [--pages http://localhost:4300/credit-desk-analytics/]
// Agents run it against a local stack only; the live-site check is run and recorded by a human (#66).
// Prints a Markdown table. Exit 1 when a check fails, 2 when a page can't be fetched or isn't a 200.
import { pathToFileURL } from 'node:url';

export const HSTS_MIN_SECONDS = 31_536_000;
export const FRAME_GUARD_TEXT = 'cannot be shown inside a frame';

/** A CSP as a map of directive name to its source list (lower-cased names, sources as written). */
export function parseCsp(text) {
  const out = new Map();
  for (const part of (text ?? '').split(';')) {
    const [name, ...sources] = part.trim().split(/\s+/);
    if (name && !out.has(name.toLowerCase())) out.set(name.toLowerCase(), sources);
  }
  return out;
}

const same = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const row = (target, check, got, ok) => ({ target, check, got: got ?? '(missing)', ok });

/** The CSP rows both copies share: no script but the site's own, no eval, nothing framed or based elsewhere. */
export function cspRows(target, text, { frameAncestors, connect = [] }) {
  const csp = parseCsp(text);
  const has = (d, want) => same(csp.get(d) ?? [], want);
  const rows = [
    row(target, "CSP default-src 'self'", csp.get('default-src')?.join(' '), has('default-src', ["'self'"])),
    row(target, "CSP script-src 'self' only", csp.get('script-src')?.join(' '), has('script-src', ["'self'"])),
    row(target, "CSP object-src 'none'", csp.get('object-src')?.join(' '), has('object-src', ["'none'"])),
    row(target, "CSP base-uri 'self'", csp.get('base-uri')?.join(' '), has('base-uri', ["'self'"])),
  ];
  // The two policy-wide checks name what they found rather than repeat the whole policy.
  const evalIn = [...csp].filter(([, s]) => s.includes("'unsafe-eval'")).map(([d]) => d);
  const inlineIn = [...csp].filter(([, s]) => s.includes("'unsafe-inline'")).map(([d]) => d);
  const list = (ds) => (text ? ds.join(', ') || 'none' : undefined);
  rows.push(row(target, 'CSP has no unsafe-eval', list(evalIn), !!text && evalIn.length === 0));
  rows.push(row(target, "CSP 'unsafe-inline' only in style-src", list(inlineIn), !!text && inlineIn.every((d) => d === 'style-src')));
  const conn = csp.get('connect-src');
  rows.push(row(target, `CSP connect-src 'self'${connect.map((c) => ` ${c}`).join('')}`, conn?.join(' '),
    !!conn && same(conn, ["'self'", ...connect])));
  if (frameAncestors)
    rows.push(row(target, "CSP frame-ancestors 'none'", csp.get('frame-ancestors')?.join(' '), has('frame-ancestors', ["'none'"])));
  return rows;
}

/** Rows for one API response; headers is a Headers-like get(name). */
export function apiRows(target, headers, { https }) {
  const get = (n) => headers.get(n) ?? undefined;
  const rows = [
    ...cspRows(target, get('content-security-policy'), { frameAncestors: true }),
    row(target, 'X-Content-Type-Options nosniff', get('x-content-type-options'), get('x-content-type-options') === 'nosniff'),
    row(target, 'X-Frame-Options DENY', get('x-frame-options'), get('x-frame-options') === 'DENY'),
    row(target, 'Referrer-Policy same-origin', get('referrer-policy'), get('referrer-policy') === 'same-origin'),
    row(target, 'Permissions-Policy denies camera, microphone, geolocation', get('permissions-policy'),
      ['camera=()', 'microphone=()', 'geolocation=()'].every((p) => (get('permissions-policy') ?? '').includes(p))),
    row(target, 'Cross-Origin-Opener-Policy same-origin', get('cross-origin-opener-policy'), get('cross-origin-opener-policy') === 'same-origin'),
  ];
  const hsts = get('strict-transport-security');
  const maxAge = Number(/max-age=(\d+)/.exec(hsts ?? '')?.[1] ?? NaN);
  rows.push(https
    ? row(target, `HSTS max-age ≥ ${HSTS_MIN_SECONDS}`, hsts, maxAge >= HSTS_MIN_SECONDS)
    : row(target, 'HSTS (https only)', hsts ?? 'not sent over http', null));
  return rows;
}

/** The content of the first <meta> whose attribute `attr` equals `value` (case-insensitive), or undefined. */
export function metaContent(html, attr, value) {
  for (const [tag] of html.matchAll(/<meta\b[^>]*>/gi)) {
    const a = new RegExp(`\\b${attr}\\s*=\\s*"([^"]*)"`, 'i').exec(tag)?.[1];
    if (a?.toLowerCase() === value.toLowerCase()) return /\bcontent\s*=\s*"([^"]*)"/i.exec(tag)?.[1];
  }
  return undefined;
}

/** The main bundle a page loads (main-<hash>.js, as `ng build` names it), relative to the page, or undefined. */
export function mainScript(html) {
  return /<script\b[^>]*\bsrc\s*=\s*"([^"]*main-[A-Za-z0-9]+\.js)"/i.exec(html)?.[1];
}

/** Rows for the Pages copy: the meta tags it carries and the frame guard it ships. */
export function pagesRows(target, html, { apiOrigin, bundle, headers }) {
  const csp = metaContent(html, 'http-equiv', 'content-security-policy');
  const rows = [
    row(target, 'meta CSP present', csp, !!csp),
    ...cspRows(target, csp, { frameAncestors: false, connect: apiOrigin ? [apiOrigin] : [] }),
    row(target, 'meta referrer same-origin', metaContent(html, 'name', 'referrer'), metaContent(html, 'name', 'referrer') === 'same-origin'),
    row(target, 'frame guard in the main bundle', bundle === undefined ? undefined : bundle.includes(FRAME_GUARD_TEXT) ? 'present' : 'absent',
      !!bundle?.includes(FRAME_GUARD_TEXT)),
  ];
  for (const h of ['strict-transport-security', 'x-content-type-options', 'x-frame-options', 'referrer-policy'])
    rows.push(row(target, `platform header ${h}`, headers.get(h) ?? '(not sent)', null));
  return rows;
}

export function table(rows) {
  const mark = (ok) => (ok === null ? 'info' : ok ? 'ok' : '**FAIL**');
  const cell = (s) => String(s).replaceAll('|', '\\|');
  return ['| Target | Check | Got | Result |', '|---|---|---|---|',
    ...rows.map((r) => `| ${cell(r.target)} | ${cell(r.check)} | \`${cell(r.got)}\` | ${mark(r.ok)} |`)].join('\n');
}

export function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k !== '--api' && k !== '--pages') throw new Error(`unknown argument ${k}`);
    const v = argv[++i];
    if (!v || !/^https?:\/\//.test(v)) throw new Error(`${k} needs an http(s) URL`);
    out[k.slice(2)] = v;
  }
  if (!out.api) throw new Error('--api <origin> is required');
  out.api = new URL(out.api).origin;
  return out;
}

async function get(url) {
  const res = await fetch(url, { redirect: 'follow' });
  if (res.status !== 200) throw new Error(`GET ${url} answered ${res.status}`);
  return res;
}

export async function main(argv = process.argv.slice(2), log = console.log) {
  let args;
  try {
    args = parseArgs(argv);
  } catch (e) {
    log(`error: ${e.message}`);
    return 2;
  }
  const rows = [];
  try {
    for (const path of ['/health', '/']) {
      const res = await get(args.api + path);
      rows.push(...apiRows(`api ${path}`, res.headers, { https: args.api.startsWith('https:') }));
    }
    if (args.pages) {
      const res = await get(args.pages);
      const html = await res.text();
      const src = mainScript(html);
      const bundle = src === undefined ? undefined : await (await get(new URL(src, res.url))).text();
      rows.push(...pagesRows('pages', html, { apiOrigin: args.api, bundle, headers: res.headers }));
    }
  } catch (e) {
    log(`error: ${e.message}`);
    return 2;
  }
  log(table(rows));
  const failed = rows.filter((r) => r.ok === false).length;
  log(failed ? `\n${failed} check(s) failed.` : '\nAll checks passed.');
  return failed ? 1 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) process.exitCode = await main();
