import { test } from "node:test";
import assert from "node:assert/strict";
import { ShellError, allSegments, parse } from "../hooks/lib/shell.mjs";

const words = (src) => allSegments(parse(src)).map((s) => s.words.map((w) => w.text));

test("segments split on ; && || | |& & newlines and parentheses", () => {
  assert.deepEqual(words("a 1; b && c || d | e |& f & g\nh (i) ;; j"), [["a", "1"], ["b"], ["c"], ["d"], ["e"], ["f"], ["g"], ["h"], ["i"], ["j"]]);
  assert.deepEqual(parse("a | b").segments.map((s) => s.sep), ["|", "eof"]);
  assert.deepEqual(parse("a;").segments.map((s) => s.sep), [";"]);
  assert.deepEqual(parse("; a").segments.map((s) => s.sep), ["eof"]);
});

test("quotes, escapes and line continuations", () => {
  assert.deepEqual(words(`gi"t" 'pu'sh "a \\"b\\" \\$c \\x" d\\ e f\\\ng`), [["git", "push", 'a "b" $c \\x', "d e", "fg"]]);
  assert.deepEqual(words(`$"x y"`), [["x y"]]);
  assert.equal(parse("'a'").segments[0].words[0].quoted, true);
  assert.equal(parse("a\\").segments[0].words[0].text, "a");
  assert.deepEqual(words('a "b\\\nc"'), [["a", "bc"]]);
  assert.deepEqual(words("a \\\nb"), [["a", "b"]]);
});

test("$'…' is decoded (G4)", () => {
  assert.deepEqual(words("$'\\x67it' $'p\\165sh' $'\\u006d\\U00000061in' $'\\t\\n\\\\\\'\\q'"), [["git", "push", "main", "\t\n\\'\\q"]]);
});

test("expansions mark the word dynamic and parse into subs", () => {
  const r = parse('echo "$(git push origin main)" `id` $X ${Y} $((1+2)) $1 $ <(cat f)');
  assert.deepEqual(r.segments[0].words.map((w) => w.dynamic), [false, true, true, true, true, true, true, false, true]);
  assert.deepEqual(r.subs.map((s) => s.kind), ["cmd", "backtick", "var", "var", "arith", "var", "procsub"]);
  assert.deepEqual(words("echo $(a $(b) `c \\`d\\``)"), [["echo", "$(a $(b) `c \\`d\\``)"], ["a", "$(b)", "`c \\`d\\``"], ["b"], ["c", "`d`"], ["d"]]);
  assert.deepEqual(words("a $(b (c) d)"), [["a", "$(b (c) d)"], ["b"], ["c"], ["d"]]);
});

test("redirections are split from words, fd prefixes included", () => {
  const s = parse("sh<.t.sh; bash 0< f 2>&1 >>out &>e <<<'x y' >| p").segments;
  assert.deepEqual(s.map((x) => x.words.map((w) => w.text)), [["sh"], ["bash"]]);
  assert.deepEqual(s[1].redirs.map((r) => [r.fd, r.op, r.target]), [["0", "<", "f"], ["2", ">&", "1"], ["", ">>", "out"], ["", "&>", "e"], ["", "<<<", "x y"], ["", ">|", "p"]]);
  assert.throws(() => parse("a >"), ShellError);
});

test("heredocs: quoted, unquoted, <<- and inside $(…) (G8)", () => {
  const r = parse("cat <<'EOF' > f\nit's $x\nEOF\ncat <<-E2; echo after\n\tbody\n\tE2\n");
  assert.deepEqual(r.heredocs.map((h) => [h.delim, h.quoted, h.body]), [["EOF", true, "it's $x"], ["E2", false, "body"]]);
  assert.deepEqual(words("cat <<A <<B\na\nA\nb\nB\nnext"), [["cat"], ["next"]]);
  const c = parse(`git commit -m "$(cat <<'EOF'\nfeat: x (it's done)\nEOF\n)" && ls`);
  assert.deepEqual(c.segments.map((s) => s.words[0].text), ["git", "ls"]);
  assert.equal(c.heredocs[0].body, "feat: x (it's done)");
  assert.throws(() => parse("cat <<EOF\nno end"), /unterminated heredoc EOF/);
  assert.throws(() => parse("cat <<EOF"), /unterminated heredoc/);
});

test("# starts a comment only at a word start", () => {
  assert.deepEqual(words("ls # git push origin main\necho a#b"), [["ls"], ["echo", "a#b"]]);
});

test("glob and brace words are marked", () => {
  assert.deepEqual(parse("ls g* a? [x] {git,gh} {a} 'q*'").segments[0].words.map((w) => w.glob), [false, true, true, true, true, false, false]);
});

test("unbalanced input throws ShellError", () => {
  for (const s of ["'a", '"a', "$'a", "`a", "$(a", "a )", "$((1", "${a"]) assert.throws(() => parse(s), ShellError, s);
});

test("a deadline in the past stops the parse", () => {
  assert.throws(() => parse("a b c", 0), /deadline/);
});
