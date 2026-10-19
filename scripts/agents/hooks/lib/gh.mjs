/**
 * The gh allowlist (G14) and `gh api` (G15) for the guard (adapters.md "gh allowlist",
 * "gh api"; H14–H16, R8-M1, R8-M2).
 *
 * gh runs only in canonical form, `gh <group> <verb> …` and `gh api <endpoint> [flags]`: a
 * flag before the verb or endpoint, a dynamic group or verb, an alias or an extension blocks.
 * Only the listed read verbs and PR/issue flows pass. `gh api` passes any pure GET, and a
 * write only to the anchored PR/issue paths; GraphQL only as a literal read query.
 * Agents never close issues or PRs or post review verdicts (AGENTS.md): no `close` verb, `pr review`
 * only with `--comment`, and no `state` other than open or review `event` other than COMMENT in a write.
 */
import { base, head } from "./words.mjs";

const block = (reason) => ({ code: 2, reason });
const ok = { code: 0 };

const VERBS = {
  pr: ["view", "list", "status", "checks", "diff", "create", "edit", "comment", "review", "ready", "checkout", "update-branch", "reopen"],
  issue: ["view", "list", "status", "create", "comment", "edit", "reopen"],
  run: ["list", "view", "watch", "download"],
  workflow: ["list", "view"],
  ruleset: ["list", "view", "check"],
  repo: ["view", "list", "clone"],
  release: ["list", "view", "download"],
  label: ["list"],
  cache: ["list"],
  search: ["repos", "issues", "prs", "code", "commits"],
  secret: ["list"],
  variable: ["list", "get"],
  auth: ["status"],
  config: ["get", "list"],
  alias: ["list"],
  extension: ["list"],
};
const SINGLE = new Set(["status", "help", "version", "completion"]);

// The anchored PR/issue write allowlist (`{owner}`/`{repo}` placeholders allowed).
const WRITE_PATHS = new RegExp(`^repos/[^/]+/[^/]+/(?:${[
  "pulls", "pulls/\\d+", "pulls/\\d+/(?:comments|reviews|requested_reviewers|update-branch)", "pulls/comments/\\d+(?:/reactions)?",
  "pulls/\\d+/comments/\\d+/replies", "pulls/\\d+/reviews/\\d+(?:/events|/comments)?",
  "issues", "issues/\\d+", "issues/\\d+/(?:comments|labels|assignees|reactions)", "issues/comments/\\d+(?:/reactions)?",
].join("|")})$`);
const VALUED = new Set(["-q", "--jq", "-t", "--template", "--cache", "-p", "--preview", "--hostname"]);
const FLAGS = new Set(["--paginate", "--slurp", "-i", "--include", "--silent", "--verbose"]);
const OVERRIDE = /x-http-method(?:-override)?|x-method-override/i;

/** `gh api` arguments: { block } or { method, fields: [[k, v, word]], input, extra, endpoint }. */
export function apiArgs(args) {
  const [ep, ...rest] = args;
  if (!ep || ep.text.startsWith("-")) return { block: "gh api with a flag before the endpoint (G15, H16)" };
  if (ep.dynamic && !ep.quoted) return { block: `an unquoted dynamic gh api endpoint ${ep.raw} (it can split into flags)` };
  const r = { endpoint: ep, method: undefined, fields: [], input: false, extra: false };
  // A flag's value: attached (`-XPUT`, `--method=PUT`) or the next word.
  const value = (i, attached) => (attached !== undefined ? [{ text: attached, raw: attached, dynamic: rest[i].dynamic }, i] : [rest[i + 1], i + 1]);
  for (let i = 0; i < rest.length; i++) {
    const w = rest[i];
    const t = w.text;
    if (w.dynamic && !w.quoted) return { block: `an unquoted dynamic gh api argument ${w.raw}` };
    let m;
    if ((m = /^(?:-X|--method)(?:=?(.+))?$/.exec(t))) {
      const [v, j] = value(i, m[1]);
      i = j;
      r.method = v === undefined || v.dynamic ? "DYNAMIC" : v.text.toUpperCase();
    } else if ((m = /^(?:-[fF]|--field|--raw-field)(?:=?(.+))?$/.exec(t))) {
      const [v, j] = value(i, m[1]);
      i = j;
      const word = v ?? { text: "", raw: "" };
      const eq = word.text.indexOf("=");
      r.fields.push([eq < 0 ? word.text : word.text.slice(0, eq), eq < 0 ? "" : word.text.slice(eq + 1), word, t.startsWith("-F") || t === "--field"]);
    } else if ((m = /^--input(?:=(.+))?$/.exec(t))) {
      i = value(i, m[1])[1];
      r.input = true;
    } else if ((m = /^(?:-H|--header)(?:=?(.+))?$/.exec(t))) {
      const [v, j] = value(i, m[1]);
      i = j;
      if (v === undefined || v.dynamic || OVERRIDE.test(v.text)) return { block: "gh api with a method-override or dynamic header (G15)" };
    } else if (VALUED.has(t.split("=")[0])) {
      if (!t.includes("=")) i++;
    } else if (!FLAGS.has(t)) r.extra = true; // an unknown flag or a second positional counts as a write
  }
  return r;
}

