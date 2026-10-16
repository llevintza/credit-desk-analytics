import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";

// Budgets and e2e job hardening (#137, #139, #155, #256 items 2-3, #257): the throwaway
// passwords stay out of build output, logs and artifacts; containers can't write the runner's
// file commands; the stack fails closed when it never comes up; k6 failures say why.
const dir = new URL("../workflows/", import.meta.url);
const WORKFLOWS = readdirSync(dir)
  .filter((f) => f.endsWith(".yml"))
  .map((f) => [f, readFileSync(new URL(f, dir), "utf8")]);
const CI = WORKFLOWS.find(([f]) => f === "ci.yml")[1];

const jobBlock = (yml, id) => {
  const m = new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z][\\w-]*:\\n|^\\S|(?![\\s\\S]))`, "m").exec(yml);
  assert.ok(m, `job ${id} not found`);
  return m[1];
};
const stepBlock = (job, name) => {
  const start = job.indexOf(`- name: ${name}`);
  assert.ok(start >= 0, `step "${name}" not found`);
  const rest = job.slice(start + 1);
  const end = rest.search(/\n {6}- /);
  return end < 0 ? job.slice(start) : job.slice(start, start + 1 + end);
};
const BUDGETS = jobBlock(CI, "budgets");
const E2E = jobBlock(CI, "e2e");

test("#257: no workflow mounts RUNNER_TEMP (or the runner's file commands) into a container", () => {
  for (const [f, yml] of WORKFLOWS) {
    assert.doesNotMatch(yml, /-v\s+"?(\$RUNNER_TEMP|\$\{RUNNER_TEMP\}|\$\{\{\s*runner\.temp\s*\}\})"?:/, f);
    assert.doesNotMatch(yml, /_runner_file_commands/, f);
  }
  // k6 gets its own results directory, and the summary reads from it.
  const k6 = stepBlock(BUDGETS, "API latency p95 (k6)");
  assert.match(k6, /mkdir -p "\$RUNNER_TEMP\/k6"/);
  assert.match(k6, /-v "\$RUNNER_TEMP\/k6":\/out /);
  assert.doesNotMatch(k6, /"\$RUNNER_TEMP\/k6\.json"/);
});

test("#257: every workspace mount into a container is read-only", () => {
  for (const [f, yml] of WORKFLOWS) {
    for (const m of yml.matchAll(/-v\s+("[^"]+"|\S+):(\S+)/g)) {
      if (/\$PWD|GITHUB_WORKSPACE|github\.workspace/.test(m[1])) assert.match(m[2], /:ro$/, `${f}: ${m[0]}`);
    }
  }
});

test("#256 item 3: every ::add-mask:: escapes % (the runner unescapes %25 in mask data)", () => {
  let n = 0;
  for (const [f, yml] of WORKFLOWS) {
    for (const m of yml.matchAll(/::add-mask::(\S*?)"/g)) {
      n++;
      assert.match(m[1], /^\$\{(\w+)\/\/%\/%25\}$/, `${f}: ${m[0]}`);
    }
  }
  assert.ok(n >= 5, `expected the 5 add-mask lines, found ${n}`);
  // The escape itself, run through bash: % becomes %25, the rest is unchanged.
  const r = spawnSync("bash", ["-c", 'v="a%25b%c"; printf %s "${v//%/%25}"'], { encoding: "utf8" });
  assert.equal(r.stdout, "a%2525b%25c");
});

test("#137: UserAdmin is built in its own step and never inside the password capture", () => {
  for (const [label, job] of [["budgets", BUDGETS], ["e2e", E2E]]) {
    const build = job.indexOf("dotnet build -c Release src/Desk.UserAdmin --nologo");
    const capture = job.indexOf("out=$(dotnet run -c Release --no-build --project src/Desk.UserAdmin -- add");
    assert.ok(build >= 0, `${label}: separate UserAdmin build step`);
    assert.ok(capture > build, `${label}: the capture runs --no-build after the build`);
    assert.doesNotMatch(job, /out=\$\(dotnet run(?![^)]*--no-build)/, `${label}: a capture without --no-build`);
    const after = job.slice(capture);
    // unset right after the sed; an explicit error (not `test -n`) before the mask.
    assert.match(after, /\| sed -n 's\/\^Password \(shown once, share it out of band\): \/\/p'\)\n\s+unset out\n\s+if \[ -z "\$\w+" \]; then echo "::error::Desk\.UserAdmin printed no password line"; exit 1; fi\n\s+echo "::add-mask::/);
    assert.doesNotMatch(job, /echo "?\$out|printf '%s\\n' "\$out" \|(?! sed)/, `${label}: $out is never echoed`);
    assert.doesNotMatch(job, /test -n "\$(DESK_PASSWORD|pw)"/, label);
  }
});

test("#137: budgets sets the .NET telemetry and logo opt-outs at job level", () => {
  assert.match(BUDGETS, /^ {4}env:\n {6}DOTNET_CLI_TELEMETRY_OPTOUT: 1\n {6}DOTNET_NOLOGO: 1\n/m);
});

test("#139: /health waits fail closed and the redundant pg_isready loops are gone", () => {
  assert.doesNotMatch(CI, /pg_isready -U desk -d creditdesk && break/);
  for (const [label, job] of [["budgets", BUDGETS], ["e2e", E2E]]) {
    const waits = [...job.matchAll(/for _ in \$\(seq 1 60\); do curl[^\n]*\/health[^\n]*done(.*)/g)];
    assert.ok(waits.length >= 1, `${label}: a /health wait`);
    for (const w of waits) assert.match(w[1], /; \[ -n "\$ok" \] \|\| \{ echo "::error::app \/health never came up"; exit 1; \}/, label);
  }
  // The loop as written: 0 when /health answers, 1 with the error when it never does.
  const wait = /(ok=; for _ in \$\(seq 1 60\)[^\n]*)/.exec(BUDGETS)[1];
  const stub = (rc) => `curl() { return ${rc}; }; sleep() { :; }; ${wait}`;
  assert.equal(spawnSync("bash", ["-c", stub(0)]).status, 0);
  const down = spawnSync("bash", ["-c", stub(7)], { encoding: "utf8" });
  assert.equal(down.status, 1);
  assert.match(down.stdout, /::error::app \/health never came up/);
});

test("#155: the e2e artifact leaves out test-results (traces) and expires within 7 days", () => {
  const upload = E2E.slice(E2E.indexOf("uses: actions/upload-artifact@"));
  const withBlock = /with:\n((?: {10}.*\n)+)/.exec(upload)[1];
  assert.doesNotMatch(withBlock, /test-results/);
  assert.match(withBlock, /e2e\/playwright-report/);
  assert.match(withBlock, /docs\/screenshots/);
  const days = Number(/retention-days: (\d+)/.exec(withBlock)?.[1]);
  assert.ok(days >= 1 && days <= 7, `retention-days ${days}`);
});

test("#155: the login setup project never records a trace (the fill carries the password)", () => {
  const cfg = readFileSync(new URL("../../e2e/playwright.config.ts", import.meta.url), "utf8");
  assert.match(cfg, /\{ name: 'setup', testMatch: \/auth\\\.setup\\\.ts\/, use: \{ trace: 'off'(, screenshot: 'off')?(, video: 'off')? \} \}/);
});

test("#278: gate-tests runs the security-headers tests, and each #67 script against the bar on its own", () => {
  const GATE = jobBlock(CI, "gate-tests");
  const headers = stepBlock(GATE, "Security headers check tests (#66, ≥80% line/branch)");
  assert.match(headers, /--test-coverage-lines=80 --test-coverage-branches=80/);
  assert.match(headers, /--test-coverage-include=perf\/security-headers\.mjs \\\n\s+perf\/security-headers\.test\.mjs/);
  const each = stepBlock(GATE, "Demo-session report and cache-memory fill tests, each script ≥80% line/branch (#67)");
  assert.match(each, /for script in perf\/demo-session-report\.mjs perf\/cache-memory\.mjs; do/);
  assert.match(each, /--test-coverage-lines=80 --test-coverage-branches=80/);
  assert.match(each, /--test-coverage-include="\$script" \\\n\s+perf\/demo-session-report\.test\.mjs/);
  assert.doesNotMatch(GATE + headers + each, /continue-on-error|\|\| true/);
});

test("#256 item 2: k6 errors are rc-specific and the compose logs follow a k6 failure", () => {
  const k6 = stepBlock(BUDGETS, "API latency p95 (k6)");
  assert.match(k6, /^ {8}id: k6$/m);
  assert.match(k6, /if \[ "\$rc" -eq 99 \]; then\n\s+echo "::error::k6 p95 budgets: a threshold was crossed or a check failed \(exit 99\)/);
  assert.match(k6, /echo "::error::k6 did not complete \(exit \$rc: config, script or setup error\)/);
  // The "(non-zero: …)" legend only when rc != 0.
  assert.doesNotMatch(k6, /echo "k6 exit code: \$rc \(non-zero/);
  assert.match(BUDGETS, /- if: failure\(\) \|\| steps\.k6\.outcome == 'failure'\n {8}run: docker compose logs/);
});

test("#256 item 2: the k6 step's rc handling, run for 0, 99 and 107 (MIN_BOOK=abc)", () => {
  const k6 = stepBlock(BUDGETS, "API latency p95 (k6)");
  const body = /\n {8}run: \|\n((?: {10}.*\n?|\s*\n)+)/.exec(k6)[1].split("\n").map((l) => l.slice(10)).join("\n");
  // Everything after the docker run: the summary, the annotations and the exit.
  const tail = body.slice(body.indexOf("|| rc=$?") + "|| rc=$?".length);
  const tmp = mkdtempSync(join(tmpdir(), "k6-rc-"));
  const run = (rc) => {
    const summary = join(tmp, `summary-${rc}`);
    const r = spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", `rc=${rc}\n${tail}`], {
      env: { PATH: process.env.PATH, RUNNER_TEMP: tmp, GITHUB_STEP_SUMMARY: summary },
      encoding: "utf8",
    });
    return { status: r.status, out: r.stdout, summary: readFileSync(summary, "utf8") };
  };
  const ok = run(0);
  assert.equal(ok.status, 0);
  assert.doesNotMatch(ok.out, /::error::/);
  assert.match(ok.summary, /k6 exit code: 0\. Non-blocking/);
  assert.doesNotMatch(ok.summary, /non-zero|did not complete|threshold/);
  const breach = run(99);
  assert.equal(breach.status, 99);
  assert.match(breach.out, /::error::k6 p95 budgets: a threshold was crossed or a check failed \(exit 99\)/);
  assert.doesNotMatch(breach.out, /did not complete/);
  const script = run(107);
  assert.equal(script.status, 107);
  assert.match(script.out, /::error::k6 did not complete \(exit 107: config, script or setup error\)/);
  assert.doesNotMatch(script.out, /threshold was crossed/);
  assert.match(script.summary, /k6 did not complete: 104 config, 107 script exception, 108 abort/);
  assert.match(script.summary, /\| k6 produced no summary \|/);
});

const REVIEW = jobBlock(WORKFLOWS.find(([f]) => f === "claude-review.yml")[1], "review");
const UPLOAD_PIN = /uses: actions\/upload-artifact@([0-9a-f]{40}) # (v[\d.]+)/;

test("#463: the claude-review job is opt-in via CLAUDE_REVIEW_ENABLED and keeps its guards", () => {
  const cond = /^ {4}if: >-\n((?: {6}.*\n)+)/m.exec(REVIEW);
  assert.ok(cond, "review job has no folded if: block");
  const terms = cond[1].split("\n").map((l) => l.trim().replace(/ &&$/, "")).filter(Boolean);
  for (const t of [
    "vars.CLAUDE_REVIEW_ENABLED == 'true'",
    "github.event.pull_request.draft == false",
    "github.event.pull_request.head.repo.full_name == github.repository",
    "github.event.pull_request.user.login != 'dependabot[bot]'",
    "(github.actor == 'cursor[bot]' || !endsWith(github.actor, '[bot]'))",
  ]) assert.ok(terms.includes(t), `review if: is missing ${t}`);
  // Every term is AND-ed: an || at the top level would let the job run with the switch off.
  assert.equal(cond[1].trim().split("&&").length, terms.length);
  assert.match(REVIEW, /#463/);
});

test("#461: the review job uploads the execution output only after the review step failed and the scan was clean", () => {
  assert.match(stepBlock(REVIEW, "Claude review"), /^ {8}id: review$/m);
  const scan = stepBlock(REVIEW, "Scan the Claude execution output for secrets");
  assert.match(scan, /^ {8}id: scan$/m);
  assert.match(scan, /^ {8}if: failure\(\) && steps\.key\.outputs\.present == 'true' && steps\.review\.outcome == 'failure'$/m);
  assert.match(scan, /EXECUTION_FILE: \$\{\{ steps\.review\.outputs\.execution_file \}\}/);
  // The base commit's gitleaks config, read-only, and the same image pin as the ci.yml secrets job.
  assert.match(scan, /-v "\$PWD\/\.review-base\/\.gitleaks\.toml":\/config\/gitleaks\.toml:ro/);
  const gitleaks = /ghcr\.io\/gitleaks\/gitleaks:\S+@sha256:[0-9a-f]{64}/;
  assert.equal(gitleaks.exec(scan)[0], gitleaks.exec(CI)[0]);
  const upload = stepBlock(REVIEW, "Upload the Claude execution output");
  assert.match(upload, /^ {8}if: failure\(\) && steps\.key\.outputs\.present == 'true' && steps\.review\.outcome == 'failure' && steps\.scan\.outputs\.clean == 'true'$/m);
  // Pinned to the same SHA and version as ci.yml's uploads.
  assert.deepEqual(UPLOAD_PIN.exec(upload).slice(1), UPLOAD_PIN.exec(CI).slice(1));
  assert.match(upload, /name: claude-review-output-\$\{\{ github\.event\.pull_request\.number \}\}-\$\{\{ github\.run_attempt \}\}/);
  assert.match(upload, /path: \$\{\{ runner\.temp \}\}\/claude-review-output\/claude-execution-output\.json$/m);
  assert.match(upload, /if-no-files-found: warn/);
  const days = Number(/retention-days: (\d+)/.exec(upload)?.[1]);
  assert.ok(days >= 1 && days <= 7, `retention-days ${days}`);
  // The job's permissions stay as they were: uploading needs none.
  assert.match(REVIEW, /permissions:\n {6}contents: read\n {6}pull-requests: write\n {4}steps:/);
});

test("#461: the scan step, run with a stub docker: clean, a job secret, a gitleaks finding, no file", () => {
  const scan = stepBlock(REVIEW, "Scan the Claude execution output for secrets");
  const body = /\n {8}run: \|\n((?: {10}.*\n?|\s*\n)+)/.exec(scan)[1].split("\n").map((l) => l.slice(10)).join("\n");
  const run = (content, dockerRc) => {
    const tmp = mkdtempSync(join(tmpdir(), "review-scan-"));
    const bin = join(tmp, "bin");
    mkdirSync(bin);
    writeFileSync(join(bin, "docker"), `#!/bin/sh\nexit ${dockerRc}\n`, { mode: 0o755 });
    if (content !== null) writeFileSync(join(tmp, "claude-execution-output.json"), content);
    const output = join(tmp, "output");
    writeFileSync(output, "");
    const r = spawnSync("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", body], {
      cwd: tmp,
      env: { PATH: `${bin}:${process.env.PATH}`, RUNNER_TEMP: tmp, GITHUB_OUTPUT: output, KEY: "sk-test-key", TOKEN: "ghs_test" },
      encoding: "utf8",
    });
    const staged = join(tmp, "claude-review-output", "claude-execution-output.json");
    return { status: r.status, out: r.stdout, output: readFileSync(output, "utf8"), staged: existsSync(staged) };
  };
  const clean = run('[{"type":"result","is_error":true}]', 0);
  assert.equal(clean.status, 0);
  assert.equal(clean.output, "clean=true\n");
  assert.ok(clean.staged);
  for (const leak of ['"sk-test-key"', '"ghs_test"']) {
    const r = run(`[${leak}]`, 0);
    assert.equal(r.status, 1);
    assert.match(r.out, /::error::The Claude execution output contains a job secret; not uploaded\./);
    assert.equal(r.output, "");
    assert.ok(!r.staged);
  }
  const flagged = run("[]", 1);
  assert.equal(flagged.status, 1);
  assert.match(flagged.out, /::error::gitleaks flagged the Claude execution output; not uploaded\./);
  assert.equal(flagged.output, "");
  assert.ok(!flagged.staged);
  const missing = run(null, 0);
  assert.equal(missing.status, 0);
  assert.match(missing.out, /::warning::No Claude execution output at /);
  assert.equal(missing.output, "");
});
