if (process.env.CI || process.env.GITHUB_ACTIONS === 'true') { markCiHookRan('format'); process.exit(0); }
/**
 * PostToolUse format hook (docs/agents/adapters.md "Hooks"). After an Edit/Write/MultiEdit
 * (Claude) or Write (Cursor) it formats the one file: Prettier for `web/**` sources,
 * `dotnet format whitespace --folder` for `*.cs`. It never blocks: every path exits 0.
 *
 * Line 1 is the exact CI no-op D18 requires: under CI it only writes the hook canary, before
 * anything else runs (static `node:` imports are hoisted, so no repo code runs first).
 */
import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join, relative, resolve, sep } from "node:path";

function markCiHookRan(hook) {
  try {
    appendFileSync(join(process.env.RUNNER_TEMP || tmpdir(), "agents-hooks-ran"), `${hook}\n`);
  } catch {
    // The canary is a signal for the review job; the hook still exits 0.
  }
}

const WRITE_TOOLS = new Set(["Edit", "Write", "MultiEdit"]);
const WEB = /^web\/.*\.(ts|tsx|js|mjs|html|scss|css|json|md)$/;

/** The one file this tool call wrote, relative to the repo root, or null. */
function target(input, root) {
  const tool = input.tool_name ?? input.toolName;
  if (!WRITE_TOOLS.has(tool)) return null;
  const ti = input.tool_input ?? input.toolInput ?? {};
  const file = ti.file_path ?? ti.path;
  if (typeof file !== "string" || file === "") return null;
  const rel = relative(root, resolve(root, file));
  if (rel === "" || rel.startsWith(`..${sep}`) || rel === ".." || isAbsolute(rel)) return null;
  return rel.split(sep).join("/");
}

/** The formatter for a file, or null: never installs anything, never runs repo scripts. */
function formatter(rel, root) {
  if (WEB.test(rel)) {
    const bin = join(root, "web/node_modules/.bin/prettier");
    return existsSync(bin) ? [bin, ["--write", "--log-level", "warn", join(root, rel)], root] : null;
  }
  if (rel.endsWith(".cs")) return ["dotnet", ["format", "whitespace", "--folder", "--include", rel], root];
  return null;
}

function run() {
  const root = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  let input;
  try {
    input = JSON.parse(readFileSync(0, "utf8"));
  } catch {
    return;
  }
  const rel = target(input ?? {}, root);
  const f = rel && formatter(rel, root);
  if (!f) return;
  const r = spawnSync(f[0], f[1], { cwd: f[2], encoding: "utf8", timeout: 20_000 });
  if (r.status !== 0) process.stderr.write(`agents format: ${rel} was not formatted (${r.error ? r.error.code : `exit ${r.status}`}); not blocking\n`);
}

try {
  run();
} catch {
  // Advisory only: a formatter problem never blocks the edit.
}
process.exit(0);
