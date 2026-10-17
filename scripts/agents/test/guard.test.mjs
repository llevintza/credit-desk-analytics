import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Readable } from "node:stream";
import { fileURLToPath } from "node:url";
import { decide, gitSubcommand, head, isSecretPath } from "../hooks/lib/decide.mjs";
import { parse } from "../hooks/lib/shell.mjs";
import { normalize, unescapeRaw } from "../hooks/lib/input.mjs";
import { BLOCK_TTL_MS, callKey, recall, remember } from "../hooks/lib/record.mjs";
import { HARD_CAP_MS, loadPatterns, main } from "../hooks/guard.mjs";
import { capture, scratch } from "./helpers.mjs";

const GUARD = fileURLToPath(new URL("../hooks/guard.mjs", import.meta.url));

// The three shell shapes of the exit-code table, plus Grok's camelCase shape.
const SHAPES = {
  claude: (command) => ({ tool_name: "Bash", tool_input: { command }, hook_event_name: "PreToolUse" }),
  "cursor-imported": (command) => ({ tool_name: "Shell", tool_input: { command } }),
  "cursor-native": (command) => ({ hook_event_name: "beforeShellExecution", command, cwd: "/w" }),
  grok: (command) => ({ toolName: "run_terminal_command", toolInput: { command } }),
};
const code = (json, opts) => decide(typeof json === "string" ? json : JSON.stringify(json), opts).code;
const rows = (list, want) => {
  for (const c of list) for (const [name, shape] of Object.entries(SHAPES)) assert.equal(code(shape(c)), want, `${name}: ${c}`);
};

test("secret reads by command block in every shape", () => {
  rows([
    "cat .env.dev", "head web/.env.qa", "sed -n 1p .env.prod", "base64 secrets/x", "wc -l < .env", `python -c "print(open('.env').read())"`,
    "tail -n1 a/secrets/db.json", "xxd .ENV", "cp .env /tmp/x", "source .env", ". .env.local", "awk 1 .env", "cut -c1 .env", "less .env",
    "echo $(cat .env)", "printenv; cat .env",
  ], 2);
  rows(["cat .env.example", "ls .env", "git add .env.example", "cat .envrc", "cat docs/secrets.md", "node --test", "git status"], 0);
});

test("Read and Write tools: secret paths, secret patterns and Password= literals", () => {
  const read = (path, tool = "Read") => ({ tool_name: tool, tool_input: { file_path: path } });
  assert.equal(code(read(".env")), 2);
  assert.equal(code(read("web/.env.prod")), 2);
  assert.equal(code(read("secrets/key.pem")), 2);
  assert.equal(code(read("web/.env.example")), 0);
  assert.equal(code({ tool_name: "Edit", tool_input: { file_path: ".env.example", old_string: "A=", new_string: "A=\nB=" } }), 0);
  const patterns = () => [{ id: "postgres-uri-password", re: /postgres:\/\/[^:\s/@]+:([^@\s$<>{}]+)@/ }];
  const w = (tool_input, tool = "Write") => code({ tool_name: tool, tool_input }, { patterns });
  assert.equal(w({ file_path: "a.cs", content: 'var c = "Host=x;Password=hunter2";' }), 2);
  assert.equal(w({ file_path: "a.md", content: "pwd=abc" }), 2);
  assert.equal(w({ file_path: "a.md", content: "postgres://u:s3cret@h/db" }), 2);
  assert.equal(w({ file_path: "a.md", old_string: "x", new_string: "Password=abc" }, "Edit"), 2);
  assert.equal(w({ file_path: "a.md", edits: [{ old_string: "a", new_string: "b" }, { old_string: "c", new_string: "PASSWORD=zz" }] }, "MultiEdit"), 2);
  assert.equal(w({ path: "a.md", contents: "Password=abc" }), 2); // Cursor field names
  for (const content of ["No Password=… literals", "Password=<password>", "Password=$PGPASSWORD", "Password={0}", "Password=", 'Password="', "postgres://u:${P}@h", "plain text"])
    assert.equal(w({ file_path: "a.md", content }), 0, content);
  assert.equal(w({ file_path: "AGENTS.md", content: "use the web fetch tool, never curl" }), 0); // G18 is for commands only
});

test("curl and wget block in every form, with no exception (G18)", () => {
  rows([
    "curl -s http://localhost:5180/health", "/usr/bin/curl x", "env curl x", "command curl x", "timeout 5 curl x", "busybox wget x", "c\\url x",
    `"cu""rl" x`, "cu$'r'l x", "$'\\x63url' x", "CURL x", "xargs curl < u", "sh -c 'wget -q x'", "CURL_HOME=. curl x", "rg -n curl docs/",
    "git commit -m 'drop wget'", "echo $(wget x)",
  ], 2);
  rows(["npm i node-libcurl", "cat ~/.curlrc", "ls"], 0);
});

