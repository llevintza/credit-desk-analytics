// Indirect-execution rows (testing.md G1, G4, G13, G19–G22, N2). REBUILT: CR8's V53–V57 and
// CR9's W/X/H rows are not in the repo; these are the docs' examples, by requirement.
import { test } from "node:test";
import assert from "node:assert/strict";
import { copyFileSync, mkdtempSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { decide } from "../hooks/lib/decide.mjs";
import { globNamesGuarded, guardedBinary, unwrap } from "../hooks/lib/indirect.mjs";
import { parse } from "../hooks/lib/shell.mjs";
import { fakeRepo } from "./helpers.mjs";

const code = (command, cwd) => decide(JSON.stringify({ tool_name: "Bash", tool_input: { command }, ...(cwd && { cwd }) }), { repoAt: fakeRepo() }).code;
// Every shell shape: Claude, Cursor-imported, Cursor-native and Grok (#521, #525).
const SHAPES = [
  (command) => ({ tool_name: "Bash", tool_input: { command } }), (command) => ({ tool_name: "Shell", tool_input: { command } }),
  (command) => ({ hook_event_name: "beforeShellExecution", command }), (command) => ({ toolName: "run_terminal_command", toolInput: { command } }),
];
const rows = (list, want) => {
  for (const c of list) for (const [i, shape] of SHAPES.entries()) assert.equal(decide(JSON.stringify(shape(c)), { repoAt: fakeRepo() }).code, want, `shape ${i}: ${c}`);
};

test("wrappers and shell strings are unwrapped (G1)", () => {
  rows([
    "nice -n 5 git push origin main", "nice -5 git push origin main", "timeout -s KILL -k 5 10 git push origin main", "setsid -f git push origin main",
    "flock /tmp/l git push origin main", "flock -w 1 /tmp/l -c 'git push origin main'", "sudo -u x git push origin main", "env -S 'git push origin main'",
    "env -Sgit\\ push\\ origin\\ main", "env -i GIT_DIR=x git push origin feat", "bash -c -- 'git push origin main'", "sh -lc 'gh pr merge 1'", "nohup git push origin main &",
    "time -p git push origin main", "command git push origin main", "exec -a x git push origin main", "builtin cd .. && git push", "stdbuf -o0 git push origin main",
    "ionice -c 3 git push origin main", "busybox sh -c 'git push origin main'", "eval 'git push origin main'", "eval git push origin main", "env -- git push origin main",
    "bash -c \"bash -c 'git push origin main'\"", "x() { git push origin main; }; x", "eval echo hi", "env eval ls",
  ], 2);
  rows(["nice -n 5 git push origin feat", "env git push -u origin feat", "bash -c 'git push origin feat'", "timeout 60 npm test", "command -v git",
    "xargs -n1 echo < f", "find . -name x -exec rm {} \\;", "git ls-files | xargs git log -1", "find . -exec git status \\;", "sudo -- ls", "env", "bash -c"], 0);
  assert.deepEqual(unwrap(parse("A=1 env B=2 -u C nice git x").segments[0]).words.map((w) => w.text), ["A=1", "B=2", "git", "x"]);
  assert.deepEqual(unwrap(parse("X=1").segments[0]).words.map((w) => w.text), ["X=1"]);
});

test("pipes, here-strings and redirections into a shell or interpreter (G1)", () => {
  rows(["echo 'git push origin main' | bash", "printf 'gh pr merge 1' | sh", "echo 'git push' |& node", "bash <<< 'git push origin main'",
    "git push origin feat; sh<.t.sh", "git push origin feat; bash 0< f", "bash <<EOF\ngit push origin feat\nEOF",
    `node -e "require('child_process').execSync('git push origin main')"`, `python3 -c 'import os; os.system("gh pr merge 1")'`, "perl -e 'system q(git push)'"], 2);
  rows(["echo hi | bash", "node -e 'console.log(1)'", "cat README.md | wc -l"], 0);
});

test("dynamic, escaped and glob command words (G4, G20)", () => {
  rows(["$G push origin main", '"$G" status', "$(npm bin)/eslint .", '"$EDITOR" f', "`which git` push", "$'\\x67it' push origin feat", "g=$(printf git); $g origin main",
    "echo main | xargs git push origin", "ls | xargs -I{} git push origin {}", "find . -name '*.sh' -exec git push origin main \\;", "parallel git push origin ::: main",
    "xargs $CMD", "find . -exec {} \\;", "ls | xargs gh pr view {}", "ls git*", "/usr/bin/g?t push origin main", "{git,x} status", "ls < g*", "xargs curl < u"], 2);
  rows(["$'\\x6cs' -la", "ls *.md", "rm -f *", "echo h{a,b}t", "ls | xargs gh pr view"], 0);
  assert.equal(globNamesGuarded({ text: "[g]it" }), true);
  assert.equal(globNamesGuarded({ text: "w[!g]et" }), false);
});

test("the test builtins [ and [[ are command words, not globs (live over-block)", () => {
  rows(['until npm ci || [ "$?" = 8 ]; do :; done', "[ -f a ] && echo y", '[[ -n "$X" ]] && echo y', "if [ -d src ]; then ls; fi", "x=1 [ -f a ]",
    "[ $X ]", "command [ -f a ]", 'while [ "$n" -lt 3 ]; do n=1; done'], 0);
  const E = "." + "env";
  rows(["[ -f a ] && git push --force origin main", "[[ -n x ]] || gh pr merge 1", "[ -f a ] && $G push origin main", "[g]it push origin main", "[ab] x",
    "[a]", "[[x]] y", `[ -f a ] && cat ${E}`, "[ -f a ] && echo 'git push origin main' | bash", "[ x ] && g[i]t status"], 2);
  // Helms's bar on #575: a command after a test, a substitution inside one, and [a-z] globs are all still caught.
  rows(["[ -f x ] && rm g*", "[ -f x ] && rm /usr/bin/g[a-z]t", "[ -f x ] && $RM x", "[ -f x ] && rm a; cat secrets/k", `[ -f x ] && rm a; cat ${E}`,
    "[[ -n $(git push --force origin main) ]]", "[[ -z `gh pr merge 1` ]]", '[ -n "$(git push origin +main)" ]', "[[ $(gh pr merge 1) == x ]] && echo y",
    "[a-z]", "[a-z]it status", "[ -f x ] && [a-z]* x", "[ x ] && ls [a-z]url", "[[ x ]] && /usr/bin/[a-z]it push origin main"], 2);
});

test("renamed or copied guarded binaries are resolved by identity (G21)", () => {
  const git = execFileSync("bash", ["-c", "command -v git"], { encoding: "utf8" }).trim();
  const dir = mkdtempSync(join(tmpdir(), "agents-g21-"));
  copyFileSync(git, join(dir, "x"));
  symlinkSync(git, join(dir, "l"));
  assert.equal(guardedBinary("x", dir), "git");
  assert.equal(guardedBinary("l", dir), "git");
  assert.equal(guardedBinary("missing", dir), undefined);
  assert.equal(guardedBinary(".", dir), undefined);
  assert.equal(code("./x push origin main", dir), 2);
  assert.equal(code(`${dir}/l push origin main`), 2);
  assert.equal(code("./x status", dir), 0);
  rows([`cp ${git} /tmp/x`, "cp $(command -v git) x", "cp g* x", "ln -s /usr/bin/gh x", "install -m 755 curl x"], 2);
  rows(["mv a b", "cp -r src dst"], 0);
  // B6 over-block report: a quoted dynamic destination with a literal last component passes; an
  // unquoted one can split into a source, and a wholly dynamic one could be git itself (#525).
  rows(['cp a.json "$D/b.json"', 'cp a.json "$D"/b.json', 'mv -f x "${OUT}/x"', 'install -m 644 a "$P/a"', "cp a '$D'"], 0);
  rows(['cp evil.sh "$D"', 'D=/usr/bin/git; cp a "$D" && git status', 'cp a "$(command -v git)"', 'mv a "$G"', 'mv -f x "${OUT}"', 'install -m 644 a "$P"',
    'cp a "$D/$N"', 'cp a "$D/`id`"', 'cp evil.sh "$D/git"', 'cp evil.sh "$HOME/.local/bin/git"', 'ln -sf evil "$D/gh"', "cp evil.sh ~/.local/bin/git", 'cp a "$D/Git"',
    'cp --target-directory="$D" a', 'rsync a "$D"'], 2);
  rows(["cp a.json $D/b.json", 'cp "$S" b', 'cp a "$D/git"', 'cp -t "$D" a', 'cp --target-directory=$D a', 'cp -t$D a', 'cp a "$D"/*', `cp ${git} "$D"`], 2);
});

test("indirect execution in a push/merge-shaped payload (G13) and the N2 message carve-out", () => {
  rows(["git push origin feat && sh f", "git push origin feat; bash f", "git push origin feat; source f", "git push origin feat; . f", "git push origin feat && ./x",
    "git push origin feat && /opt/x", "git push origin feat && python3 x.py", "git push origin feat; bash <(echo hi)", "echo 'git push origin feat' > f; sh f"], 2);
  rows(["./scripts/test.sh", "bash scripts/ci.sh", "git push -u origin feat && npm test"], 0);
  rows([`git commit -m "docs: git push flow" && ./scripts/test.sh`, "git commit -am 'fix: gh pr merge text' && node --test",
    `git commit --message="feat: push" && bash scripts/ci.sh`, "git tag -m 'git push' v1 && ./x", "git commit -m'git push' && ./x", "git notes add -m 'git push' && ./x"], 0);
  rows(["git log -1 --format=%B > p.sh && sh p.sh", "git log -1 --format=%B | sh", "git log -1 --format=%B | tee p.sh; bash p.sh",
    `git commit -m "git push" > out.txt && ./x`], 2);
});

test("commit-message replay (G19, G19b) and write-then-run (G22)", () => {
  rows([`git commit -m "$(echo x)"`, "git commit -m \"`date`\"", `git tag -m "$(id)" v1`, "git commit -m x && eval y", "git tag v1 && bash <<< 'x'", "git commit -m x && sh", "git commit -m x && node -e 1",
    "git commit -m x && echo y | python3", "git stash && xargs ls < f", "git commit -m x && find . -exec ls \\;", "git commit -m x && cat <(ls)",
    "git show HEAD:x.sh | bash", "git log --format=%s | xargs echo", "bash .git/x", "node .git/hooks/x", `python3 -c "print(open('.git/COMMIT_EDITMSG').read())"`,
    "echo 'echo hi' > x.sh && sh x.sh", "cat > t.sh <<'EOF'\nls\nEOF\nbash t.sh", "cp a.sh b.sh && ./b.sh", "printf x | tee run.sh && . run.sh",
    "sed -i s/a/b/ s.sh && sh s.sh", "echo x >> ./y && ./y", "git commit -m x && sh < f"], 2);
  rows([`git commit -m "$(cat <<'EOF'\nfeat: x\n\nmentions parallel and eval\nEOF\n)"`, "git commit -m 'mentions parallel'", "git commit -F msg.txt",
    "git stash && git stash pop", "sh x.sh && echo > x.sh", "echo x > out.txt && cat out.txt", "git commit -m x && bash scripts/ci.sh"], 0);
});

test("#520 review: same-call aliases, make/npm write-then-run, xargs stdin reads, decoded text into a shell", () => {
  rows(["shopt -s expand_aliases\nalias g=git\ng push --force origin main", "shopt -s expand_aliases; alias g=git\ng push --force origin main", "alias g='git push'\ng --force origin main", "alias s='sudo ' g=git\ns g push origin main",
    "alias p='gh pr'\np merge 5", "alias ls='ls -l'\nalias x=ls\nx; alias y='git push origin main'\ny"], 2);
  rows(["alias ll='ls -l'\nll", "alias", "alias g=git\ng status", "alias ls='ls -G'\nls"], 0);
  rows(["printf 'x:\\n\\tgit push --force origin main\\n' > Makefile && make x", "printf 'x:\\n\\tgit push -f origin main\\n' > b.mk && make -f b.mk x",
    "cat > GNUmakefile <<'EOF'\nx:\n\tls\nEOF\nmake x", "printf 'x:\\n\\tgit push --force origin main\\n' > GNUmakefile && make",
    `printf '{"scripts":{"x":"git push --force origin main"}}' > package.json && npm run x`, `echo '{"scripts":{"x":"git push --force origin main"}}' > package.json && npm run x`,
    "jq . p > web/package.json && cd web && npm test", "echo x > .npmrc && npx y", "printf x | tee Makefile; gmake"], 2);
  rows(["make test", "npm run test", "echo x > out.txt && make", "npm test && echo done > log.txt", "make -C web build"], 0);
  const E = "." + "env";
  rows([`echo ${E} | xargs cat`, `xargs cat <<< ${E}`, `printf ${E} | xargs -I{} cat {}`, "echo secrets/k | xargs cat", `find . -name ${E} | xargs head`,
    `printf '${E}\\n' | xargs cat`, `xargs cat <<EOF\n${E}\nEOF`, `echo web/${E}.dev | parallel cat`], 2);
  rows(["echo README.md | xargs cat", `echo ${E}.example | xargs cat`, "ls | xargs cat", `echo ${E} | xargs echo`], 0);
  rows(["echo Z2l0 | base64 -d | sh", "base64 --decode f | bash", "base64 -D < f | sh", "openssl base64 -d -in f | sh", "xxd -r -p f | bash"], 2);
  rows(["echo aGk= | base64 -d", "base64 -d f > out.bin", "echo hi | base64 | sh"], 0);
});

test("#521 item 5: a push/merge string piped or redirected into xargs/parallel running a shell (G1)", () => {
  rows([`echo "'gh pr merge 1'" | xargs sh -c`, "printf 'gh pr merge 1' | xargs -d '\\n' sh -c", "printf 'gh pr merge 1\\0' | xargs -0 sh -c",
    `echo "'git push -f origin main'" | xargs bash -c`, "printf 'git push -f origin main' | xargs -0 bash -c", "printf 'gh pr merge 1' | parallel",
    "printf 'gh pr merge 1' | parallel -j 2", "xargs -0 sh -c <<< 'gh pr merge 1'", "printf 'gh pr merge 1' | xargs -0 node -e", "printf 'gh pr merge 1' | nice parallel"], 2);
  rows(["echo a | xargs echo", "echo hi | xargs sh -c 'echo $0'", "ls | parallel -j 2 gzip", "echo a | xargs -I{} sh -c 'echo {}'", "git push -u origin feat",
    "find . -name '*.ts' -exec grep x {} \\;", "grep -rn KEY src/", "cat .env.example", "ls .env*"], 0);
});

test("#576: PATH planting, enable -n/-f and hash -p re-point a command name (G22)", () => {
  const W = "printf '#!/bin/sh\\ngit push --force origin main\\n' > bin/ls && chmod +x bin/ls";
  rows([`${W} && PATH=bin:$PATH ls`, `${W} && PATH=bin:$PATH env ls`, `${W} && PATH=bin:$PATH env test x`, `${W} && env PATH=bin:$PATH ls`,
    `${W}; export PATH=bin:$PATH; ls`, `${W}; PATH=bin:$PATH; ls`, `${W}; export PATH="$PWD/bin:$PATH"; ls`, `${W} && PATH=./bin:$PATH ls`], 2);
  // A relative, empty, `~` or dynamic entry fails closed even with no write here: the file can come from an earlier call.
  rows(["PATH=bin:$PATH ls", "PATH=:$PATH ls", "PATH=.:$PATH ls", "PATH=~/bin:$PATH ls", "PATH=$HOME/bin:$PATH ls", "PATH+=:bin ls", "export PATH=$PATH:bin; ls",
    "declare -x PATH=bin:$PATH; ls", "sudo PATH=bin:$PATH ls", "env -i PATH=bin ls", 'PATH="bin:$PATH" npm test',
    "path=(bin $path); ls", "path+=(bin); ls", "path=bin:$path; ls"], 2);
  // An absolute directory fails closed when this payload (or a shell string in it) writes under it.
  rows(["printf x > /tmp/pp/ls; PATH=/tmp/pp:$PATH ls", "cp /usr/bin/git /tmp/pp/ls && PATH=/tmp/pp:$PATH ls", "printf x > /tmp/pp/ls; bash -c 'PATH=/tmp/pp:$PATH ls'",
    "printf x > /tmp/pp/ls; PATH=/tmp/pp:$PATH ./run.sh", "tee /tmp/pp/git < f; export PATH=/tmp/pp:$PATH; git status"], 2);
  rows(["enable -n [", "enable -n test", "enable -n echo; echo x", "enable -f ./x.so x", "builtin enable -n test", "enable -an", "hash -p /tmp/x ls", "hash ls=/tmp/x"], 2);
  rows(["PATH=/usr/bin:$PATH ls", "export PATH=/opt/homebrew/bin:$PATH; brew list", "PATH=/usr/local/bin:/usr/bin:/bin npm test", "printf x > out.txt && PATH=/usr/bin:$PATH ls",
    "printf x > /tmp/a/f && PATH=/tmp/b:$PATH ls", "echo $PATH", "echo PATH=bin:$PATH", "export PATH", "PATH=bin:$PATH", "hash", "hash -r", "enable", "enable -a",
    "export FOO=bar; ls", "grep PATH= .bashrc", "env | grep PATH", "printenv PATH"], 0);
});

test("#578 item 1: a quoted PATH= argument to a declarer, env or sudo still sets PATH (G22)", () => {
  rows(["export 'PATH=bin:'$PATH; ls", "declare 'PATH=bin'; ls", 'typeset "PATH=bin"; ls', "f() { local 'PATH=bin'; ls; }; f", "readonly 'PATH=bin:'$PATH; ls",
    "export 'PATH'=bin; ls", "env 'PATH=bin' ls", "sudo 'PATH=bin' ls", "export 'path=bin'; ls",
    // A single-quoted or escaped $PATH is a literal relative directory named `$PATH`.
    "export 'PATH=$PATH:/usr/bin'; ls", "PATH='$PATH':/usr/bin ls", "PATH=\\$PATH:/usr/bin ls"], 2);
  rows(['export "PATH=/opt/homebrew/bin:$PATH"; brew list', "export 'PATH=/usr/bin:/bin'; ls", "env 'PATH=/usr/bin' ls"], 0);
});

test("#578 item 2: PATH set without an assignment word fails closed before a command (G22)", () => {
  rows(["printf -v PATH %s bin; ls", "printf -vPATH %s bin; ls", "read -r PATH <<< bin; ls", "read PATH < f; ls", "IFS= read PATH < f; ls", "IFS= read -r PATH <<< bin && ls",
    "read -a path <<< bin; ls", "mapfile -t PATH < f; ls", "readarray path < f; ls", "for PATH in bin; do ls; done", "select path in bin; do ls; done",
    "while getopts b PATH; do ls; done", "unset PATH; ls", "unset -v PATH && ls", "f() { local PATH; ls; }; f", "declare -n r=PATH; r=bin; ls", "typeset -n r=path; r=(bin); ls",
    "let PATH=1; ls", "(( PATH = 1 )); ls", ": $((PATH=1)); ls", ": ${PATH::=bin}; ls", ": ${PATH:=bin}; ls",
    // eval is banned outright (indirect.mjs), so these block before G22 sees them.
    "eval 'PATH=bin'; ls", 'eval "export PATH=bin"; ls'], 2);
  // Reading PATH, or setting it with nothing after, is not planting.
  rows(["printenv PATH", "echo PATH", "printf '%s\\n' PATH; ls", "export PATH; ls", "readonly PATH; ls", "grep -n PATH .bashrc; ls", "read -r X <<< bin; ls",
    "printf -v X %s bin; ls", "read -r PATH <<< bin", "declare -n r=X; ls", "echo $((1+2)); ls", "echo ${PATH:-x}; ls"], 0);
});

test("#578 item 3: an absolute PATH entry fails closed after a write the guard can't place (G22)", () => {
  const cwd = process.cwd();
  // cd/pushd/popd move the directory relative writes land in.
  rows(["cd /tmp && printf x > pp/ls && PATH=/tmp/pp:$PATH ls", "pushd /tmp; printf x > pp/ls; PATH=/tmp/pp:$PATH ls", "cd /tmp; cp /usr/bin/git pp/ls; export PATH=/tmp/pp:$PATH; ls",
    "popd; printf x > pp/ls; PATH=/tmp/pp:$PATH ls", "cd /tmp; bash -c 'printf x > pp/ls; PATH=/tmp/pp:$PATH ls'"], 2);
  // Writers whose destination isn't the last word, and shells or interpreters that write themselves.
  rows(["dd if=f of=/tmp/pp/ls; PATH=/tmp/pp:$PATH ls", "install -D f /tmp/pp/ls; PATH=/tmp/pp:$PATH ls", "install -t /tmp/pp f; PATH=/tmp/pp:$PATH ls",
    "cp -t /tmp/pp ls; PATH=/tmp/pp:$PATH ls", "cp --target-directory=/tmp/pp ls; PATH=/tmp/pp:$PATH ls", "tar -x -C /tmp/pp -f a.tar; PATH=/tmp/pp:$PATH ls",
    "tar -xf a.tar; PATH=/tmp/pp:$PATH ls", "unzip -d /tmp/pp a.zip; PATH=/tmp/pp:$PATH ls", "patch -p0 < x.diff; PATH=/tmp/pp:$PATH ls", "cpio -i < a.cpio; PATH=/tmp/pp:$PATH ls",
    "node -e 'require(\"fs\").writeFileSync(\"/tmp/pp/ls\", \"x\")'; PATH=/tmp/pp:$PATH ls", "python3 w.py; PATH=/tmp/pp:$PATH ls", "perl w.pl && export PATH=/tmp/pp:$PATH; ls",
    "bash -c 'printf x > /tmp/pp/ls'; PATH=/tmp/pp:$PATH ls", "source w.sh; PATH=/tmp/pp:$PATH ls", "git clone ../r /tmp/pp; PATH=/tmp/pp:$PATH ls",
    "PATH=/tmp/pp:$PATH node w.js"], 2);
  // An absolute directory under the working directory: anything earlier could have written it.
  rows([`PATH=${cwd}/bin:$PATH ls`, `export PATH=${cwd}:$PATH; ls`, `PATH=${cwd}/node_modules/.bin:$PATH jest`], 2);
  // Nothing written, or a write the guard can place outside the PATH directory.
  rows(["cd /tmp && PATH=/usr/bin:$PATH ls", "cd src; export PATH=/opt/homebrew/bin:$PATH; brew list", "dd if=f of=/tmp/a/x; PATH=/tmp/b:$PATH ls",
    "cp a b && PATH=/usr/local/bin:$PATH ls", "cd /tmp; ls; PATH=/usr/bin:$PATH ls"], 0);
});

test("#577: [[ … ]] operands are patterns, not globs; the sweep still covers everything else (G20)", () => {
  rows(["[[ $X == g* ]]", "[[ $X == g* ]] && echo y", "[[ -f g* ]]", '[[ "$B" = gh-* ]] || echo n', "if [[ $X == git* ]]; then ls; fi"], 0);
  rows(["[ $X == g* ]", "test $X == g*", "[[ x ]] g*", "[[ g*", "echo [[ g* ]]", "[[ $X == g* ]] && ls git*", "[[ $X == g* ]] && git push --force origin main",
    "[[ -n $(git push --force origin main) ]]", "[[ $X == g* ]] && $G push origin main", "[[ $X == g* ]]; /usr/bin/g?t push origin main", "[[ $X == c*l ]] && c?rl x"], 2);
});
