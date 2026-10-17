/**
 * `git push` (and `hub push`) analysis for the guard (adapters.md "git push … unless every
 * destination is provably not main"; testing.md G2, G3, G9, G11, G12, G17).
 *
 * A push passes only when every destination resolves to a branch other than `main` (any case)
 * or a tag. Destinations are resolved with read-only git calls (config, HEAD, symbolic names);
 * the guard never runs a push or a network call. Anything it can't prove fails closed.
 */
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { parse } from "./shell.mjs";

const base = (s) => s.slice(s.lastIndexOf("/") + 1);
const block = (reason) => ({ code: 2, reason });
const ok = { code: 0 };
const ASSIGN = /^[A-Za-z_]\w*=/;
// G3: these select the repo, its config, the global gitconfig or the git binary.
const GIT_ENV = /^(?:GIT_\w*|HOME|XDG_CONFIG_HOME|PATH)$/;
const RISKY_CONFIG = /^(?:remote|push|branch|alias|url|include|includeif)\.|^core\.hookspath$/i;
const SAFE_GLOBALS = new Set(["--no-pager", "-P", "-p", "--paginate", "--no-optional-locks", "--literal-pathspecs", "--no-replace-objects", "--version", "--help", "-v", "-h"]);

function runGit(args, cwd) {
  try {
    return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 400 });
  } catch (e) {
    if (e.status === 1) return null; // -q lookups: "not a symbolic ref", "no such ref"
    throw new Error(`git ${args[0]} lookup failed in ${cwd}`);
  }
}