test("push/merge-shaped segments fail closed until an analyser proves them; mentions pass", () => {
  rows(["git push origin main", "git -C . push origin feat", "hub push origin x", "env git push", "gh pr merge 5", "gh api repos/o/r/pulls/5/merge -X PUT",
    "echo 'git push' | sh", "printf 'git push' |& bash", "echo $(git push origin main)", "x=`git push`", "git -c alias.c='!git push' commit", "cat <(git push)",
    "if git push; then :; fi", "{ cd ..; git push; }", "for b in a; do git push origin $b; done", "! git push", "X=1 git push"], 2);
  rows([
    "echo 'git push origin main'", "grep -rn 'git push' docs", "git log --grep push", `git commit -m "docs: the git push flow"`, "ls # git push origin main",
    `git commit -m "$(cat <<'EOF'\nfix: it's how we git push\nEOF\n)"`, "cat <<'EOF' > notes.md\ngit push origin main\nEOF", "git --no-pager -C . log", "npm test",
  ], 0);
});

test("tokenizer errors fail closed only when the signature or a secret path is there (G7, G8)", () => {
  rows(["cat <<EOF\ngit push origin main", "echo 'unbalanced && git push origin main", "cat \"unbalanced .env", 'gi"t push origin main'], 2);
  rows(["echo 'unbalanced", "cat <<EOF\nno end"], 0);
});

test("unrecognized input: open without the signature, closed with it (H4, R4-M1(c), G6)", () => {
  for (const raw of ['{"cmd":"git push origin main"}', "gh pr merge 3", '{"cmd":"git \\u0070ush origin main"}', "git \\u0070ush origin main", '["gh pr merge 1"]', '{"a":{"b":["x","api.github.com/x"]}}'])
    assert.equal(code(raw), 2, raw);
  for (const raw of ["hello", '{"x":1}', "null", '{"command":"ls"}', '{"tool_name":"Bash","tool_input":"ls"}'])
    assert.deepEqual([decide(raw).code, decide(raw).unrecognized], [0, true], raw);
  assert.equal(unescapeRaw("\\u0067\\x69\\u{74}"), "git");
});

test("recognized shapes: malformed calls block, unknown tools pass", () => {
  assert.equal(code({ tool_name: "Bash", tool_input: { command: ["ls"] } }), 2);
  assert.equal(code({ hook_event_name: "beforeShellExecution", command: 1 }), 2);
  assert.equal(code({ toolName: "run_terminal_command", toolInput: {} }), 2);
  assert.equal(code({ tool_name: "Write", tool_input: { content: "x" } }), 2);
  for (const t of ["Grep", "WebFetch", "Glob"]) assert.equal(code({ tool_name: t, tool_input: { pattern: "x" } }), 0, t);
  assert.equal(code({ tool_name: "WebFetch", tool_input: { url: "https://api.github.com/repos/o/r" } }), 0);
});

test("MCP calls are normalized per adapter; a signature in the arguments fails closed", () => {
  assert.equal(normalize({ tool_name: "mcp__github__get_issue", tool_input: {} }).tool, "mcp__github__get_issue");
  assert.equal(normalize({ tool_name: "MCP:merge_pull_request", mcp_server_name: "gh", tool_input: {} }).tool, "mcp__gh__merge_pull_request");
  assert.equal(normalize({ tool_name: "MCP:x", tool_input: {} }).tool, "mcp__unknown__x");
  assert.equal(normalize({ toolName: "github__merge_pull_request", toolInput: {} }).tool, "mcp__github__merge_pull_request");
  assert.equal(normalize({ toolName: "mcp__a__b", toolInput: {} }).tool, "mcp__a__b");
  assert.equal(code({ tool_name: "mcp__github__get_issue", tool_input: { owner: "o", repo: "r", issue_number: 1 } }), 0);
  assert.equal(code({ toolName: "github__update_ref", toolInput: { ref: "git/refs/heads/main" } }), 2);
  assert.equal(code({ tool_name: "mcp__gh__update_repo", tool_input: { default_branch: "x" } }), 2);
});

