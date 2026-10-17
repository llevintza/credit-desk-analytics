// git push analysis against a fake repo (the real-git rows are in guard-realgit.test.mjs).
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { decide } from "../hooks/lib/decide.mjs";
import { expandOption, gitRepo, pushOptions } from "../hooks/lib/git.mjs";
import { parse } from "../hooks/lib/shell.mjs";
import { fakeRepo } from "./helpers.mjs";

const run = (command, repo = {}) => decide(JSON.stringify({ tool_name: "Bash", tool_input: { command } }), { repoAt: fakeRepo(repo) });
const code = (command, repo) => run(command, repo).code;
const words = (s) => parse(s).segments[0].words;

test("option abbreviations expand as git does (G12)", () => {
  assert.equal(expandOption("set-up"), "set-upstream");
  assert.equal(expandOption("force-w"), "force-with-lease");
  assert.equal(expandOption("no-verif"), "no-verify");
  assert.equal(expandOption("tags"), "tags");
  assert.equal(expandOption("d"), null);
  assert.equal(expandOption("frobnicate"), null);
});

test("push options: values, bundles, -- and unknown shorts", () => {
  assert.deepEqual(pushOptions(words("-o ci.skip --push-option x -oy -4v6q --signed=no -- origin -feat")).positional.map((w) => w.text), ["origin", "-feat"]);
  assert.equal(pushOptions(words("-dn origin x")).del, true);
  assert.match(pushOptions(words("-z origin")).block, /unknown git push option -z/);
  assert.match(pushOptions(words("--receive-pack=x origin")).block, /receive-pack/);
  assert.equal(pushOptions(words("--no-force --no-all --tags origin")).block, undefined);
});

test("destinations: config mapping with wildcards, push.default, tags and non-branch refs (G11)", () => {
  const star = { config: { "remote.origin.push": "refs/heads/*:refs/heads/x/*" } };
  assert.equal(code("git push origin feat", { ...star, refs: { feat: "refs/heads/feat" } }), 0);
  assert.equal(code("git push origin main", { ...star, refs: { main: "refs/heads/main" } }), 0); // mapped to x/main
  assert.equal(code("git push origin main", { config: { "remote.origin.push": ["refs/heads/a:refs/heads/b", "refs/heads/*:refs/heads/*"] }, refs: { main: "refs/heads/main" } }), 2);
  assert.equal(code("git push origin dev", { config: { "remote.origin.push": "refs/heads/dev:refs/heads/main" }, refs: { dev: "refs/heads/dev" } }), 2);
  assert.equal(code("git push origin dev", { config: { "remote.origin.push": "refs/heads/other:refs/heads/main" }, refs: { dev: "refs/heads/dev" } }), 0);
  assert.equal(code("git push origin feat", { config: { "remote.origin.push": "+refs/heads/*:refs/heads/*" } }), 2);
  assert.equal(code("git push origin", { config: { "remote.origin.push": "refs/heads/feat" } }), 2);
  assert.equal(code("git push", { config: { "push.default": "nothing" } }), 0);
  assert.equal(code("git push", { config: { "push.default": "upstream" } }), 2);
  assert.equal(code("git push", { config: { "push.default": "upstream", "branch.feat.merge": "refs/heads/feat" } }), 0);
  assert.equal(code("git push", { config: { "push.default": "weird" } }), 2);
  assert.equal(code("git push", { config: { "branch.feat.pushremote": "fork", "remote.fork.mirror": "true" } }), 2);
  assert.equal(code("git push origin v2", { refs: { v2: "refs/tags/v2" } }), 0);
  assert.equal(code("git push origin n", { refs: { n: "refs/notes/n" } }), 2);
  assert.equal(code("git push origin x:refs/pull/1/head"), 2);
  assert.equal(code("git push origin x:a..b"), 2);
  assert.equal(code("git push origin refs/x"), 2);
  assert.equal(code("git push origin HEAD~1"), 2);
  assert.equal(code("git push o* feat"), 2);
  assert.equal(code("git push origin"), 0);
  assert.equal(code("git push", { branch: null }), 2);
  assert.equal(code("git push origin HEAD", { branch: null }), 2);
});

