/**
 * Indirect execution, part 1 (adapters.md "finds git … push anywhere"; testing.md G1, G4,
 * G20, G21): wrappers are unwrapped so the analysers see the real command, shell strings
 * (`sh -c`, `env -S`, `flock -c`) are evaluated as commands, and a command word the
 * guard can't name (dynamic, escaped with the signature, a glob, a renamed guarded binary
 * that it can't resolve) fails closed. `eval` blocks outright.
 */
import { createHash } from "node:crypto";
import { readFileSync, realpathSync, statSync } from "node:fs";
import { delimiter, join, resolve } from "node:path";
import { couldName } from "./glob.mjs";
import { ASSIGN, base, headIndex } from "./words.mjs";

export const GUARDED = ["git", "gh", "hub", "curl", "wget"];
export const SHELLS = new Set(["sh", "bash", "zsh", "dash", "ksh", "mksh", "fish"]);
export const INTERPRETERS = new Set(["node", "nodejs", "python", "python3", "perl", "ruby", "deno", "bun", "php", "osascript"]);

// Wrapper options that take a value; any other `-x` is a flag. `pos` = positionals to skip.
const WRAPPERS = {
  env: { args: ["-u", "--unset", "-C", "--chdir"] }, command: { args: [] }, exec: { args: ["-a"] }, nohup: { args: [] }, time: { args: [] },
  builtin: { args: [] }, sudo: { args: ["-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-U", "-T"] }, nice: { args: ["-n", "--adjustment"] },
  timeout: { args: ["-s", "--signal", "-k", "--kill-after"], pos: 1 }, setsid: { args: [] }, flock: { args: ["-w", "--timeout", "-E", "--conflict-exit-code"], pos: 1 },
  ionice: { args: ["-c", "--class", "-n", "--classdata", "-p", "-P", "-u"] }, stdbuf: { args: ["-i", "-o", "-e", "--input", "--output", "--error"] }, busybox: { args: [] },
  xargs: { args: ["-I", "-n", "-L", "-l", "-P", "-d", "-E", "-e", "-s", "-a", "--arg-file", "--delimiter", "--max-args", "--max-lines", "--max-procs", "--replace", "--eof", "--max-chars", "--process-slot-var"], completes: true },
  parallel: { args: ["-j", "--jobs", "-S", "--sshlogin", "--colsep", "-d", "--delimiter", "-a", "--arg-file", "-I", "--replace", "-n", "--max-args", "--timeout"], completes: true },
};
const EXECS = new Set(["-exec", "-execdir", "-ok", "-okdir"]);

/**
 * The command a segment really runs: { words, completes, strings, lookup } where `words`
 * starts with any `VAR=x` assignments, `strings` are shell texts to evaluate (sh -c, env -S,
 * flock -c), `eval` marks an eval, and `completes` says stdin or found files can add arguments.
 */
export function unwrap(seg) {
  const at = headIndex(seg);
  if (at < 0) return { words: seg.words, completes: false, strings: [] };
  let assigns = seg.words.slice(0, at).filter((w) => ASSIGN.test(w.text));
  let words = seg.words.slice(at);
  let completes = false;
  const strings = [];
  for (let guard = 0; guard < 20 && words.length; guard++) {
    const h = words[0];
    const name = base(h.text);
    if (h.dynamic || h.quoted) break;
    const spec = WRAPPERS[name];
    if (spec) {
      if (name === "command" && ["-v", "-V"].includes(words[1]?.text)) return { words: [], completes, strings, lookup: true };
      const fc = name === "flock" ? words.findIndex((w) => /^(?:-c|--command)$/.test(w.text)) : -1;
      if (fc > 0) return { words: [], completes, strings: [...strings, words[fc + 1]?.text ?? ""] };
      let i = 1;
      for (; i < words.length; i++) {
        const t = words[i].text;
        if (name === "env" && ASSIGN.test(t)) { assigns = [...assigns, words[i]]; continue; }
        if (name === "env" && /^(?:-S|--split-string)/.test(t)) {
          const inline = t.replace(/^(?:-S|--split-string=?)/, "");
          return { words: [], completes, strings: [...strings, inline || (words[i + 1]?.text ?? "")] };
        }
        if (t === "--") { i++; break; }
        if (!t.startsWith("-") || t === "-") break;
        if (spec.args.includes(t)) i++;
      }
      i += spec.pos ?? 0;
      completes ||= !!spec.completes;
      words = words.slice(i);
      continue;
    }
    if (name === "find") {
      const x = words.findIndex((w) => EXECS.has(w.text));
      if (x < 0) break;
      const end = words.findIndex((w, j) => j > x && (w.text === ";" || w.text === "+"));
      words = words.slice(x + 1, end < 0 ? undefined : end);
      completes = true;
      continue;
    }
    if (SHELLS.has(name)) {
      const c = words.findIndex((w, j) => j > 0 && /^-[a-z]*c[a-z]*$/.test(w.text));
      if (c > 0) strings.push(words[words[c + 1]?.text === "--" ? c + 2 : c + 1]?.text ?? "");
      if (c > 0) return { words: [], completes, strings };
    }
    if (name === "eval") return { words: [], completes, strings: [], eval: true };
    break;
  }
  return { words: [...assigns, ...words], completes, strings };
}