test("helpers: git subcommand, secret paths", () => {
  const w = (s) => s.split(" ").map((text) => ({ text }));
  assert.deepEqual(gitSubcommand(w("git -C x --git-dir y -c a=b --no-pager log")), { sub: "log", config: true });
  assert.deepEqual(gitSubcommand(w("git --config-env=a=B")), { sub: undefined, config: true });
  assert.equal(isSecretPath("--env-file=web/.env.local"), true);
  assert.equal(isSecretPath("secrets"), false);
  assert.equal(head(parse("{ ! X=1 git push").segments[0]).text, "git");
  assert.equal(head(parse("'if' x").segments[0]).text, "if");
  assert.equal(head(parse("X=1").segments[0]), null);
});

const input = (v) => Readable.from([typeof v === "string" ? v : JSON.stringify(v)]);
async function run(v, opts = {}) {
  const dir = scratch();
  const out = capture();
  const err = capture();
  const rc = await main({ stdin: input(v), stdout: out, stderr: err, env: {}, recordFile: join(dir, "rec.json"), logFile: join(dir, "u.log"), patternsFile: join(dir, "none.txt"), ...opts });
  return { rc, out: out.text(), err: err.text(), dir };
}

test("CLI: exit codes, stderr reasons and the Cursor-native deny reply", async () => {
  const ok = await run(SHAPES.claude("ls"));
  assert.deepEqual([ok.rc, ok.out, ok.err], [0, "", ""]);
  const c = await run(SHAPES.claude("cat .env"));
  assert.equal(c.rc, 2);
  assert.match(c.err, /^agents guard: blocked: reading \.env/);
  assert.equal(c.out, "");
  const n = await run(SHAPES["cursor-native"]("git push origin main"));
  assert.equal(n.rc, 2);
  assert.deepEqual(Object.keys(JSON.parse(n.out)), ["permission", "user_message"]);
  assert.equal(JSON.parse(n.out).permission, "deny");
});

test("CLI: unrecognized input warns once and logs a redacted line", async () => {
  const r = await run("not json, password=hunter2");
  assert.equal(r.rc, 0);
  assert.equal(r.err, "agents guard: unrecognized hook input; not enforcing\n");
  const log = readFileSync(join(r.dir, "u.log"), "utf8");
  assert.match(log, /^\S+ bytes=26 sha256=[0-9a-f]{16}\n$/);
  assert.doesNotMatch(log, /hunter2/);
  assert.equal((await run("hello", { logFile: join(r.dir, "missing", "u.log") })).rc, 0);
});

test("CLI: any exception exits 2 (G5)", async () => {
  const r = await run(SHAPES.claude("ls"), { decideImpl: () => { throw new Error("boom"); } });
  assert.equal(r.rc, 2);
  assert.match(r.err, /guard error, failing closed \(G5\): boom/);
  assert.equal((await run(SHAPES.claude("ls"), { decideImpl: () => { throw "raw"; } })).rc, 2);
  const dir = scratch();
  writeFileSync(join(dir, "p.txt"), "bad-line-without-regex\n");
  assert.equal((await run({ tool_name: "Write", tool_input: { file_path: "a", content: "x" } }, { patternsFile: join(dir, "p.txt") })).rc, 2);
  assert.equal((await run({ tool_name: "Write", tool_input: { file_path: "a", content: "x" } }, { patternsFile: dir })).rc, 2); // EISDIR
});

test("CLI: the secret-patterns file is read for writes ((?i) becomes the i flag)", async () => {
  const dir = scratch();
  const file = join(dir, "p.txt");
  writeFileSync(file, "# GENERATED from .gitleaks.toml\n\npg-pass (?i)POSTGRES_PASSWORD\\s*[:=]\\s*[\"']?([A-Za-z0-9_]{3,})\nnamed (?P<k>tok_[a-z]{4})\n");
  assert.deepEqual(loadPatterns(file).map((p) => [p.id, p.re.flags]), [["pg-pass", "i"], ["named", ""]]);
  const write = (content) => run({ tool_name: "Write", tool_input: { file_path: "c.yml", content } }, { patternsFile: file });
  assert.equal((await write("postgres_password: abcdef")).rc, 2);
  assert.match((await write("x tok_abcd")).err, /secret pattern named/);
  assert.equal((await write("POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}")).rc, 0);
  assert.equal((await run(SHAPES.claude("ls"), { patternsFile: join(dir, "p.txt", "x") })).rc, 0); // commands never load patterns
});

test("CLI: under CI the canary is written, then evaluation goes on", async () => {
  const dir = scratch();
  const r = await run(SHAPES.claude("cat .env"), { env: { CI: "true", RUNNER_TEMP: dir } });
  assert.equal(r.rc, 2);
  assert.equal(readFileSync(join(dir, "agents-hooks-ran"), "utf8"), "guard\n");
  assert.equal((await run(SHAPES.claude("ls"), { env: { GITHUB_ACTIONS: "true", RUNNER_TEMP: join(dir, "missing") } })).rc, 0);
});

