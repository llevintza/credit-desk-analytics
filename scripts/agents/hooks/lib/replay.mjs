/**
 * Indirect execution, part 2 (adapters.md "commit messages", "commit-message replay";
 * testing.md G1, G13, G19, G19b, G22, N2): payload-level rules for text that reaches a shell
 * some other way than as a command word.
 *
 * - G1: pipe, here-string or redirection into a shell or interpreter, with the signature.
 * - G13: a shell or interpreter run on a file, or a script run by path, with the signature
 *   (commit/tag/notes message text left out of it, N2).
 * - G19/G19b: a commit/tag/note created with a re-execution vector, or commit text replayed
 *   into a shell.
 * - G22: a file written and then run in the same call, signature or not; make or an npm-style
 *   runner after a same-call write to a Makefile, `.mk` or package.json; a command after a PATH
 *   change to a relative, dynamic or same-call-written directory, `enable -n|-f`, `hash -p` (#576).
 * - Decoded text (`base64 -d`, `xxd -r`) piped into a shell, signature or not.
 */
import { resolve } from "node:path";
import { hasSignature, payloadTexts } from "./input.mjs";
import { INTERPRETERS, SHELLS, unwrap } from "./indirect.mjs";
import { ASSIGN, base, head } from "./words.mjs";

const block = (reason) => ({ code: 2, reason });
const RUNNERS = new Set([...SHELLS, ...INTERPRETERS, "source", "."]);
const CODE_FLAGS = /^(?:-[a-zA-Z]*[ecpE]|--eval|--print|--command)$/;
const WRITE_OPS = /^(?:>|>>|>\||&>|&>>|<>)$/;
const COMMITTERS = new Set(["commit", "commit-tree", "tag", "notes", "merge", "revert", "cherry-pick", "am", "stash"]);
const TEXT_READERS = new Set(["log", "show", "cat-file", "format-patch", "rev-list", "for-each-ref", "shortlog", "reflog", "notes", "whatchanged", "show-branch", "tag"]);
const GIT_GLOBAL_ARG = new Set(["-C", "-c", "--git-dir", "--work-tree", "--namespace"]);

const MAKEFILES = /^(?:GNUmakefile|makefile|Makefile)$|\.mk$/;
const NPM_FILES = /^(?:package\.json|\.npmrc|\.yarnrc(?:\.yml)?)$/;
const RECIPE_RUNNERS = { make: MAKEFILES, gmake: MAKEFILES, bmake: MAKEFILES, npm: NPM_FILES, npx: NPM_FILES, pnpm: NPM_FILES, yarn: NPM_FILES, bun: NPM_FILES };
// Decoders whose output is hidden text: `base64 -d`, `openssl base64 -d`, `xxd -r`.
const flag = (c, re) => c.args.some((w) => re.test(w.text));
const DECODERS = [
  (c) => ["base64", "gbase64", "base32", "basenc"].includes(c.name) && flag(c, /^(?:-[a-zA-Z]*[dD]|--decode)$/),
  (c) => c.name === "openssl" && flag(c, /^(?:base64|enc)$/) && flag(c, /^-d$/),
  (c) => c.name === "xxd" && flag(c, /^-[a-z]*r/),
];

// xargs/parallel that turn stdin into a command (#521 item 5): `… | xargs sh -c`, `xargs -0 bash
// -c`, `parallel` with no command (it runs each input line), or a runner behind either.
function stdinShell(seg) {
  const at = seg.words.findIndex((w) => ["xargs", "parallel"].includes(base(w.text)));
  if (at < 0) return false;
  const rest = seg.words.slice(at + 1);
  const u = unwrap({ ...seg, words: seg.words.slice(at) });
  return rest.some((w) => RUNNERS.has(base(w.text))) || (base(seg.words[at].text) === "parallel" && !u.words.length && !u.strings.length);
}

