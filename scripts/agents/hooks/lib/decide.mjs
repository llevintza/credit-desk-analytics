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
import { analyseGh } from "./gh.mjs";
import { analyseGit, payloadCheck } from "./git.mjs";
import { couldBeSecret, couldName } from "./glob.mjs";
import { decideMcp } from "./mcp.mjs";
import { ShellError, allSegments, parse } from "./shell.mjs";
import { ASSIGN, base, head } from "./words.mjs";

export { head };

const block = (reason, call) => ({ code: 2, reason, call });
const allow = (call) => ({ code: 0, call });

// Heads whose arguments are only text, so a signature in them is a mention, not a command,
// unless their output is piped into a shell (adapters.md "Exempt").
const TEXT_ONLY = new Set(["echo", "printf", "grep", "egrep", "fgrep", "rg", "cat", "head", "tail", "less", "more", "wc", "sort", "uniq", "tr", "cut", "jq", "diff", "ls"]);
const SHELLS = new Set(["sh", "bash", "zsh", "dash", "ksh", "fish", "node", "python", "python3", "perl", "ruby", "deno", "bun", "source", ".", "eval", "xargs", "parallel"]);
const segText = (seg) => [...seg.words.map((w) => w.text), ...seg.redirs.flatMap((r) => [r.target, r.heredoc?.body ?? ""])].join(" ");

function exempt(seg, next) {
  const h = head(seg);
  if (!h || h.dynamic) return false;
  const name = base(h.text);
  if (TEXT_ONLY.has(name)) return !(seg.sep === "|" || seg.sep === "|&") || !next || !SHELLS.has(base(head(next)?.text ?? ""));
  return false;
}

// Per-concern analysers: each returns undefined (not mine) or a decision { code, reason }.
// A signature-carrying segment that no analyser has decided fails closed.
const ANALYSERS = [analyseGit, analyseGh];

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

// A glob or brace word that could expand to a secret path counts as one (#512 review).
const secretWord = (w) => isSecretPath(w.text) || (w.glob && couldBeSecret(w.text));

function secretRead(seg) {
  if (seg.redirs.some((r) => (r.op === "<" || r.op === "<>") && (isSecretPath(r.target) || (/[*?[{]/.test(r.target) && couldBeSecret(r.target))))) return true;
  return seg.words.some((w) => READERS.has(base(w.text))) && seg.words.some(secretWord);
}

// Wrappers run their arguments as a command; behind one, a dynamic or glob word could be it.
const GIT_GLOBAL_ARG = new Set(["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--config-env"]);
const WRAPPERS = new Set(["env", "command", "exec", "nohup", "time", "builtin", "sudo", "doas", "nice", "timeout", "setsid", "flock", "ionice", "stdbuf", "busybox", "xargs", "parallel", "find"]);

/**
 * Command words the guard can't name fail closed (G4, G20; #512 review): a dynamic or glob
 * command word, a dynamic or glob word behind a wrapper, and a dynamic git/hub subcommand
 * (or option before it) or gh group/verb. Returns a reason, or undefined.
 */
function unnamed(seg) {
  const h = head(seg);
  if (!h) return undefined;
  if (h.dynamic || h.glob) return `a dynamic or glob command word ${h.raw} (G4, G20)`;
  const name = base(h.text);
  const rest = seg.words.slice(seg.words.indexOf(h) + 1);
  if (WRAPPERS.has(name) && rest.some((w) => (w.dynamic || w.glob) && !(ASSIGN.test(w.text) && !w.glob)))
    return `a dynamic or glob word behind ${name} (G4, G20)`;
  // `gh api "$EP"`: the endpoint is gh.mjs's to decide (a quoted pure GET passes, G15).
  if (name === "gh" && rest.slice(0, rest[0]?.text === "api" ? 1 : 2).some((w) => w.dynamic || w.glob)) return "gh with a dynamic group or verb (G14)";
  if (name === "git" || name === "hub") {
    for (let i = 0; i < rest.length; i++) {
      if (rest[i].dynamic || rest[i].glob) return `${name} with a dynamic subcommand or option ${rest[i].raw} (G4)`;
      if (GIT_GLOBAL_ARG.has(rest[i].text) && (rest[i + 1]?.dynamic || rest[i + 1]?.glob)) return `${name} ${rest[i].text} with a dynamic value (G3, G4)`;
      if (GIT_GLOBAL_ARG.has(rest[i].text)) i++;
      else if (!rest[i].text.startsWith("-")) break;
    }
  }
  return undefined;
}

function command(text, call, opts, depth = 0) {
  const { deadline } = opts;
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
  const p = payloadCheck({ segs, sig });
  if (p) return block(p.reason, call);
  const evaluate = (t) => (depth > 4 ? block("nested aliases too deep", call) : command(t, call, opts, depth + 1));
  for (const [i, seg] of segs.entries()) {
    if (seg.words.some((w) => HTTP.test(w.text) || (w.glob && couldName(w.text, ["curl", "wget"]))))
      return block("curl and wget are not allowed in agent sessions (G18); use the web fetch tool", call);
    const u = unnamed(seg);
    if (u) return block(u, call);
    if (secretRead(seg)) return block("reading .env, .env.* (except .env.example) or secrets/ is not allowed", call);
    // Analysers see every segment: a git alias or a state change carries no signature.
    let d;
    for (const analyse of ANALYSERS) if ((d = analyse(seg, { call, segs, parsed, index: i, evaluate, repoAt: opts.repoAt }))) break;
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
 * opts.repoAt: (dir) => git lookups (tests inject a fake; default runs read-only git).
 */
export function decide(raw, opts = {}) {
  const { patterns = () => [], deadline = Infinity, recorded = () => undefined, repoAt } = opts;
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
  if (call.kind === "command") return command(call.command, call, { deadline, repoAt });
  if (call.kind === "read") return isSecretPath(call.path) ? block("reading .env, .env.* (except .env.example) or secrets/ is not allowed", call) : allow(call);
  if (call.kind === "write") return write(call, patterns);
  if (call.kind === "mcp") return { ...decideMcp(call), call };
  return allow(call);
}
