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

const cmds = (src) => allSegments(parse(src)).map((s) => s.words.map((w) => w.text).join(" "));

test("${…} bodies are parsed: substitutions inside run (#510 review)", () => {
  assert.ok(cmds("echo ${x:-$(git push --force origin main)}").includes("git push --force origin main"));
  assert.ok(cmds("echo ${x:=`git push`}").includes("git push"));
  assert.ok(cmds("echo ${x/a/$(git push)}").includes("git push"));
  assert.ok(cmds("echo ${a:-${b:-$(git push)}}").includes("git push"));
  assert.ok(cmds('echo "${x:-$(git push)}"').includes("git push"));
  assert.ok(cmds(`echo "\${x:-'$(git push)'}"`).includes("git push")); // ' is literal inside "…"
  assert.ok(cmds(`echo "\${x:-"$(git push)"}"`).includes("git push"));
  assert.ok(!cmds("echo ${x:-'$(git push)'}").includes("git push")); // quoted: inert
  assert.ok(!cmds("echo ${x:-\\$(git push)}").includes("git push"));
  for (const s of ["echo ${x:-$(git push", "echo ${x:-'a}", `echo "\${x:-"a}"`]) assert.throws(() => parse(s), ShellError, s);
});

test("$((…)) bodies are parsed; $((cmd) ) throws (#510 review)", () => {
  assert.ok(cmds("echo $(( $(git push) + 1 ))").includes("git push"));
  assert.ok(cmds("echo $(( 1 + `git push` ))").includes("git push"));
  assert.deepEqual(cmds("echo $(( (1+2) * 3 )) $((a\\ ))"), ["echo $(( (1+2) * 3 )) $((a\\ ))"]);
  for (const s of ["echo $((git push) )", "echo $(( '1' ))", "echo $(( 1 + 2 )"]) assert.throws(() => parse(s), ShellError, s);
});

test("unquoted heredoc bodies are expanded; quoted ones stay inert (#510 review)", () => {
  assert.ok(cmds("cat <<EOF\n$(git push origin main)\nEOF").includes("git push origin main"));
  assert.ok(cmds("cat <<-EOF\n\t`git push`\n\tEOF").includes("git push"));
  assert.ok(cmds("cat <<EOF\n${x:-$(git push)} it's\nEOF").includes("git push"));
  assert.ok(cmds("cat <<EOF\n$(cat <<IN\n$(git push)\nIN\n)\nEOF").includes("git push"));
  for (const q of ["'EOF'", '"EOF"', "\\EOF", "E'O'F"]) assert.ok(!cmds(`cat <<${q}\n$(git push)\nEOF`).includes("git push"), q);
  assert.ok(!cmds("cat <<EOF\n\\$(git push) \\`x\\`\nEOF").includes("git push"));
  assert.throws(() => parse("cat <<EOF\n$(git push\nEOF"), ShellError);
});

test("leading reserved words are dropped so the first word is the command (#510 review)", () => {
  assert.deepEqual(words("if git push; then git status; elif a; else b; fi"), [["git", "push"], ["git", "status"], ["a"], ["b"]]);
  assert.deepEqual(words("! git push; { cd ..; git push; }; while x; do y; done; until z; do w; done"), [["git", "push"], ["cd", ".."], ["git", "push"], ["x"], ["y"], ["z"], ["w"]]);
  assert.deepEqual(words("'if' x; \\! y; for b in a; do git push; done"), [["if", "x"], ["!", "y"], ["for", "b", "in", "a"], ["git", "push"]]);
});