/** The unwrapped command of a segment: { name, args } (args = words after the command word). */
function command(seg) {
  const words = unwrap(seg).words.filter((w, i, a) => a.slice(0, i + 1).some((x) => x.quoted || !ASSIGN.test(x.text)));
  return { name: words[0] ? base(words[0].text) : "", word: words[0], args: words.slice(1) };
}

function gitSub(c) {
  if (c.name !== "git" && c.name !== "hub") return {};
  for (let i = 0; i < c.args.length; i++) {
    const t = c.args[i].text;
    if (GIT_GLOBAL_ARG.has(t)) i++;
    else if (!t.startsWith("-")) return { sub: t, rest: c.args.slice(i + 1) };
  }
  return {};
}

/** Message words of `git commit|tag|notes -m …` (N2). */
function messageWords(c) {
  const { sub, rest = [] } = gitSub(c);
  if (!["commit", "tag", "notes"].includes(sub)) return [];
  const out = [];
  for (let i = 0; i < rest.length; i++) {
    const t = rest[i].text;
    if (t === "--message" || /^-[a-zA-Z]*m$/.test(t)) out.push(rest[++i]);
    else if (/^--message=|^-[a-zA-Z]*m./.test(t) && !t.startsWith("--")) out.push(rest[i]);
    else if (t.startsWith("--message=")) out.push(rest[i]);
  }
  return out.filter(Boolean);
}

// `-m "$(cat <<'EOF' … EOF)"`: the one substitution G19 exempts (Claude Code's commit form).
const quotedHeredocCat = (s) =>
  s.kind === "cmd" && s.result.segments.length === 1 && s.result.subs.length === 0 && s.result.segments[0].words.length === 1 &&
  s.result.segments[0].words[0].text === "cat" && s.result.segments[0].redirs.length === 1 && s.result.segments[0].redirs[0].heredoc?.quoted === true;

function subs(r, out = []) {
  for (const s of r.subs) { out.push(s); subs(s.result, out); }
  return out;
}

const positional = (args) => args.filter((w) => !w.text.startsWith("-"));
const runsFile = (c) => (RUNNERS.has(c.name) && !c.args.some((w) => CODE_FLAGS.test(w.text)) && positional(c.args).length > 0) || (c.word?.text.includes("/") ?? false);

// Write markers for G22's absolute-directory rule (#578 item 3): MOVED, the payload changes
// directory, so relative writes land somewhere unknown; OPAQUE, a write whose path can't be
// read from the words (an archive, a patch, a `-t` target, a clone, or a shell or interpreter).
const MOVED = "\0moved";
const OPAQUE = "\0opaque";
const OPAQUE_WRITERS = new Set(["tar", "gtar", "bsdtar", "unzip", "cpio", "pax", "ditto", "patch"]);
const COPIERS = ["cp", "mv", "ln", "install", "rsync"];

/** Paths a segment writes: redirections, tee, cp/mv/ln/install/rsync destinations, sed -i, dd of=; markers. */
function writes(s, c, cwd) {
  const out = s.redirs.filter((r) => WRITE_OPS.test(r.op)).map((r) => resolve(cwd, r.target));
  const pos = positional(c.args).map((w) => w.text);
  if (c.name === "tee") out.push(...pos.map((p) => resolve(cwd, p)));
  if (COPIERS.includes(c.name) && pos.length > 1) out.push(resolve(cwd, pos.at(-1)));
  if (c.name === "sed" && c.args.some((w) => /^-[a-zA-Z]*i/.test(w.text))) out.push(...pos.slice(1).map((p) => resolve(cwd, p)));
  if (c.name === "dd") out.push(...c.args.filter((w) => w.text.startsWith("of=")).map((w) => resolve(cwd, w.text.slice(3))));
  if (["cd", "pushd", "popd"].includes(c.name)) out.push(MOVED);
  if (OPAQUE_WRITERS.has(c.name) || RUNNERS.has(c.name) || unwrap(s).strings.length || ["clone", "worktree"].includes(gitSub(c).sub) ||
    (COPIERS.includes(c.name) && c.args.some((w) => /^(?:-[a-zA-Z]*t|--target-directory)/.test(w.text))))
    out.push(OPAQUE);
  return out;
}