test("state changes earlier in the payload (G2)", () => {
  assert.equal(code("git update-ref refs/heads/feat HEAD && git push origin feat"), 2);
  assert.equal(code("git update-ref refs/heads/x HEAD && git push origin HEAD:feat"), 0); // explicit dst
  assert.equal(code("git branch -m x && git push origin HEAD"), 2);
  assert.equal(code("git branch -d x && git push origin HEAD"), 0);
  assert.equal(code("git rebase main feat && git push origin HEAD"), 2);
  assert.equal(code("git rebase --onto main x && git push origin HEAD"), 0);
  assert.equal(code("git config --get user.name && git push origin feat"), 0);
  assert.equal(code("git remote add f ../f && git push origin feat"), 2);
  assert.equal(code("gh pr checkout 5 && git push origin HEAD"), 2);
  assert.equal(code("unset GIT_DIR && git push origin feat"), 2);
  assert.equal(code("source env.sh && git push origin feat"), 2);
  assert.equal(code("git x && git push origin HEAD", { config: { "alias.x": "!cd .." } }), 2);
  assert.equal(code("echo $(git checkout main) && git push origin HEAD"), 2); // a substitution counts wherever it is
});

test("aliases: chains, depth, empty and shell aliases", () => {
  assert.equal(code("git a", { config: { "alias.a": "b", "alias.b": "push origin main" } }), 2);
  assert.equal(code("git a", { config: { "alias.a": "b", "alias.b": "status" } }), 0);
  assert.equal(code("git a", { config: { "alias.a": "a" } }), 2);
  assert.equal(code("git push origin feat && echo $(git a)", { config: { "alias.a": "a" } }), 2); // a looping alias anywhere
  assert.equal(code("git a", { config: { "alias.a": "" } }), 0); // git runs nothing
  assert.equal(code("git a", { config: { "alias.a": "!git a" } }), 2); // nested too deep
  assert.equal(code("git status", { config: { "alias.status": "push origin main" } }), 0); // builtins win
  assert.equal(code("git --version"), 0);
});

test("other git and hub commands", () => {
  assert.equal(code("hub pull-request -m x"), 2);
  assert.equal(code("hub status"), 0);
  assert.equal(code("git --bare log"), 2);
  assert.equal(code("git $X log"), 2);
  assert.equal(code('git -c "$K" push origin feat'), 2); // a dynamic -c value (G3)
  assert.equal(code('git -C "$D" push origin feat'), 2);
  assert.equal(code("git -c color.ui=never log"), 0);
  assert.equal(code("cp x .git/hooks/pre-push"), 2);
  assert.equal(code("ls .git/hooks"), 0);
  assert.equal(code("cat .git/config"), 0); // G17 config files only with the signature
  assert.equal(code("cat ~/.gitconfig; git push origin feat"), 2);
});

test("lookup failures fail closed", () => {
  const broken = () => { throw new Error("no repo"); };
  const d = (command) => decide(JSON.stringify({ tool_name: "Bash", tool_input: { command } }), { repoAt: broken }).code;
  assert.equal(d("git push origin feat"), 2);
  assert.equal(d("git x && git push origin HEAD:feat"), 2);
  const lazy = () => ({ ...fakeRepo()(), branch: () => { throw new Error("HEAD"); } });
  assert.equal(decide(JSON.stringify({ tool_name: "Bash", tool_input: { command: "git push origin HEAD:feat" } }), { repoAt: lazy }).code, 2);
  assert.throws(() => gitRepo(mkdtempSync(join(tmpdir(), "agents-norepo-"))).branch(), /lookup failed/);
});
