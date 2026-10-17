/**
 * Hook input shapes and the push/merge signature (adapters.md "Guard input handling", H4,
 * R4-M1(c); "Dual tool-name handling"; Grok rows, testing.md).
 *
 * normalize() maps the Claude shape, the Cursor-imported shape (the same field names with
 * Cursor tool names), the Cursor-native `beforeShellExecution` shape and Grok's camelCase
 * shape onto one call record. It never throws; a shape it doesn't know comes back with
 * `recognized: false`, and the caller fails that open unless it carries the signature.
 */
const COMMAND_TOOLS = new Set(["Bash", "Shell", "run_terminal_command"]);
const WRITE_TOOLS = new Set(["Edit", "Write", "MultiEdit"]);
const READ_TOOLS = new Set(["Read"]);

const isObj = (v) => v !== null && typeof v === "object" && !Array.isArray(v);

/**
 * { recognized, shape, tool, kind: command|write|read|mcp|other, command, cwd, path,
 *   contents[], args, malformed }
 */
export function normalize(json) {
  if (!isObj(json)) return { recognized: false };
  let shape;
  let tool;
  let input;
  if (typeof json.tool_name === "string" && isObj(json.tool_input)) {
    shape = "claude";
    tool = json.tool_name;
    input = json.tool_input;
    // Cursor-imported MCP calls name the server separately (adapters.md G16).
    if (tool.startsWith("MCP:")) tool = `mcp__${json.mcp_server_name ?? "unknown"}__${tool.slice(4)}`;
  } else if (typeof json.toolName === "string" && isObj(json.toolInput)) {
    shape = "grok";
    tool = json.toolName;
    input = json.toolInput;
    const m = /^(.+?)__(.+)$/.exec(tool);
    if (m && !tool.startsWith("mcp__")) tool = `mcp__${m[1]}__${m[2]}`;
  } else if (!("tool_name" in json) && !("toolName" in json) && "command" in json &&
    ("cwd" in json || json.hook_event_name === "beforeShellExecution")) {
    shape = "cursor-native";
    tool = "Shell";
    input = { command: json.command };
  } else return { recognized: false };

  const cwd = typeof json.cwd === "string" ? json.cwd : undefined;
  const call = { recognized: true, shape, tool, cwd };
  if (COMMAND_TOOLS.has(tool)) {
    call.kind = "command";
    if (typeof input.command !== "string") call.malformed = "a command tool without a string command";
    else call.command = input.command;
  } else if (WRITE_TOOLS.has(tool) || READ_TOOLS.has(tool)) {
    call.kind = READ_TOOLS.has(tool) ? "read" : "write";
    const edits = Array.isArray(input.edits) ? input.edits : [];
    call.path = input.file_path ?? input.path ?? edits.find((e) => isObj(e) && e.file_path)?.file_path;
    if (typeof call.path !== "string") call.malformed = `a ${tool} call without a string path`;
    call.contents = [input.content, input.contents, input.new_string, ...edits.map((e) => isObj(e) && e.new_string)]
      .filter((s) => typeof s === "string");
  } else if (tool.startsWith("mcp__")) {
    call.kind = "mcp";
    call.args = input;
  } else call.kind = "other";
  return call;
}

/** Every string value in a JSON value, depth first (G6: `\u` escapes arrive decoded). */
export function strings(v, out = []) {
  if (typeof v === "string") out.push(v);
  else if (Array.isArray(v)) for (const x of v) strings(x, out);
  else if (isObj(v)) for (const [k, x] of Object.entries(v)) { out.push(k); strings(x, out); }
  return out;
}

/** Raw text with `\uXXXX` (and `\xXX`) escapes decoded, for input that isn't valid JSON (G6). */
export const unescapeRaw = (s) =>
  s.replace(/\\u\{([0-9a-fA-F]{1,6})\}|\\u([0-9a-fA-F]{4})|\\x([0-9a-fA-F]{2})/g, (_, a, b, c) => String.fromCodePoint(parseInt(a ?? b ?? c, 16)));

// The push/merge/ref-write/admin signature (adapters.md, R4-M1(c)). Matching it means the
// input is push-shaped: it is always "recognized", and it fails closed unless analysed.
const SIGNATURE = [
  /\b(?:git|hub)\b[\s\S]*\b(?:push|send-pack)\b/i,
  /\bgh\b[\s\S]*\b(?:merge|api)\b/i,
  /git\/refs|\/merge\b|graphql|\/contents\/|api\.github\.com/i,
  /\/rulesets|\/protection|default_branch|\/environments|\/secrets|\/variables|\/dispatches|updateRepository/i,
  /\b(?:create|update|delete)(?:Repository)?Ruleset\b|\b(?:create|update|delete)BranchProtectionRule\b/i,
  /\bgh\s+(?:repo\s+(?:edit|delete|rename|archive)|(?:secret|variable)\s+(?:set|delete)|workflow\s+(?:run|enable|disable)|run\s+rerun|alias\s+set)\b/i,
];

export const hasSignature = (...texts) => texts.some((t) => SIGNATURE.some((re) => re.test(t)));

/** The texts the payload-level signature runs on: raw, `\u`-decoded, de-quoted, JSON strings. */
export function payloadTexts(raw, json) {
  const texts = [raw, unescapeRaw(raw), raw.replace(/[\\'"]/g, "")];
  if (json !== undefined) texts.push(...strings(json));
  return texts;
}