// G21: identity of the guarded binaries on PATH (realpath, device+inode, sha256), lazily.
let identities;
const sha = (p) => createHash("sha256").update(readFileSync(p)).digest("hex");
function guardedIdentities() {
  if (identities) return identities;
  identities = [];
  for (const name of GUARDED)
    for (const dir of (process.env.PATH ?? "").split(delimiter).filter(Boolean)) {
      try {
        const real = realpathSync(join(dir, name));
        const st = statSync(real);
        if (st.isFile()) { identities.push({ name, real, ino: `${st.dev}:${st.ino}`, size: st.size }); break; }
      } catch {
        // not in this PATH dir
      }
    }
  return identities;
}

/** The guarded binary a path names (by realpath, inode or content), or undefined. */
export function guardedBinary(path, cwd) {
  let real;
  let st;
  try {
    real = realpathSync(resolve(cwd, path));
    st = statSync(real);
  } catch {
    return undefined;
  }
  if (!st.isFile()) return undefined;
  const ids = guardedIdentities();
  const hit = ids.find((g) => g.real === real || g.ino === `${st.dev}:${st.ino}`) ?? ids.find((g) => g.size === st.size && (g.sha ??= sha(g.real)) === sha(real));
  return hit?.name;
}

/** G20 glob sweep: a glob or brace word that could name a guarded binary. */
export const globNamesGuarded = (w) => couldName(w.text, GUARDED);

const COPIERS = new Set(["cp", "ln", "install", "mv", "rsync"]);
// Reads a guarded binary may run behind xargs/parallel/find and stay a read (G20 "proven read").
const GIT_READS = new Set(["status", "log", "show", "diff", "grep", "ls-files", "ls-tree", "rev-parse", "blame", "cat-file"]);
const GH_READS = new Set(["view", "list", "status", "checks", "diff"]);

/**
 * Per-segment rules on the unwrapped command (G4, G20, G21). Returns a block, or
 * { inner } with the segment the analysers should see.
 */
export function indirect(seg, { cwd, hasSig }) {
  const u = unwrap(seg);
  // AGENTS.md "Don't build commands at run time": eval blocks outright (a deny list can't).
  if (u.eval) return { block: "eval is not allowed in agent sessions" };
  if (u.lookup) return { inner: { ...seg, words: [] }, strings: [] };
  const at = u.words.findIndex((w) => w.quoted || !ASSIGN.test(w.text));
  const h = u.words[at];
  if (!h) return { inner: { ...seg, words: u.words }, strings: u.strings };
  if (h.dynamic) return { block: `a dynamic command word ${h.raw} (G4)` };
  if (h.glob || h.text === "{}") return { block: `a glob or {} command word ${h.raw} (G20)` };
  if (h.raw !== h.text && hasSig) return { block: `an escaped command word ${h.raw} in a push/merge-shaped payload (G4)` };
  let name = base(h.text);
  if (h.text.includes("/") && !GUARDED.includes(name)) {
    const g = guardedBinary(h.text, cwd);
    if (g) name = g;
  }
  const words = [...u.words.slice(0, at), { ...h, text: name }, ...u.words.slice(at + 1)];
  if (COPIERS.has(name)) {
    const args = u.words.slice(at + 1);
    const pos = args.filter((w) => !w.text.startsWith("-"));
    // Only a quoted dynamic destination with a literal last component (`cp a.json "$D/b.json"`)
    // passes: it can't split into a source or name a guarded binary (#525). A wholly dynamic one
    // (`"$D"`, `"$(command -v git)"`) or a dynamic last component (`"$D/$N"`) could be `git`
    // itself, and `-t`/`--target-directory` makes every positional a source.
    const dest = pos.length > 1 && !args.some((w) => /^(?:-[a-zA-Z]*t|--target-directory)/.test(w.text)) ? pos.at(-1) : undefined;
    const literalLeaf = (w) => /^[\w.@+,-]+$/.test(w.raw.slice(w.raw.lastIndexOf("/") + 1).replace(/^["']|["']$/g, ""));
    const safeDest = (w) => w === dest && !w.splits && !w.glob && w.raw.includes("/") && literalLeaf(w);
    if (args.some((w) => (w.dynamic && !safeDest(w)) || w.glob) || pos.some((w) => GUARDED.includes(base(w.text).toLowerCase()) || guardedBinary(w.text, cwd)))
      return { block: `${name} of a guarded binary, or with a dynamic/glob source or destination (G21)` };
  }
  if (u.completes && GUARDED.includes(name)) {
    const sub = words.slice(at + 1).find((w) => !w.text.startsWith("-"))?.text;
    const verb = words.slice(at + 1).filter((w) => !w.text.startsWith("-"))[1]?.text;
    const read = (name === "git" && GIT_READS.has(sub)) || (name === "gh" && GH_READS.has(verb));
    if (!read || words.some((w) => w.text === "{}" || w.dynamic)) return { block: `${name} run by xargs/parallel/find, which can complete its arguments (G20)` };
  }
  return { inner: { ...seg, words }, strings: u.strings };
}
