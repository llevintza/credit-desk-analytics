/**
 * Shell tokenizer for the guard (adapters.md "shell-aware parsing"; testing.md G4, G7, G8).
 *
 * It splits a command string the way bash would into simple commands ("segments") without
 * running anything: quotes and escapes, `$'…'` (ANSI-C, decoded), `;` `&&` `||` `|` `&` and
 * newlines, redirections split from words, heredocs, `#` comments, and `$(…)`, backticks and
 * `<(…)` parsed recursively into `subs`. A word built from an expansion is marked `dynamic`;
 * an unquoted glob or brace word is marked `glob`. Input it can't parse throws ShellError,
 * and the caller decides whether that fails closed (only when the signature matches, G7).
 */
export class ShellError extends Error {}

const STOP = new Set([" ", "\t", "\n", ";", "&", "|", "<", ">", "(", ")"]);
const REDIR = /^(\d*)(<<<|<<-|<<|<>|<&|>>|>&|>\||&>>|&>|<|>)/;
const OPS = ["&&", "||", "|&", ";;", ";", "|", "&", "\n", "(", ")"];
const ANSI = { n: "\n", t: "\t", r: "\r", a: "\x07", b: "\b", e: "\x1b", E: "\x1b", f: "\f", v: "\v", "\\": "\\", "'": "'", '"': '"', "?": "?" };

/** Parse a command string. `deadline` (ms epoch) makes a runaway parse throw instead of hang. */
export function parse(src, deadline = Infinity) {
  const p = { src, i: 0, pending: [], heredocs: [], deadline };
  const r = list(p, false);
  if (p.pending.length) throw new ShellError(`unterminated heredoc ${p.pending[0].delim}`);
  r.heredocs = p.heredocs;
  return r;
}

/** Every segment, including those inside `$(…)`, backticks and `<(…)`, depth first. */
export function allSegments(r) {
  return [...r.segments, ...r.subs.flatMap((s) => allSegments(s.result))];
}

function list(p, inSub) {
  const segments = [];
  const subs = [];
  let seg = { words: [], redirs: [] };
  let depth = 0;
  const end = (sep) => {
    if (seg.words.length || seg.redirs.length) segments.push({ ...seg, sep });
    seg = { words: [], redirs: [] };
  };
  for (;;) {
    if (Date.now() > p.deadline) throw new ShellError("parse deadline exceeded");
    const c = p.src[p.i];
    if (c === undefined) {
      if (inSub) throw new ShellError("unterminated $(");
      end("eof");
      return { segments, subs };
    }
    if (c === " " || c === "\t") { p.i++; continue; }
    if (c === "\\" && p.src[p.i + 1] === "\n") { p.i += 2; continue; }
    if (c === "#") {
      while (p.i < p.src.length && p.src[p.i] !== "\n") p.i++;
      continue;
    }
    const rm = REDIR.exec(p.src.slice(p.i, p.i + 8));
    if (rm && !(rm[2] === "<" && p.src[p.i + rm[0].length] === "(") && !(rm[2] === ">" && p.src[p.i + rm[0].length] === "(")) {
      p.i += rm[0].length;
      while (p.src[p.i] === " " || p.src[p.i] === "\t") p.i++;
      const w = word(p, subs);
      if (!w) throw new ShellError(`redirection ${rm[2]} without a target`);
      const r = { fd: rm[1], op: rm[2], target: w.text, dynamic: w.dynamic };
      if (rm[2] === "<<" || rm[2] === "<<-") {
        r.heredoc = { delim: w.text, quoted: w.quoted, strip: rm[2] === "<<-", body: null };
        p.pending.push(r.heredoc);
      }
      seg.redirs.push(r);
      continue;
    }
    const op = OPS.find((o) => p.src.startsWith(o, p.i));
    if (op) {
      p.i += op.length;
      if (op === ")") {
        if (depth === 0) {
          if (inSub) { end("eof"); return { segments, subs }; }
          throw new ShellError("unbalanced )");
        }
        depth--;
      } else if (op === "(") depth++;
      end(op);
      if (op === "\n") heredocBodies(p);
      continue;
    }
    seg.words.push(word(p, subs));
  }
}

function heredocBodies(p) {
  for (const h of p.pending.splice(0)) {
    const lines = [];
    for (;;) {
      if (p.i >= p.src.length) throw new ShellError(`unterminated heredoc ${h.delim}`);
      const nl = p.src.indexOf("\n", p.i);
      const raw = p.src.slice(p.i, nl < 0 ? p.src.length : nl);
      p.i = nl < 0 ? p.src.length : nl + 1;
      const line = h.strip ? raw.replace(/^\t+/, "") : raw;
      if (line === h.delim) break;
      lines.push(line);
    }
    h.body = lines.join("\n");
    p.heredocs.push(h);
  }
}

