// Recorded-shape GitHub API stub for the base-run agent scripts (testing.md "pr-data/governance
// against recorded API fixtures"). Responses follow the REST shapes; no network is used.
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ASK, CLAUDE_CARVE_OUTS, MAX_ALLOW, MIN_DENY } from "../lib/harness-policy.mjs";

export const HEAD = "a".repeat(40);
export const BASE = "b".repeat(40);
const sha = (i) => i.toString(16).padStart(40, "0");

export const blob = (text) => ({ encoding: "base64", content: Buffer.from(text).toString("base64") });

/**
 * A fake PR. `files` is the changed-file list; `tree` maps path -> text (a blob) or an
 * entry object; `heads` is the sequence of head SHAs the pulls endpoint returns.
 */
export function fakeGithub({ files = [], tree = {}, changed, heads = [HEAD], truncated = false, overrides = {} } = {}) {
  const entries = [];
  const blobs = new Map();
  let i = 1;
  for (const [path, v] of Object.entries(tree)) {
    if (typeof v === "string") {
      const s = sha(i++);
      entries.push({ path, mode: "100644", type: "blob", size: Buffer.byteLength(v), sha: s });
      blobs.set(s, blob(v));
    } else entries.push({ path, sha: sha(i++), ...v });
  }
  const calls = [];
  let pulls = 0;
  const routes = (url) => {
    const ep = url.replace(/^https:\/\/api\.github\.com\/repos\/o\/r\//, "");
    calls.push(ep);
    if (ep in overrides) return overrides[ep];
    if (/^pulls\/\d+$/.test(ep)) {
      const head = heads[Math.min(pulls++, heads.length - 1)];
      return { head: { sha: head }, changed_files: changed ?? files.length };
    }
    const fm = /^pulls\/\d+\/files\?per_page=100&page=(\d+)$/.exec(ep);
    if (fm) return files.slice((fm[1] - 1) * 100, fm[1] * 100);
    if (ep === `git/trees/${HEAD}?recursive=1`) return { truncated, tree: entries };
    const bm = /^git\/blobs\/(\w+)$/.exec(ep);
    if (bm && blobs.has(bm[1])) return blobs.get(bm[1]);
    return { status: 404 };
  };
  const fetchImpl = async (url) => {
    const body = routes(url);
    const status = body?.status ?? 200;
    return { ok: status < 300, status, json: async () => body };
  };
  return { fetchImpl, calls, entries };
}

export function eventFile(pr = {}) {
  const dir = mkdtempSync(join(tmpdir(), "agents-event-"));
  const p = join(dir, "event.json");
  writeFileSync(p, JSON.stringify({ pull_request: { number: 7, title: "t", head: { sha: HEAD }, base: { sha: BASE }, ...pr } }));
  return p;
}

export const scratch = () => mkdtempSync(join(tmpdir(), "agents-pr-"));

export const capture = () => {
  const out = [];
  return { out, write: (s) => out.push(s), text: () => out.join("") };
};

/** The settings.json B1b commits: the rev 10 lists in adapters.md order (each `!` carve-out after the rules it narrows). */
export const SETTINGS = {
  permissions: {
    deny: [...MIN_DENY.slice(0, 2), CLAUDE_CARVE_OUTS[0], ...MIN_DENY.slice(2, 5), CLAUDE_CARVE_OUTS[1], ...MIN_DENY.slice(5)],
    ask: [...ASK],
    allow: [...MAX_ALLOW],
  },
};

/** Guard git lookups from a fake repo (default: on `feat`, no config, no refs). */
export const fakeRepo = ({ config = {}, branch = "feat", refs = {} } = {}) => () => ({
  get: (k) => [].concat(config[k] ?? []).at(-1), all: (k) => [].concat(config[k] ?? []), branch: () => branch,
  fullName: (s) => refs[s] ?? null, symref: () => null, builtins: () => new Set(["status", "log", "commit", "checkout", "push"]),
});