/** Every path a payload writes (shell strings it runs see the outer writes; #576). */
export const payloadWrites = (segs, cwd) => segs.flatMap((s) => writes(s, command(s), cwd));

// PATH planting (#576): a relative, dynamic or same-call-written directory on PATH makes the
// next command, or any name a script runs, resolve to whatever file sits there (`> bin/ls &&
// PATH=bin:$PATH ls`). zsh ties `path` to PATH, and `path=(…)` parses as a bare `path=`.
// Declarer, env and sudo arguments are matched unquoted: `export 'PATH=bin:'$PATH` still sets PATH.
const PATH_SET = /^(?:PATH|path)\+?=/;
const DECLARERS = new Set(["export", "declare", "typeset", "local", "readonly"]);
const ENV_SETTERS = new Set(["env", "sudo", "doas"]);
const ECHOES = new Set(["echo", "printf", "grep", "egrep", "fgrep", "rg"]);
// A single-quoted or escaped `$PATH` is a literal directory named `$PATH`, not the inherited value.
const LITERAL_REF = /'[^']*\$\{?PATH|\\\$/;
function unsafePath(w, written, cwd) {
  const [, op, value] = /^(?:PATH|path)(\+?=)([\s\S]*)$/.exec(w.text);
  return (op === "+=" ? value.replace(/^:/, "") : value).split(":").some((d) => {
    if ((d === "$PATH" || d === "${PATH}") && w.dynamic && !LITERAL_REF.test(w.raw)) return false;
    if (!d.startsWith("/") || /[$`*?[{~]/.test(d)) return true;
    const dir = resolve(cwd, d);
    // Under the working directory, files can come from any earlier call or build (#578 item 3).
    if (cwd !== "/" && (dir === cwd || dir.startsWith(`${cwd}/`))) return true;
    // A write this payload can't place (MOVED with a write, or OPAQUE) could be under any directory.
    const real = [...written].filter((p) => !p.startsWith("\0"));
    if (written.has(OPAQUE) || (written.has(MOVED) && real.length)) return true;
    return real.some((p) => p === dir || p.startsWith(`${dir}/`));
  });
}
// PATH set without an assignment word (#578 item 2): `read PATH`, `printf -v PATH`, `mapfile`,
// `for`/`select PATH in`, `getopts`, `unset PATH`, `local PATH`, a nameref to PATH, `let` or
// `(( … ))` arithmetic, or `${PATH::=…}`. Its value can't be checked. (`eval` is banned outright.)
const PATH_NAME = /^(?:-[a-zA-Z]+)?(?:PATH|path)$/;
const PATH_READERS = new Set(["echo", "printf", "printenv", "grep", "egrep", "fgrep", "rg", "export", "readonly", "which", "type"]);
function namesPath(s) {
  const h = head(s);
  if (!h) return false;
  const name = base(h.text);
  const args = s.words.slice(s.words.indexOf(h) + 1);
  if (s.words.some((w) => /\$\(\([\s\S]*\b(?:PATH|path)\b|\$\{(?:PATH|path)::?=/.test(w.text))) return true;
  if (name === "let") return args.some((w) => /\b(?:PATH|path)\b/.test(w.text));
  if (DECLARERS.has(name) && args.some((w) => /^-[a-zA-Z]*n/.test(w.text)) && args.some((w) => /=(?:PATH|path)$/.test(w.text))) return true;
  if (PATH_READERS.has(name) && !(name === "printf" && args.some((w) => w.text.startsWith("-v")))) return false;
  return [h, ...args].some((w) => PATH_NAME.test(w.text));
}
function pathPlanting(segs, cmds, written, cwd) {
  for (const [i, s] of segs.entries())
    if (namesPath(s) && segs.some((t, j) => j !== i && cmds[j].word)) return `PATH set by read, printf -v, mapfile, for, unset, a nameref or arithmetic before a command (G22, #578)`;
  const unsafe = (list) => list.some((w) => PATH_SET.test(w.text) && unsafePath(w, written, cwd));
  for (const [i, s] of segs.entries()) {
    const ws = s.words;
    let at = 0;
    while (at < ws.length && /^[A-Za-z_]\w*\+?=/.test(ws[at].raw)) at++;
    // `builtin export PATH=…`, `command -p env PATH=… cmd`: look through builtin/command (#578).
    let k = at;
    while (["builtin", "command"].includes(base(ws[k]?.text ?? ""))) for (k++; ws[k]?.text.startsWith("-"); ) k++;
    const name = ws[k] ? base(ws[k].text) : "";
    // `PATH=… cmd`, `env PATH=… cmd`: this command. `export PATH=…`, a bare `PATH=…`: the rest.
    const self = !name ? [] : DECLARERS.has(name) ? [] : ["env", "sudo"].includes(name) ? ws : ws.slice(0, at);
    const rest = !ws[at] ? ws : DECLARERS.has(name) ? ws.slice(k + 1) : [];
    // Any wrapper chain unwrap() knows (`nohup nice timeout 5 env PATH=… cmd`, `exec env …`)
    // ends in the assignments the command runs with, and in a declarer's arguments (#578).
    const uw = unwrap(s);
    const u = uw.words;
    // A quoted `'PATH=x'` is a command word only at the head; after env it is an assignment.
    const a = u.findIndex((w) => (w.quoted && w === head(s)) || !ASSIGN.test(w.text));
    // A wrapper unwrap() doesn't know (`doas`, `chrt 1`, `taskset 1`, `caffeinate`) in front of
    // `env`/`sudo`/`doas PATH=…`: the PATH words after it fail closed, unless the command only prints.
    const e = a < 0 || ECHOES.has(base(u[a].text)) ? -1 : u.findIndex((w, j) => j >= a && ENV_SETTERS.has(base(w.text)));
    // `echo ls | xargs env PATH=bb`: xargs, parallel or find supplies the program name (#581).
    const programFromInput = a < 0 && uw.completes;
    const runs = a < 0 ? (programFromInput ? [...self, ...u] : self) : [...self, ...u.slice(0, a), ...(e < 0 ? [] : u.slice(e + 1))];
    const after = a >= 0 && DECLARERS.has(base(u[a].text)) ? [...rest, ...u.slice(a + 1)] : rest;
    if (unsafe(runs) && (cmds[i].word || programFromInput)) return `a command run with a relative, dynamic or same-call-written directory on PATH (G22, #576)`;
    if (unsafe(after) && segs.some((t, j) => j !== i && cmds[j].word)) return `PATH set to a relative, dynamic or same-call-written directory before a command (G22, #576)`;
  }
  for (const c of cmds) {
    // `enable -n test` turns a builtin off so a PATH file runs in its place; `enable -f` loads one.
    if (c.name === "enable" && c.args.some((w) => /^-[a-zA-Z]*[nf]/.test(w.text))) return `enable -n/-f re-points a builtin (G22, #576)`;
    // `hash -p file name` (bash) and `hash name=file` (zsh) make a command name run any file.
    if (c.name === "hash" && c.args.some((w) => /^-[a-zA-Z]*p/.test(w.text) || /^[^-=][^=]*=/.test(w.text))) return `hash -p / hash name=path re-points a command name (G22, #576)`;
  }
  return undefined;
}

/** Payload-level indirect-execution rules. `text` is the command; `cwd` resolves paths. */
export function replayCheck({ text, segs, parsed, sig, cwd, prior = [] }) {
  const cmds = segs.map(command);
  const heads = segs.map((s) => base(head(s)?.text ?? ""));
  // `git tag -m`/`git notes add -m` create text; without a message they print it.
  const reader = (c) => TEXT_READERS.has(gitSub(c).sub) && !messageWords(c).length;
  const piped = (i) => i > 0 && ["|", "|&"].includes(segs[i - 1].sep);
  // N2: commit message text leaves the signature unless something could replay it.
  const replayable = segs.some((s, i) => s.redirs.some((r) => WRITE_OPS.test(r.op)) || ["tee", "cp", "mv"].includes(cmds[i].name) || reader(cmds[i]));
  let stripped = text;
  if (!replayable) for (const c of cmds) for (const w of messageWords(c)) stripped = stripped.replace(w.raw, " ");
  const sigN2 = sig && hasSignature(...payloadTexts(stripped));

  const written = new Set();
  for (const [i, s] of segs.entries()) {
    const c = cmds[i];
    const shell = RUNNERS.has(c.name);
    if ((shell || stdinShell(s)) && sig && (piped(i) || s.redirs.some((r) => ["<<<", "<", "<<", "<<-", "<>"].includes(r.op))))
      return block(`push/merge-shaped text piped or redirected into ${c.name || heads[i]} (G1)`);
    const code = c.args.findIndex((w) => CODE_FLAGS.test(w.text));
    if (INTERPRETERS.has(c.name) && code >= 0 && hasSignature(c.args[code + 1]?.text ?? "")) return block(`${c.name} inline code with the push/merge signature (G1)`);
    if (shell && c.args.some((w) => /(?:^|[^\w.])\.git\//.test(w.text))) return block(`${c.name} on a file under .git/ (G19b)`);
    if (reader(c) && s.sep?.startsWith("|") && [...RUNNERS, "xargs", "parallel"].includes(heads[i + 1])) return block("commit text piped into a shell, xargs or parallel (G19b)");
    if (sigN2 && (runsFile(c) || c.args.some((w) => w.raw.startsWith("<(")))) return block(`${c.word.text} runs a file in a push/merge-shaped payload (G13)`);
    const runs = [c.word?.text.includes("/") ? c.word.text : undefined, ...(RUNNERS.has(c.name) ? positional(c.args).slice(0, 1).map((w) => w.text) : [])].filter(Boolean);
    if (runs.some((p) => written.has(resolve(cwd, p)))) return block(`a file written earlier in the same call is run (G22)`);
    // make and npm-style runners execute recipes and scripts from files they find (#520 review).
    const recipe = RECIPE_RUNNERS[c.name];
    if (recipe && [...written].some((p) => recipe.test(base(p)))) return block(`${c.name} after a same-call write to its Makefile, .mk or package.json (G22)`);
    if (shell && piped(i) && DECODERS.some((d) => d(cmds[i - 1]))) return block(`decoded text piped into ${c.name} (base64 -d | sh)`);
    for (const p of writes(s, c, cwd)) written.add(p);
  }
  const planted = pathPlanting(segs, cmds, new Set([...prior, ...written]), cwd);
  if (planted) return block(planted);

  if (cmds.some((c) => COMMITTERS.has(gitSub(c).sub))) {
    const vector =
      subs(parsed).some((s) => !quotedHeredocCat(s)) ||
      segs.some((s, i) => {
        const h = heads[i];
        const args = s.words.slice(s.words.indexOf(head(s)) + 1);
        return s.redirs.some((r) => r.op === "<<<") || ["eval", "source", ".", "xargs", "parallel"].includes(h) ||
          (h === "find" && s.words.some((w) => /^-(?:exec|execdir|ok|okdir)$/.test(w.text))) ||
          (RUNNERS.has(h) && (piped(i) || s.redirs.some((r) => r.op === "<") || args.some((w) => CODE_FLAGS.test(w.text)) || (SHELLS.has(h) && !positional(args).length)));
      });
    if (vector) return block("a commit, tag or note is created in a payload that can re-execute text (G19)");
  }
  return undefined;
}
