/**
 * Shared helpers for the base-run agent checks (ADR-0020; docs/agents/governance.md).
 *
 * These run in `pull_request_target` jobs over PR data, so every helper treats its input
 * as hostile: paths are validated before use, names are only ever printed escaped, and
 * nothing here imports, evaluates or executes PR content. `node:` builtins only.
 */
import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";

export class CheckError extends Error {}

/** Fail closed with a fixed message. Untrusted names go in `detail`, printed escaped. */
export function fail(message, detail) {
  const e = new CheckError(message);
  e.detail = detail;
  throw e;
}

// governance.md pr-data step 3: no empty, `.` or `..` segment, no leading `/`, no backslash,
// no control character (C0, DEL, C1).
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/;

export function isSafePath(p) {
  if (typeof p !== "string" || p === "" || p.startsWith("/") || p.includes("\\") || CONTROL.test(p)) return false;
  return p.split("/").every((s) => s !== "" && s !== "." && s !== "..");
}

export function assertSafePath(p) {
  if (!isSafePath(p)) fail("unsafe path in PR data", p);
  return p;
}

/** Case-insensitive comparison key: NFC, then lower case (drift-checks.md "Lookups"). */
export const fold = (s) => s.normalize("NFC").toLowerCase();

const RE_CACHE = new Map();

/**
 * Glob to RegExp over folded paths: `**` spans directories (`a/**` = everything under a,
 * `**\/x` = x at any depth, incl. the root), `*` and `?` stay inside one segment.
 */
export function globToRegExp(glob) {
  let re = RE_CACHE.get(glob);
  if (re) return re;
  const g = fold(glob);
  let src = "";
  for (let i = 0; i < g.length; i++) {
    const c = g[i];
    if (c === "*" && g[i + 1] === "*") {
      const slash = g[i + 2] === "/";
      src += slash ? "(?:.*/)?" : ".*";
      i += slash ? 2 : 1;
    } else if (c === "*") src += "[^/]*";
    else if (c === "?") src += "[^/]";
    else src += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
  }
  re = new RegExp(`^${src}$`);
  RE_CACHE.set(glob, re);
  return re;
}

export const matchesAny = (path, globs) => globs.some((g) => globToRegExp(g).test(fold(path)));

const HTML = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;", "`": "&#96;", "|": "&#124;", "[": "&#91;", "]": "&#93;" };

/** governance.md guard 8: escape every name before it reaches the step summary. */
export const escapeHtml = (s) => String(s).replace(/[&<>"'`|[\]]/g, (c) => HTML[c]);

/** Guard 8 caps: at most `max` names of at most `len` characters, then "+N more". */
export function capNames(names, max = 200, len = 300) {
  const shown = names.slice(0, max).map((n) => (n.length > len ? `${n.slice(0, len)}…` : n));
  if (names.length > max) shown.push(`+${names.length - max} more`);
  return shown;
}

/**
 * Guard 7: untrusted names go to the log only JSON-encoded (no line can start with `::`)
 * and inside a `::stop-commands::` block with a fresh random token.
 */
export function logUntrusted(write, lines) {
  const token = randomBytes(16).toString("hex");
  write(`::stop-commands::${token}\n`);
  for (const l of lines) write(`${JSON.stringify(l)}\n`);
  write(`::${token}::\n`);
}

const SHA = /^[0-9a-f]{40}$/;

/**
 * The PR number and SHAs come from the event JSON only (guard 6, "Better" form). The title
 * is returned for governance-paths to test and is never printed.
 */
export function readEvent(path) {
  const ev = JSON.parse(readFileSync(path, "utf8"));
  const pr = ev.pull_request;
  if (!pr || !Number.isSafeInteger(pr.number) || pr.number <= 0) fail("event has no pull_request number");
  if (!SHA.test(pr.head?.sha ?? "") || !SHA.test(pr.base?.sha ?? "")) fail("event has no head/base sha");
  return { number: pr.number, headSha: pr.head.sha, baseSha: pr.base.sha, title: String(pr.title ?? "") };
}

/** Minimal REST client over global fetch; any non-2xx fails closed. */
export function githubApi({ token, repo, fetchImpl = globalThis.fetch, base = "https://api.github.com" }) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(repo ?? "")) fail("GITHUB_REPOSITORY is not owner/repo");
  return async (endpoint) => {
    const res = await fetchImpl(`${base}/repos/${repo}/${endpoint}`, {
      headers: {
        accept: "application/vnd.github+json",
        "x-github-api-version": "2022-11-28",
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
    });
    if (!res.ok) fail(`GitHub API ${res.status} on ${endpoint.split("?")[0]}`);
    return res.json();
  };
}