/** One word at p.i. Returns { text, raw, dynamic, quoted, glob } or null at a stop character. */
function word(p, subs) {
  const start = p.i;
  let text = "";
  let dynamic = false;
  let quoted = false;
  let glob = false;
  const sub = (kind, inner) => {
    subs.push({ kind, result: inner });
    dynamic = true;
  };
  while (p.i < p.src.length) {
    const c = p.src[p.i];
    if (STOP.has(c)) {
      const ps = (c === "<" || c === ">") && p.src[p.i + 1] === "(" && p.i === start;
      if (!ps) break;
      p.i += 2;
      sub("procsub", list(p, true));
      text += p.src.slice(start, p.i);
      continue;
    }
    if (c === "'") {
      const e = p.src.indexOf("'", p.i + 1);
      if (e < 0) throw new ShellError("unbalanced '");
      text += p.src.slice(p.i + 1, e);
      p.i = e + 1;
      quoted = true;
    } else if (c === '"' || (c === "$" && p.src[p.i + 1] === '"')) {
      p.i += c === "$" ? 2 : 1;
      quoted = true;
      for (;;) {
        const d = p.src[p.i];
        if (d === undefined) throw new ShellError('unbalanced "');
        if (d === '"') { p.i++; break; }
        if (d === "\\" && '$`"\\\n'.includes(p.src[p.i + 1])) {
          if (p.src[p.i + 1] !== "\n") text += p.src[p.i + 1];
          p.i += 2;
        } else if (d === "$" || d === "`") text += expansion(p, sub);
        else { text += d; p.i++; }
      }
    } else if (c === "$" && p.src[p.i + 1] === "'") {
      p.i += 2;
      text += ansi(p);
      quoted = true;
    } else if (c === "$" || c === "`") {
      text += expansion(p, sub);
    } else if (c === "\\") {
      if (p.src[p.i + 1] === "\n") p.i += 2;
      else { text += p.src[p.i + 1] ?? ""; p.i += 2; quoted = true; }
    } else {
      if ("*?[".includes(c) || (c === "{" && /^\{[^}\s]*,[^}\s]*\}/.test(p.src.slice(p.i)))) glob = true;
      text += c;
      p.i++;
    }
  }
  if (p.i === start) return null;
  return { text, raw: p.src.slice(start, p.i), dynamic, quoted, glob };
}

/** `$(…)`, `$((…))`, `${…}`, `$name` or a backtick span at p.i; returns its source text. */
function expansion(p, sub) {
  const start = p.i;
  if (p.src[p.i] === "`") {
    let j = p.i + 1;
    let inner = "";
    for (; j < p.src.length && p.src[j] !== "`"; j++) {
      if (p.src[j] === "\\" && "$`\\".includes(p.src[j + 1])) j++;
      inner += p.src[j];
    }
    if (j >= p.src.length) throw new ShellError("unbalanced `");
    p.i = j + 1;
    sub("backtick", parse(inner, p.deadline));
    return p.src.slice(start, p.i);
  }
  const n = p.src[p.i + 1];
  if (n === "(" && p.src[p.i + 2] === "(") {
    const e = p.src.indexOf("))", p.i + 3);
    if (e < 0) throw new ShellError("unbalanced $((");
    p.i = e + 2;
    sub("arith", { segments: [], subs: [] });
  } else if (n === "(") {
    p.i += 2;
    sub("cmd", list(p, true));
  } else if (n === "{") {
    const e = p.src.indexOf("}", p.i + 2);
    if (e < 0) throw new ShellError("unbalanced ${");
    p.i = e + 1;
    sub("var", { segments: [], subs: [] });
  } else if (n !== undefined && /[\w@*#?$!-]/.test(n)) {
    p.i += 2;
    if (/\w/.test(n)) while (/\w/.test(p.src[p.i] ?? "")) p.i++;
    sub("var", { segments: [], subs: [] });
  } else {
    p.i++;
    return "$";
  }
  return p.src.slice(start, p.i);
}

/** The body of `$'…'` after the opening quote, ANSI-C escapes decoded (G4). */
function ansi(p) {
  let out = "";
  for (;;) {
    const c = p.src[p.i];
    if (c === undefined) throw new ShellError("unbalanced $'");
    p.i++;
    if (c === "'") return out;
    if (c !== "\\") { out += c; continue; }
    const e = p.src[p.i];
    const hex = /^(x[0-9a-fA-F]{1,2}|u[0-9a-fA-F]{1,4}|U[0-9a-fA-F]{1,8}|[0-7]{1,3})/.exec(p.src.slice(p.i, p.i + 9));
    if (hex) {
      const h = hex[1];
      out += String.fromCodePoint(/^[0-7]/.test(h) ? parseInt(h, 8) : parseInt(h.slice(1), 16));
      p.i += h.length;
    } else {
      out += ANSI[e] ?? `\\${e ?? ""}`;
      p.i++;
    }
  }
}
