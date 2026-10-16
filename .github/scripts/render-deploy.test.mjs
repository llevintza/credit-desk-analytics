import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { API, deployId, describe, hook, main, serviceId, status, unreadable } from "./render-deploy.mjs";

// #302: the release job logs the Render deploy id from the hook response and, when the /health smoke
// fails, reports Render's status for that deploy, or says it couldn't be read. Nothing here reaches Render:
// fetch is a stub, and the CLI run below has no key, so it stops before any request.
const SCRIPT = new URL("./render-deploy.mjs", import.meta.url).pathname;
const DEPLOY = readFileSync(new URL("../workflows/deploy.yml", import.meta.url), "utf8");
const HOOK = "https://api.render.com/deploy/srv-abc123?key=hookSecret42";
const KEY = "rnd_testKey0123";

const stepBlock = (yml, name) => {
  const start = yml.indexOf(`- name: ${name}`);
  assert.ok(start >= 0, `step "${name}" not found`);
  const rest = yml.slice(start + 1);
  const end = rest.search(/\n {6}- (name|uses):|\n {2}[a-z][\w-]*:\n|\n\S/);
  return end < 0 ? yml.slice(start) : yml.slice(start, start + 1 + end);
};

const scratch = () => {
  const dir = mkdtempSync(join(tmpdir(), "render-deploy-"));
  const out = join(dir, "output");
  const sum = join(dir, "summary");
  writeFileSync(out, "");
  writeFileSync(sum, "");
  return { dir, out, sum, read: (f) => readFileSync(f, "utf8"), done: () => rmSync(dir, { recursive: true, force: true }) };
};

const capture = () => {
  const lines = [];
  return { lines, log: (l) => lines.push(l), text: () => lines.join("\n") };
};

const reply = (status, body) => async (url, init) => {
  reply.calls.push({ url, init });
  return { ok: status >= 200 && status < 300, status, json: async () => body };
};
reply.calls = [];

test("deployId: the id from the hook's JSON response, else null", () => {
  assert.equal(deployId('{"deploy":{"id":"dep-d1a2b3c4"}}'), "dep-d1a2b3c4");
  assert.equal(deployId(' {"deploy": {"id": "dep-x9"}, "other": 1}\n'), "dep-x9");
  for (const body of ["", "not json", "null", "{}", '{"deploy":null}', '{"deploy":{"id":42}}', '{"id":"dep-top"}', '{"deploy":{"id":"srv-abc"}}', '{"deploy":{"id":"dep-a/../b"}}', '{"deploy":{"id":"dep-A1"}}'])
    assert.equal(deployId(body), null, body);
});

test("serviceId: the srv- segment of the hook path, never the key", () => {
  assert.equal(serviceId(HOOK), "srv-abc123");
  assert.equal(serviceId("https://api.render.com/deploy/srv-abc123/?key=k&ref=x"), "srv-abc123");
  for (const hook of ["", "not a url", "https://api.render.com/deploy/?key=srv-abc", "https://api.render.com/deploy/svc-abc?key=k", "https://api.render.com/deploy/srv-ab%2Fc?key=k"])
    assert.equal(serviceId(hook), null, hook);
});

test("describe: build or start failures, cancels and a slow start each read differently", () => {
  const msg = (s) => describe("dep-1", s);
  assert.equal(msg("build_failed"), "Render deploy dep-1: build_failed: the Docker build failed on Render");
  assert.equal(msg("update_failed"), "Render deploy dep-1: update_failed: the build passed but the new instance failed to start");
  assert.match(msg("pre_deploy_failed"), /pre-deploy command failed$/);
  assert.match(msg("canceled"), /canceled \(a newer deploy may have replaced it\)$/);
  assert.match(msg("deactivated"), /a newer deploy replaced this one$/);
  assert.match(msg("live"), /reports it live, but \/health did not report this commit$/);
  for (const s of ["created", "queued", "build_in_progress", "update_in_progress", "pre_deploy_in_progress"])
    assert.equal(msg(s), `Render deploy dep-1: ${s}: still in progress (slow build or cold start)`);
  assert.equal(msg("something_new"), "Render deploy dep-1: something_new: unrecognised status");
  const failed = new Set(["build_failed", "update_failed", "canceled"].map(msg));
  assert.equal(failed.size, 3);
  assert.ok(![...failed].some((m) => m.includes("still in progress")));
  assert.equal(unreadable("no API key"), "Render deploy status could not be read (no API key)");
});

