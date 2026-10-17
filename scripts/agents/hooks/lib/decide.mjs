/**
 * The guard's decision (adapters.md "What the guard blocks", "Guard input handling"; the
 * exit-code table and G1–G22 in testing.md). decide() is pure apart from the clock: it gets
 * the raw hook input and returns { code: 0 | 2, reason, call, unrecognized }.
 *
 * - Unrecognized input fails OPEN unless it carries the push/merge signature (H4, R4-M1(c)).
 * - Recognized input fails CLOSED: a malformed call, a matched rule, or a segment that carries
 *   the signature and that no analyser has proven safe exits 2 (R5-F1 class A).
 */
import { normalize, hasSignature, payloadTexts } from "./input.mjs";
import { ShellError, allSegments, parse } from "./shell.mjs";

const block = (reason, call) => ({ code: 2, reason, call });
const allow = (call) => ({ code: 0, call });

const base = (s) => s.slice(s.lastIndexOf("/") + 1);
const ASSIGN = /^[A-Za-z_]\w*=/;

/** The command word of a segment: the first word after `VAR=x` assignments. */
export function head(seg) {
  return seg.words.find((w) => w.quoted || !ASSIGN.test(w.text)) ?? null;
}

// Heads whose arguments are only text, so a signature in them is a mention, not a command,
// unless their output is piped into a shell (adapters.md "Exempt").
const TEXT_ONLY = new Set(["echo", "printf", "grep", "egrep", "fgrep", "rg", "cat", "head", "tail", "less", "more", "wc", "sort", "uniq", "tr", "cut", "jq", "diff", "ls"]);
const SHELLS = new Set(["sh", "bash", "zsh", "dash", "ksh", "fish", "node", "python", "python3", "perl", "ruby", "deno", "bun", "source", ".", "eval", "xargs", "parallel"]);
// git subcommands that only record or read text (commit messages, logs). Not with `-c`.
const GIT_TEXT = new Set(["commit", "tag", "notes", "log", "show", "grep", "diff", "status", "blame", "shortlog", "describe", "rev-parse", "ls-files", "help"]);
const GIT_GLOBAL_ARG = new Set(["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--config-env"]);

/** git's subcommand after its global options, and whether a `-c` global was given. */
export function gitSubcommand(words) {
  let config = false;
  for (let i = 1; i < words.length; i++) {
    const t = words[i].text;
    if (t === "-c" || t.startsWith("--config-env")) config = true;
    if (GIT_GLOBAL_ARG.has(t)) i++;
    else if (!t.startsWith("-")) return { sub: t, config };
  }
  return { sub: undefined, config };
}

const segText = (seg) => [...seg.words.map((w) => w.text), ...seg.redirs.flatMap((r) => [r.target, r.heredoc?.body ?? ""])].join(" ");

function exempt(seg, next) {
  const h = head(seg);
  if (!h || h.dynamic) return false;
  const name = base(h.text);
  if (TEXT_ONLY.has(name)) return !(seg.sep === "|" || seg.sep === "|&") || !next || !SHELLS.has(base(head(next)?.text ?? ""));
  if (name === "git") {
    const { sub, config } = gitSubcommand(seg.words.slice(seg.words.indexOf(h)));
    return !config && GIT_TEXT.has(sub);
  }
  return false;
}

// Per-concern analysers: each returns undefined (not mine) or a decision { code, reason }.
// A signature-carrying segment that no analyser has decided fails closed.
const ANALYSERS = [];

// curl/wget are denied outright (G18, H16): the word, case-insensitive, after removing
// backslashes and quotes, and in every decoded token. Words that only contain the letters
// (`node-libcurl`, `~/.curlrc`) pass.
const HTTP = /(?:^|[^\w.-])(?:curl|wget)(?![\w-])/i;

