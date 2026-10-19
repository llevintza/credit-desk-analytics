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
 *   runner after a same-call write to a Makefile, `.mk` or package.json.
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

/** Payload-level indirect-execution rules. `text` is the command; `cwd` resolves paths. */
export function replayCheck({ text, segs, parsed, sig, cwd }) {
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
    for (const r of s.redirs) if (WRITE_OPS.test(r.op)) written.add(resolve(cwd, r.target));
    const pos = positional(c.args).map((w) => w.text);
    if (c.name === "tee") pos.forEach((p) => written.add(resolve(cwd, p)));
    if (["cp", "mv", "ln", "install", "rsync"].includes(c.name) && pos.length > 1) written.add(resolve(cwd, pos.at(-1)));
    if (c.name === "sed" && c.args.some((w) => /^-[a-zA-Z]*i/.test(w.text))) pos.slice(1).forEach((p) => written.add(resolve(cwd, p)));
  }

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