test("one tool call delivered twice is evaluated once; a block is never replayed as an allow (C7)", async () => {
  const dir = scratch();
  const recordFile = join(dir, "rec.json");
  const first = await run({ ...SHAPES.claude("git push origin main"), cwd: "/w" }, { recordFile });
  const second = await run(SHAPES["cursor-native"]("git push origin main"), { recordFile });
  assert.deepEqual([first.rc, second.rc], [2, 2]);
  assert.doesNotMatch(first.err, /recorded/);
  assert.match(second.err, /\(recorded for this call\)/);
  assert.equal((await run(SHAPES["cursor-native"]("ls"), { recordFile })).rc, 0);
  const key = callKey(normalize(SHAPES.claude("git push origin main")), "/w");
  assert.equal(key, callKey(normalize(SHAPES["cursor-native"]("git push origin main")), "/x")); // cwd "/w" comes from the payload
  assert.equal(recall(key, Date.now() + BLOCK_TTL_MS + 1, recordFile), undefined);
  remember(key, { code: 0 }, Date.now(), recordFile);
  writeFileSync(recordFile, JSON.stringify({ [key]: { code: 0, at: Date.now() } }));
  assert.equal(recall(key, Date.now(), recordFile), undefined); // a planted allow is ignored
  writeFileSync(recordFile, JSON.stringify({ [key]: { code: 2, at: Date.now() } }));
  assert.equal(recall(key, Date.now(), recordFile), undefined); // no reason: not a record
  writeFileSync(recordFile, JSON.stringify({ [key]: { code: 2, reason: "r", at: Date.now() + 60000 } }));
  assert.equal(recall(key, Date.now(), recordFile), undefined); // from the future
  writeFileSync(recordFile, "[]");
  assert.equal(recall(key, Date.now(), recordFile), undefined);
  remember(key, { code: 2, reason: "r" }, Date.now(), join(dir, "missing", "rec.json")); // unwritable: no throw
  writeFileSync(recordFile, JSON.stringify({ old: { code: 2, reason: "r", at: 0 }, bad: null }));
  remember(key, { code: 2, reason: "r" }, Date.now(), recordFile);
  assert.deepEqual(Object.keys(JSON.parse(readFileSync(recordFile, "utf8"))), [key]);
});

test("timing: ≤500 ms at p99 in-process, and the 1 s hard cap fails closed", async () => {
  const cmds = ["git status", "cat .env", "npm test && git push -u origin feat", `git commit -m "$(cat <<'EOF'\nx\nEOF\n)"`, "a | b; c && d || e"];
  const times = [];
  for (let i = 0; i < 200; i++) {
    const t = process.hrtime.bigint();
    decide(JSON.stringify(SHAPES.claude(`${cmds[i % cmds.length]} ${"x ".repeat(i)}`)));
    times.push(Number(process.hrtime.bigint() - t) / 1e6);
  }
  times.sort((a, b) => a - b);
  assert.ok(times[Math.floor(times.length * 0.99)] <= 500, `p99 ${times[198]} ms`);
  let t = 0;
  const now = () => (t += HARD_CAP_MS); // every reading is past the deadline
  const r = await run(SHAPES.claude("ls"), { now });
  assert.equal(r.rc, 2);
  assert.match(r.err, /hard cap/);
});

test("the hook process: exit codes end to end, and stdin left open hits the hard cap", async () => {
  const spawnGuard = (stdin, close = true) => new Promise((resolve) => {
    const env = { ...process.env, CI: "", GITHUB_ACTIONS: "" };
    const p = spawn(process.execPath, [GUARD], { env, stdio: ["pipe", "pipe", "pipe"] });
    let err = "";
    p.stderr.on("data", (d) => (err += d));
    const started = Date.now();
    p.on("close", (rc) => resolve({ rc, err, ms: Date.now() - started }));
    p.stdin.write(stdin);
    if (close) p.stdin.end();
  });
  assert.equal((await spawnGuard(JSON.stringify(SHAPES.claude("ls")))).rc, 0);
  assert.equal((await spawnGuard(JSON.stringify(SHAPES.claude("head .env.prod")))).rc, 2);
  const hung = await spawnGuard("{", false);
  assert.equal(hung.rc, 2);
  assert.match(hung.err, /hard cap/);
  assert.ok(hung.ms < 5000);
  assert.equal(existsSync(GUARD), true);
});