test("hook: logs the id, writes deploy_id and service_id, and adds the id to the summary; no hook URL or key", () => {
  const s = scratch();
  try {
    const c = capture();
    const env = { HOOK, GITHUB_OUTPUT: s.out, GITHUB_STEP_SUMMARY: s.sum };
    assert.equal(hook({ env, body: '{"deploy":{"id":"dep-d1a2b3c4"}}', log: c.log }), 0);
    assert.deepEqual(c.lines, ["Render deploy id: dep-d1a2b3c4"]);
    assert.equal(s.read(s.out), "deploy_id=dep-d1a2b3c4\nservice_id=srv-abc123\n");
    assert.equal(s.read(s.sum), "### Render deploy\n- Deploy id: `dep-d1a2b3c4`\n");
    const all = c.text() + s.read(s.out) + s.read(s.sum);
    assert.ok(!all.includes("hookSecret42") && !all.includes("api.render.com"), "no key and no hostname");
  } finally {
    s.done();
  }
});

test("hook: a response without an id warns, writes empty outputs, and still exits 0", () => {
  const s = scratch();
  try {
    const c = capture();
    assert.equal(hook({ env: { GITHUB_OUTPUT: s.out, GITHUB_STEP_SUMMARY: s.sum }, body: "", log: c.log }), 0);
    assert.match(c.text(), /^::warning::The Render deploy hook returned no deploy id/);
    assert.equal(s.read(s.out), "deploy_id=\nservice_id=\n");
    assert.equal(s.read(s.sum), "### Render deploy\n- Deploy id: none returned by the hook\n");
    // Without GITHUB_OUTPUT or a summary (a local run) it only logs.
    const c2 = capture();
    assert.equal(hook({ env: {}, body: '{"deploy":{"id":"dep-z"}}', log: c2.log }), 0);
    assert.deepEqual(c2.lines, ["Render deploy id: dep-z"]);
  } finally {
    s.done();
  }
});

const ENV = { DEPLOY_ID: "dep-d1a2b3c4", SERVICE_ID: "srv-abc123", RENDER_API_KEY: KEY };
const noFetch = async () => assert.fail("must not call Render");

test("status: without a key it says so, makes no request and exits 0", async () => {
  const s = scratch();
  try {
    for (const key of [undefined, ""]) {
      const c = capture();
      const env = { ...ENV, RENDER_API_KEY: key, GITHUB_STEP_SUMMARY: s.sum };
      assert.equal(await status({ env, fetch: noFetch, log: c.log }), 0);
      assert.deepEqual(c.lines, ["::warning::Render deploy status could not be read (no API key)"]);
    }
    assert.equal(s.read(s.sum), "- Render deploy status could not be read (no API key)\n".repeat(2));
  } finally {
    s.done();
  }
});

test("status: a missing or malformed deploy or service id is unreadable, with no request", async () => {
  const cases = [
    [{ DEPLOY_ID: "" }, "no deploy id from the hook"],
    [{ DEPLOY_ID: undefined }, "no deploy id from the hook"],
    [{ DEPLOY_ID: "dep-x/../../owners" }, "no deploy id from the hook"],
    [{ SERVICE_ID: "" }, "no service id in the hook URL"],
    [{ SERVICE_ID: undefined }, "no service id in the hook URL"],
    [{ SERVICE_ID: "srv-a?x=1" }, "no service id in the hook URL"],
  ];
  for (const [over, why] of cases) {
    const c = capture();
    assert.equal(await status({ env: { ...ENV, ...over }, fetch: noFetch, log: c.log }), 0);
    assert.deepEqual(c.lines, [`::warning::${unreadable(why)}`]);
  }
});

test("status: GETs the deploy with the key as a bearer token and fails with Render's status; the key is never printed", async () => {
  const s = scratch();
  try {
    for (const st of ["build_failed", "update_failed", "canceled", "build_in_progress", "live"]) {
      reply.calls = [];
      const c = capture();
      const env = { ...ENV, GITHUB_STEP_SUMMARY: s.sum };
      assert.equal(await status({ env, fetch: reply(200, { id: ENV.DEPLOY_ID, status: st }), log: c.log }), 0);
      assert.deepEqual(c.lines, [`::error::${describe(ENV.DEPLOY_ID, st)}`]);
      assert.equal(reply.calls.length, 1);
      assert.equal(reply.calls[0].url, `${API}/services/srv-abc123/deploys/dep-d1a2b3c4`);
      assert.equal(reply.calls[0].init.headers.authorization, `Bearer ${KEY}`);
      assert.ok(reply.calls[0].init.signal instanceof AbortSignal, "the request has a timeout");
    }
    const sum = s.read(s.sum);
    assert.match(sum, /^- Render deploy dep-d1a2b3c4: build_failed: the Docker build failed on Render$/m);
    assert.ok(!sum.includes(KEY) && !sum.includes("api.render.com"));
  } finally {
    s.done();
  }
});