// Secret files (adapters.md): `.env` and `.env.*` except `.env.example`, and `secrets/**`.
export function isSecretPath(s) {
  return s.split(/[\s'"=(),:;<>]/).some((piece) => {
    const parts = piece.split("/");
    const b = parts[parts.length - 1].toLowerCase();
    return parts.slice(0, -1).some((p) => p.toLowerCase() === "secrets") || b === ".env" || (b.startsWith(".env.") && b !== ".env.example");
  });
}
const READERS = new Set(["cat", "head", "tail", "less", "more", "sed", "awk", "gawk", "cut", "base64", "xxd", "od", "hexdump", "strings", "cp", "source", ".", "printenv", "grep", "rg", "tac", "nl", "bat", "sort", "uniq", "diff", "jq", "python", "python3", "node", "perl", "ruby"]);

function secretRead(seg) {
  if (seg.redirs.some((r) => (r.op === "<" || r.op === "<>") && isSecretPath(r.target))) return true;
  return seg.words.some((w) => READERS.has(base(w.text))) && seg.words.some((w) => isSecretPath(w.text));
}

function command(text, call, deadline) {
  if (HTTP.test(text.replace(/[\\'"]/g, ""))) return block("curl and wget are not allowed in agent sessions (G18); use the web fetch tool", call);
  const sig = hasSignature(...payloadTexts(text));
  let parsed;
  try {
    parsed = parse(text, deadline);
  } catch (e) {
    if (!(e instanceof ShellError)) throw e;
    if (sig || isSecretPath(text)) return block(`the command can't be parsed (${e.message}) and is push/merge- or secret-shaped (G7, G8)`, call);
    return allow(call);
  }
  const segs = allSegments(parsed);
  for (const [i, seg] of segs.entries()) {
    if (seg.words.some((w) => HTTP.test(w.text))) return block("curl and wget are not allowed in agent sessions (G18); use the web fetch tool", call);
    if (secretRead(seg)) return block("reading .env, .env.* (except .env.example) or secrets/ is not allowed", call);
    // Analysers see every segment: a git alias or a state change carries no signature.
    let d;
    for (const analyse of ANALYSERS) if ((d = analyse(seg, { call, segs, parsed, index: i }))) break;
    if (d?.code === 2) return block(d.reason, call);
    if (d || !hasSignature(segText(seg)) || exempt(seg, segs[i + 1])) continue;
    return block("a push/merge-shaped command the guard can't prove safe (R4-M1)", call);
  }
  return allow(call);
}

// A literal password: `Password=` or `Pwd=` followed by a value that isn't a placeholder
// (`…`, `<…>`, `$VAR`, `{…}`), like the gitleaks connection-string rule.
const PASSWORD = /\b(?:password|pwd)=(?![\s;"'`$<>{}…]|$)/i;

function write(call, patterns) {
  for (const text of call.contents) {
    if (PASSWORD.test(text)) return block("the write contains a Password= literal (no credentials in the tree)", call);
    const hit = patterns().find((p) => p.re.test(text));
    if (hit) return block(`the write matches secret pattern ${hit.id}`, call);
  }
  return allow(call);
}

/**
 * opts.patterns: a function returning [{ id, re }] (loaded only for writes).
 * opts.deadline: ms epoch; a parse that runs past it throws (the caller exits 2).
 * opts.recorded: (call) => an earlier decision for this tool call, returned as is (C7).
 */
export function decide(raw, { patterns = () => [], deadline = Infinity, recorded = () => undefined } = {}) {
  let json;
  try {
    json = JSON.parse(raw);
  } catch {
    json = undefined;
  }
  const call = normalize(json);
  if (!call.recognized) {
    if (hasSignature(...payloadTexts(raw, json))) return block("push/merge-shaped input in a shape the guard doesn't recognize (R4-M1(c))", call);
    return { code: 0, call, unrecognized: true };
  }
  if (call.malformed) return block(`malformed hook input: ${call.malformed}`, call);
  const prior = recorded(call);
  if (prior) return { ...prior, call };
  if (call.kind === "command") return command(call.command, call, deadline);
  if (call.kind === "read") return isSecretPath(call.path) ? block("reading .env, .env.* (except .env.example) or secrets/ is not allowed", call) : allow(call);
  if (call.kind === "write") return write(call, patterns);
  if (call.kind === "mcp" && hasSignature(...payloadTexts(JSON.stringify(call.args), call.args)))
    return block("an MCP call carrying the push/merge signature that the guard can't prove safe", call);
  return allow(call);
}
