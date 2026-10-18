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
const rows = (list, want) => {
  for (const c of list) assert.equal(code(c), want, c);
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