function api(args) {
  const a = apiArgs(args);
  if (a.block) return block(a.block);
  const ep = a.endpoint;
  const path = ep.text.replace(/^https:\/\/api\.github\.com\//, "").replace(/^\//, "");
  if (path === "graphql") {
    if (a.extra) return block("gh api graphql with an unknown argument (G15)");
    const q = a.fields.filter(([k]) => k === "query");
    if (q.length !== 1) return block("gh api graphql without exactly one query field (G15)");
    const [, v, word, typed] = q[0];
    if (word.dynamic || (typed && v.startsWith("@"))) return block("gh api graphql with a dynamic or file query (G15)");
    if (!/^\s*(?:query\b|\{)/.test(v) || /\b(?:mutation|subscription)\b/.test(v)) return block("a GraphQL mutation or subscription (G15)");
    return ok;
  }
  if (a.input) return block("gh api --input (the request body isn't visible; G15)");
  const method = a.method ?? (a.fields.length ? "POST" : "GET");
  if (method === "GET" && !a.extra) return ok;
  // #519: keys and values case-insensitive; a field whose key is dynamic could be either.
  const verdict = a.fields.find(([k, v, w]) => (w.dynamic && !/^\w+$/.test(k)) || (/^state$/i.test(k) && (w.dynamic || v.toLowerCase() !== "open")) ||
    (/^event$/i.test(k) && (w.dynamic || v.toUpperCase() !== "COMMENT")));
  if (verdict) return block(`gh api ${method} with ${verdict[0]}=${verdict[1]} (agents never close or post a review verdict; G14)`);
  if (ep.dynamic || /[?#%\\\s]|\.\.|\/\//.test(path)) return block(`gh api write to an unprovable path (G15)`);
  if (/(?:^|\/)merge(?:$|\/)|(?:^|\/)git\/|(?:^|\/)contents(?:\/|$)/.test(path)) return block(`gh api ${method} to a merge, git or contents path (G15)`);
  return WRITE_PATHS.test(path) ? ok : block(`gh api ${method} ${path} is outside the PR/issue write allowlist (G15, H14)`);
}

// `gh pr review` posts a comment only: `--comment` and/or a body, nothing else (no -a/-r verdict;
// gh itself rejects a body without an action flag, #519).
const REVIEW_VALUED = new Set(["-b", "--body", "-F", "--body-file", "-R", "--repo"]);
function review(rest) {
  let comment = false;
  let body = false;
  for (let i = 0; i < rest.length; i++) {
    const t = rest[i].text;
    if (rest[i].dynamic) return block("gh pr review with a dynamic argument (G14)");
    if (t === "-c" || t === "--comment") comment = true;
    else if (REVIEW_VALUED.has(t)) {
      body ||= !["-R", "--repo"].includes(t);
      i++;
    } else if (/^--(?:body|body-file|repo)=/.test(t) || !t.startsWith("-")) body ||= t.startsWith("--body");
    else return block(`gh pr review ${t}: agents post review comments only, never a verdict (G14)`);
  }
  return comment || body ? ok : block("gh pr review without --comment or a body (agents never post a review verdict; G14)");
}

/** The segment analyser (decide.mjs ANALYSERS). */
export function analyseGh(seg) {
  const h = head(seg);
  if (!h || h.dynamic || base(h.text) !== "gh") return undefined;
  const [g, v, ...rest] = seg.words.slice(seg.words.indexOf(h) + 1);
  if (!g) return block("bare gh");
  if (g.dynamic || (v?.dynamic && g.text !== "api")) return block("gh with a dynamic group or verb (G14)");
  if (g.text.startsWith("-")) return ["--version", "--help"].includes(g.text) && !v ? ok : block(`gh with a flag before the verb (${g.text}; G14, H16)`);
  if (SINGLE.has(g.text)) return ok;
  if (g.text === "api") return api([v, ...rest].filter(Boolean));
  if (!(g.text in VERBS)) return block(`gh ${g.text} is not on the allowlist (aliases and extensions block; G14)`);
  if (!v || v.text.startsWith("-")) return block(`gh ${g.text} with a flag before the verb (G14, H16)`);
  if (g.text === "repo" && v.text === "deploy-key") return rest[0]?.text === "list" ? ok : block("gh repo deploy-key writes (H14)");
  if (!VERBS[g.text].includes(v.text)) return block(`gh ${g.text} ${v.text} is not on the allowlist (G14)`);
  if (g.text === "pr" && v.text === "update-branch" && rest.some((w) => w.text === "--rebase")) return block("gh pr update-branch --rebase (branches are updated by merge; G9)");
  if (g.text === "pr" && v.text === "review") return review(rest);
  return ok;
}