/** Read-only, memoized lookups in one repo. `run` is injectable for tests. */
export function gitRepo(cwd, run = runGit) {
  const memo = new Map();
  const once = (k, f) => (memo.has(k) ? memo.get(k) : memo.set(k, f()).get(k));
  const config = () => once("config", () => {
    const m = new Map();
    for (const e of (run(["config", "--list", "-z"], cwd) ?? "").split("\0").filter(Boolean)) {
      const nl = e.indexOf("\n");
      const k = nl < 0 ? e : e.slice(0, nl);
      m.set(k, [...(m.get(k) ?? []), nl < 0 ? "true" : e.slice(nl + 1)]);
    }
    return m;
  });
  return {
    get: (k) => config().get(k)?.at(-1),
    all: (k) => config().get(k) ?? [],
    branch: () => once("branch", () => run(["symbolic-ref", "-q", "HEAD"], cwd)?.trim().replace(/^refs\/heads\//, "") ?? null),
    fullName: (src) => once(`f:${src}`, () => run(["rev-parse", "--verify", "-q", "--symbolic-full-name", "--end-of-options", src], cwd)?.trim() || null),
    symref: (ref) => once(`s:${ref}`, () => run(["symbolic-ref", "-q", ref], cwd)?.trim() || null),
    builtins: () => once("builtins", () => new Set((run(["--list-cmds=builtins"], cwd) ?? "").split("\n"))),
  };
}

/**
 * A git/hub segment as { tool, env, globals: { dir, bad }, sub, args } with aliases expanded
 * (`!` aliases come back as { shell }), or undefined when the segment isn't git or hub.
 */
export function gitCommand(seg, repoAt, cwd) {
  const at = seg.words.findIndex((w) => w.quoted || !ASSIGN.test(w.text));
  const h = seg.words[at];
  if (!h || h.dynamic || !["git", "hub"].includes(base(h.text))) return undefined;
  const env = seg.words.slice(0, at).filter((w) => ASSIGN.test(w.text)).map((w) => w.text.slice(0, w.text.indexOf("=")));
  const rest = seg.words.slice(at + 1);
  const g = { tool: base(h.text), env, dir: cwd, bad: [] };
  let i = 0;
  for (; i < rest.length; i++) {
    const t = rest[i].text;
    if (rest[i].dynamic) g.bad.push("a dynamic git option");
    if ((t === "-C" || t === "-c") && rest[i + 1]?.dynamic) g.bad.push(`git ${t} with a dynamic value`);
    if (t === "-C") g.dir = resolve(g.dir, rest[++i]?.text ?? "");
    else if (t === "-c") {
      const kv = rest[++i]?.text ?? "";
      if (RISKY_CONFIG.test(kv.split("=")[0])) g.bad.push(`git -c ${kv.split("=")[0]}`);
    } else if (/^--(?:git-dir|work-tree|namespace|config-env|exec-path)\b/.test(t)) g.bad.push(`git ${t.split("=")[0]}`);
    else if (t.startsWith("-")) { if (!SAFE_GLOBALS.has(t)) g.bad.push(`git global option ${t}`); }
    else break;
  }
  if (i >= rest.length) return { ...g, sub: undefined, args: [] };
  let sub = rest[i].text;
  let args = rest.slice(i + 1);
  const repo = repoAt(g.dir);
  for (let depth = 0; !["push", "send-pack"].includes(sub) && repo.get(`alias.${sub}`) !== undefined && !repo.builtins().has(sub); depth++) {
    const value = repo.get(`alias.${sub}`);
    if (depth > 8) throw new Error(`git alias ${sub} loops`);
    if (value.startsWith("!")) return { ...g, sub, args, shell: value.slice(1) };
    const expanded = parse(value).segments[0]?.words ?? [];
    if (!expanded.length) return { ...g, sub, args, shell: "" };
    [sub, args] = [expanded[0].text, [...expanded.slice(1), ...args]];
  }
  return { ...g, sub, args };
}

// G2: what an earlier command in the same payload may have changed.
const BRANCH_CHANGERS = new Set(["checkout", "switch", "bisect", "worktree", "symbolic-ref"]);
const CONFIG_CHANGERS = new Set(["config", "remote", "submodule"]);
const REFS_CHANGERS = new Set(["symbolic-ref", "update-ref", "worktree"]);
const CONFIG_READS = /^(?:--get|--get-all|--get-regexp|--list|-l|--show-origin|get|list)$/;
const REBASE_ARG = new Set(["--onto", "-s", "--strategy", "-X", "--strategy-option", "-x", "--exec"]);

function changes(seg, repoAt, cwd) {
  const out = new Set();
  const texts = seg.words.map((w) => w.text);
  if (texts.some((t) => ["cd", "pushd", "popd"].includes(t))) out.add("cwd");
  if (texts.some((t) => ASSIGN.test(t) && GIT_ENV.test(t.slice(0, t.indexOf("=")))) ||
    (["export", "unset", "declare", "typeset", "readonly", "local"].includes(texts[0]) && texts.some((t) => GIT_ENV.test(t))) ||
    ["source", "."].includes(texts[0])) out.add("environment");
  if (texts[0] === "gh" && texts[1] === "pr" && texts[2] === "checkout") out.add("HEAD");
  let c;
  try {
    c = gitCommand(seg, repoAt, cwd);
  } catch {
    return new Set([...out, "HEAD", "config", "refs"]);
  }
  if (!c) return out;
  if (c.shell !== undefined) return new Set([...out, "HEAD", "config", "refs"]);
  const a = c.args.map((w) => w.text);
  if (BRANCH_CHANGERS.has(c.sub) || (c.sub === "branch" && a.some((t) => /^(?:-[mMcC]|--move|--copy)$/.test(t)))) out.add("HEAD");
  if (c.sub === "rebase") {
    const pos = [];
    for (let i = 0; i < a.length; i++) if (REBASE_ARG.has(a[i])) i++; else if (!a[i].startsWith("-")) pos.push(a[i]);
    if (pos.length >= (a.includes("--root") ? 1 : 2)) out.add("HEAD");
  }
  if (CONFIG_CHANGERS.has(c.sub) && !(c.sub === "config" && a.some((t) => CONFIG_READS.test(t)))) out.add("config");
  if (REFS_CHANGERS.has(c.sub)) out.add("refs");
  return out;
}

// git push's long options (git 2.56 `git push -h`); each also has a `no-` form.
const LONG = ["all", "atomic", "branches", "exec", "follow-tags", "force-if-includes", "force-with-lease", "mirror", "porcelain", "progress", "prune",
  "receive-pack", "recurse-submodules", "repo", "signed", "tags", "thin", "verify", "ipv4", "ipv6", "delete", "force", "dry-run", "push-option", "quiet", "set-upstream", "verbose"];
const NAMES = [...LONG, ...LONG.map((n) => `no-${n}`)];
const BLOCKED = new Set(["all", "branches", "mirror", "force", "force-with-lease", "force-if-includes", "no-verify", "repo", "prune", "exec", "receive-pack", "recurse-submodules"]);
const VALUED = new Set(["exec", "receive-pack", "repo", "push-option", "recurse-submodules"]);

/** Expand an abbreviated long option as git does (G12): exact, else a unique prefix. */
export function expandOption(name) {
  if (NAMES.includes(name)) return name;
  const hits = NAMES.filter((n) => n.startsWith(name));
  return hits.length === 1 ? hits[0] : null;
}

/** git push options: { block } or { del, positional: [word] }. */
export function pushOptions(args) {
  const positional = [];
  let del = false;
  for (let i = 0; i < args.length; i++) {
    const w = args[i];
    const t = w.text;
    if (w.dynamic || /[$`]/.test(t)) return { block: `a dynamic push argument ${w.raw}` };
    if (t === "--") { positional.push(...args.slice(i + 1)); break; }
    if (t.startsWith("--")) {
      const [n, v] = [t.slice(2).split("=")[0], t.includes("=")];
      const full = expandOption(n);
      if (!full) return { block: `an ambiguous or unknown git push option --${n} (G12)` };
      if (BLOCKED.has(full)) return { block: `git push --${full} (G9, G12)` };
      if (full === "delete") del = true;
      if (VALUED.has(full) && !v) i++;
    } else if (t.startsWith("-") && t.length > 1) {
      for (let j = 1; j < t.length; j++) {
        const c = t[j];
        if (c === "f") return { block: "git push -f (G9)" };
        if (c === "d") del = true;
        else if (c === "o") { if (j === t.length - 1) i++; break; }
        else if (!"46nquv".includes(c)) return { block: `an unknown git push option -${c}` };
      }
    } else positional.push(w);
  }
  return { del, positional };
}

/** The verdict for one resolved destination (a full ref or a plain branch name). */
function destination(ref) {
  if (/^refs\/tags\//.test(ref)) return ok;
  if (ref.startsWith("refs/") && !ref.startsWith("refs/heads/")) return block(`a push to the non-branch ref ${ref}`);
  const name = ref.replace(/^refs\/heads\//, "").replace(/^heads\//, "");
  if (!name || /^(?:HEAD|@)$/.test(name) || /[*?[\\~^:]|@\{|\.\./.test(name)) return block(`an unprovable destination ${ref}`);
  return name.toLowerCase() === "main" ? block("a push to main") : ok;
}

/** Map a full src ref through remote.<r>.push as git does (G11). */
function mapped(full, rules) {
  for (const r of rules) {
    const [s, d = s] = r.split(":");
    if (!s.includes("*")) { if (s === full) return d; continue; }
    const [pre, post] = s.split("*");
    if (full.startsWith(pre) && full.endsWith(post) && full.length >= pre.length + post.length) return d.replace("*", full.slice(pre.length, full.length - post.length));
  }
  return full;
}

function analysePush(c, repo, state) {
  const o = pushOptions(c.args);
  if (o.block) return block(o.block);
  const [remoteWord, ...specs] = o.positional;
  if (remoteWord && remoteWord.glob) return block(`a glob remote ${remoteWord.raw}`);
  const needsHead = () => {
    if (state.has("HEAD")) return "HEAD (an earlier command changes the branch)";
    if (repo.branch() === null) return "HEAD (detached)";
    return null;
  };
  if (!remoteWord) { const why = needsHead(); if (why) return block(`the push remote depends on ${why}`); }
  const b = repo.branch();
  const remote = remoteWord?.text ?? repo.get(`branch.${b}.pushremote`) ?? repo.get("remote.pushdefault") ?? repo.get(`branch.${b}.remote`) ?? "origin";
  if (repo.get(`remote.${remote}.mirror`) === "true") return block(`remote.${remote}.mirror is set`);
  const rules = repo.all(`remote.${remote}.push`);
  if (rules.some((r) => r.startsWith("+"))) return block(`remote.${remote}.push forces (+)`);
  const mode = repo.get("push.default") ?? "simple";
  if (!specs.length) {
    if (rules.length) return block(`remote.${remote}.push is set and the push names no refspec`);
    if (["simple", "current"].includes(mode)) return destination(b);
    if (["upstream", "tracking"].includes(mode)) return repo.get(`branch.${b}.merge`) ? destination(repo.get(`branch.${b}.merge`)) : block(`branch.${b}.merge is not set`);
    return mode === "nothing" ? ok : block(`push.default=${mode}`);
  }
  for (const w of specs) {
    const t = w.text;
    if (w.glob || /[*$`]/.test(t)) return block(`a wildcard or dynamic refspec ${w.raw}`);
    if (t.startsWith("+")) return block("a + (force) refspec (G9)");
    let d;
    if (o.del || t.startsWith(":")) d = destination(o.del ? t : t.slice(1));
    else if (t.includes(":")) d = destination(t.slice(t.indexOf(":") + 1));
    else {
      let full;
      if (t === "HEAD" || t === "@") {
        const why = needsHead();
        if (why) return block(`the destination depends on ${why}`);
        full = `refs/heads/${b}`;
      } else {
        if (state.has("refs")) return block("the source ref depends on refs an earlier command changes");
        full = repo.fullName(t);
        if (!full) {
          if (!/^[\w./-]+$/.test(t) || t.startsWith("refs/") || /^(?:heads|tags|remotes)\//.test(t)) return block(`an unresolvable source ${t}`);
          full = `refs/heads/${t}`;
        }
        full = repo.symref(full) ?? full;
      }
      const name = full.replace(/^refs\/heads\//, "");
      const upstream = ["upstream", "tracking"].includes(mode) && full.startsWith("refs/heads/") ? repo.get(`branch.${name}.merge`) : undefined;
      d = destination(rules.length ? mapped(full, rules) : upstream ?? full);
    }
    if (d.code === 2) return d;
  }
  return ok;
}

// G17: git and HTTP-client config files, and hook writes.
const CONFIG_FILES = /(?:^|\/)\.git\/(?:config|HEAD|refs(?:\/|$)|packed-refs)|\.gitconfig$|(?:^|\/)\.config\/git(?:\/|$)|(?:^|\/)\.(?:curlrc|wgetrc)$/;
const HOOK_WRITERS = new Set(["tee", "cp", "mv", "ln", "install", "chmod", "rsync", "sed", "dd"]);

/** Payload-level rules (G17). `sig` is whether the payload carries the push signature. */
export function payloadCheck({ segs, sig }) {
  for (const s of segs) {
    const texts = [...s.words.map((w) => w.text), ...s.redirs.map((r) => r.target)];
    if (texts.some((t) => /core\.hookspath/i.test(t))) return block("core.hooksPath is set or read (G17)");
    const hooks = (t) => /(?:^|\/)\.git\/hooks(?:\/|$)/.test(t);
    if (s.redirs.some((r) => /^(?:>|>>|>\||&>|&>>|<>)$/.test(r.op) && hooks(r.target)) ||
      (s.words.some((w) => HOOK_WRITERS.has(base(w.text))) && s.words.some((w) => hooks(w.text)))) return block("a git hook is written (G17)");
    if (sig && texts.some((t) => CONFIG_FILES.test(t))) return block("a push/merge-shaped payload that touches a git or HTTP-client config file (G17)");
  }
  return undefined;
}

/** The segment analyser (decide.mjs ANALYSERS). */
export function analyseGit(seg, ctx) {
  const cwd = ctx.call.cwd ?? process.cwd();
  const repoAt = ctx.repoAt ?? ((d) => gitRepo(d));
  let c;
  try {
    c = gitCommand(seg, repoAt, cwd);
  } catch (e) {
    return block(`git lookups failed (${e.message})`);
  }
  if (!c) return undefined;
  if (c.bad.length) return block(`${c.bad[0]} makes the destination unprovable (G3)`);
  if (c.env.some((v) => GIT_ENV.test(v))) return block(`${c.env.find((v) => GIT_ENV.test(v))}= makes the destination unprovable (G3)`);
  if (c.shell !== undefined) {
    const d = ctx.evaluate?.(c.shell);
    return d?.code === 0 ? ok : block(`the git alias ${c.sub} runs a shell command the guard can't prove safe`);
  }
  if (c.sub === "send-pack") return block("git send-pack");
  if (c.tool === "hub" && c.sub !== undefined && !repoAt(c.dir).builtins().has(c.sub)) return block(`hub ${c.sub} isn't analysed`);
  if (c.sub !== "push") return ok;
  const top = ctx.parsed.segments.length;
  const earlier = ctx.segs.filter((s, j) => j !== ctx.index && (j < Math.min(ctx.index, top) || j >= top));
  const state = new Set(earlier.flatMap((s) => [...changes(s, repoAt, cwd)]));
  for (const cause of ["cwd", "environment", "config"]) if (state.has(cause)) return block(`an earlier command changes the ${cause}, so the push destination is unprovable (G2)`);
  try {
    return analysePush(c, repoAt(c.dir), state);
  } catch (e) {
    return block(`git lookups failed (${e.message})`);
  }
}