test("status: an HTTP error, a failed request or a body without a status is unreadable, never a throw", async () => {
  const run = async (fetch) => {
    const c = capture();
    assert.equal(await status({ env: ENV, fetch, log: c.log }), 0);
    assert.equal(c.lines.length, 1);
    assert.ok(!c.text().includes(KEY) && !c.text().includes("api.render.com"), c.text());
    return c.lines[0];
  };
  assert.equal(await run(reply(401, {})), "::warning::Render deploy status could not be read (HTTP 401)");
  assert.equal(await run(reply(404, {})), "::warning::Render deploy status could not be read (HTTP 404)");
  const boom = async (url) => {
    throw new Error(`fetch failed for ${url} with ${KEY}`);
  };
  assert.equal(await run(boom), "::warning::Render deploy status could not be read (request failed)");
  const badJson = async () => ({ ok: true, status: 200, json: async () => JSON.parse("<html>") });
  assert.equal(await run(badJson), "::warning::Render deploy status could not be read (request failed)");
  for (const body of [null, {}, { status: 7 }, { status: "::error::spoof" }])
    assert.equal(await run(reply(200, body)), "::warning::Render deploy status could not be read (no status in the response)");
});

test("main: dispatches hook and status, and rejects anything else with exit 2", async () => {
  const c = capture();
  assert.equal(await main(["status"], { env: { ...ENV, RENDER_API_KEY: "" }, fetch: noFetch, log: c.log }), 0);
  assert.equal(await main(["hook"], { env: {}, body: "", log: c.log }), 0);
  assert.equal(await main([], { log: c.log }), 2);
  assert.equal(await main(["deploy"], {}), 2);
  assert.match(c.text(), /could not be read \(no API key\)[\s\S]*no deploy id[\s\S]*usage: render-deploy\.mjs hook\|status/);
});

test("CLI: hook reads stdin and status without a key exits 0 with the message", () => {
  const s = scratch();
  try {
    const env = { PATH: process.env.PATH, HOOK, GITHUB_OUTPUT: s.out };
    const out = execFileSync(process.execPath, [SCRIPT, "hook"], { env, input: '{"deploy":{"id":"dep-cli1"}}', encoding: "utf8" });
    assert.equal(out, "Render deploy id: dep-cli1\n");
    assert.equal(s.read(s.out), "deploy_id=dep-cli1\nservice_id=srv-abc123\n");
    const st = execFileSync(process.execPath, [SCRIPT, "status"], { env: { PATH: process.env.PATH, DEPLOY_ID: "dep-cli1", SERVICE_ID: "srv-abc123" }, encoding: "utf8" });
    assert.equal(st, "::warning::Render deploy status could not be read (no API key)\n");
  } finally {
    s.done();
  }
});

test("deploy.yml: the hook response is kept and parsed, and node is set up from a pinned action on the tip only", () => {
  const trigger = stepBlock(DEPLOY, "Trigger Render deploy of this commit");
  assert.match(trigger, /^ {8}id: hook$/m);
  assert.match(trigger, /^ {8}if: steps\.tip\.outputs\.allowed == 'true'$/m);
  assert.doesNotMatch(trigger, /\/dev\/null/, "the hook response is not discarded");
  assert.match(trigger, /> hook-response\.json\n/);
  assert.match(trigger, /node \.github\/scripts\/render-deploy\.mjs hook < hook-response\.json/);
  const node = /\n( {6}- uses: actions\/setup-node@[0-9a-f]{40} # v[\d.]+\n {8}if: steps\.tip\.outputs\.allowed == 'true'\n)/.exec(DEPLOY);
  assert.ok(node, "setup-node is SHA-pinned and gated on the tip");
  assert.ok(DEPLOY.indexOf(node[1]) < DEPLOY.indexOf("- name: Trigger Render deploy"), "node is set up before the hook step");
});

test("deploy.yml: a failed smoke reads the status with the production key, then fails the same way", () => {
  const smoke = stepBlock(DEPLOY, "Smoke test (wait for the new build to serve /health)");
  assert.match(smoke, /^ {10}DEPLOY_ID: \$\{\{ steps\.hook\.outputs\.deploy_id \}\}$/m);
  assert.match(smoke, /^ {10}SERVICE_ID: \$\{\{ steps\.hook\.outputs\.service_id \}\}$/m);
  assert.match(smoke, /^ {10}RENDER_API_KEY: \$\{\{ secrets\.RENDER_API_KEY \}\}$/m);
  assert.doesNotMatch(smoke, /RENDER_DEPLOY_HOOK_URL/, "the smoke never sees the hook URL");
  assert.match(
    smoke,
    /if \[ "\$v" != "\$SHA" \]; then\n\s+echo "::error::\$APP_URL did not report \$SHA within 15 minutes"\n\s+node \.github\/scripts\/render-deploy\.mjs status \|\| true\n\s+exit 1\n\s+fi/,
  );
  // The key is read only by this step, in the production-environment release job, and nowhere else.
  assert.equal(DEPLOY.match(/secrets\.RENDER_API_KEY/g).length, 1);
  const release = DEPLOY.slice(DEPLOY.indexOf("\n  release:"));
  assert.match(release, /^ {4}environment: production$/m);
  assert.ok(release.includes("secrets.RENDER_API_KEY"));
});
