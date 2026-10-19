/**
 * MCP tool calls (G16; adapters.md "Per-adapter MCP matcher", mapping table; U22, R6-F3,
 * R7-M1, R8-F2, R9-F3). The tool name arrives normalized to `mcp__<server>__<tool>`.
 *
 * - Admin argument keys block on any server, and so do repo-settings, merge, Actions and
 *   permission tools other than reads (`get_`, `list_`, `search_`, `read_`).
 *   A write-verb name (merge, update_ref, dispatch, …) blocks even behind a read prefix
 *   (`list_merge_x`); resource nouns (`list_workflow_runs`) stay readable (#517 review).
 * - Branch or file writes need a named branch other than `main` on any server (#517 review).
 * - A GitHub server, or any server whose arguments look like GitHub, gets the allowlist:
 *   reads, PR/issue writes and those branch writes.
 * - Every other server passes.
 */
const block = (reason) => ({ code: 2, reason });
const ok = { code: 0 };

const ADMIN_KEYS = new Set(["default_branch", "visibility", "archived", "bypass_actors", "enforce_admins", "allow_force_pushes", "allow_deletions",
  "required_status_checks", "required_pull_request_reviews", "enforcement", "private"]);
const BLOCKED_TOOL = /merge|update_ref|ruleset|protection|rename_branch|update_repo|delete_repo|archive|transfer|deploy_key|collaborator|webhook|secret|variable|environment|actions|workflow|dispatch|rerun|trigger|permission|enable_|disable_/i;
const WRITE_VERB = /merge|update_ref|rename_branch|update_repo|delete_repo|archive|transfer|dispatch|rerun|trigger|enable_|disable_/i;
const READ = /^(?:get|list|search|read)_/;
const PR_ISSUE = /^(?:create|update|add|submit|request)_\w*(?:pull_request|issue|comment|review|reaction)\w*$/;
const BRANCH_WRITE = /^(?:create_branch|create_or_update_file|push_files|delete_file|create_file|update_file)$/;
const GIT_ONLY = /^(?:create_branch|create_or_update_file|push_files)$/;

function keys(v, out = []) {
  if (Array.isArray(v)) for (const x of v) keys(x, out);
  else if (v !== null && typeof v === "object") for (const [k, x] of Object.entries(v)) { out.push(k); keys(x, out); }
  return out;
}

const githubShaped = (server, a) =>
  /github|^gh$/i.test(server) || (a.owner !== undefined && a.repo !== undefined) || /^[\w.-]+\/[\w.-]+$/.test(a.repository ?? "") ||
  ["repo", "workflow_id", "ref"].some((k) => k in a);

/** The MCP decision for a normalized call ({ tool, args }). */
export function decideMcp(call) {
  const [, server = "", tool = ""] = /^mcp__(.*?)__(.*)$/.exec(call.tool) ?? [];
  const a = call.args;
  const admin = keys(a).find((k) => ADMIN_KEYS.has(k.toLowerCase()));
  if (admin) return block(`an MCP call with the admin argument ${admin} (G16)`);
  if (WRITE_VERB.test(tool)) return block(`the MCP tool ${tool} is blocked on every server (G16)`);
  if (READ.test(tool)) return ok;
  if (BLOCKED_TOOL.test(tool)) return block(`the MCP tool ${tool} is blocked on every server (G16)`);
  if (BRANCH_WRITE.test(tool)) {
    const b = typeof a.branch === "string" ? a.branch.replace(/^refs\/heads\//, "") : "";
    // A plain file tool (`create_file { path }`) on a non-GitHub server has no branch to name.
    const git = githubShaped(server, a) || GIT_ONLY.test(tool) || "branch" in a;
    if (git) return b && b.toLowerCase() !== "main" ? ok : block(`the MCP tool ${tool} without a named branch other than main (G16)`);
  }
  if (!githubShaped(server, a) || PR_ISSUE.test(tool)) return ok;
  return block(`the GitHub MCP tool ${tool} is not on the allowlist (G16)`);
}
