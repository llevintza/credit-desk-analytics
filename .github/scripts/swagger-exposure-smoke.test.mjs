import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { execFile } from "node:child_process";
import { createServer } from "node:http";

// #110 (Helms ruling 3): after the deploy reports the new sha, the release job checks anonymously that
// /swagger and /openapi/v1.json are not served. #105 is on main (admin-only Swagger), so a 2xx fails
// the deploy. This runs the step's own bash against a local server; nothing here reaches production.
const DEPLOY = readFileSync(new URL("../workflows/deploy.yml", import.meta.url), "utf8");
const STEP_NAME = "Smoke test (Swagger UI and the OpenAPI document are not public)";

const stepBlock = (yml, name) => {
  const start = yml.indexOf(`- name: ${name}`);
  assert.ok(start >= 0, `step "${name}" not found`);
  const rest = yml.slice(start + 1);
  const end = rest.search(/\n {6}- (name|uses):|\n {2}[a-z][\w-]*:\n|\n\S/);
  return end < 0 ? yml.slice(start) : yml.slice(start, start + 1 + end);
};
const runBody = (step) => {
  const m = /\n( +)run: \|\n((?:\1 {2}.*\n?|\s*\n)+)/.exec(step);
  assert.ok(m, "step has a run: | block");
  const indent = m[1].length + 2;
  return m[2].split("\n").map((l) => l.slice(indent)).join("\n");
};
const STEP = stepBlock(DEPLOY, STEP_NAME);
const BODY = runBody(STEP);

// A stand-in for the app: each path answers with the status (and Location) it is given; anything else is 404.
const serve = async (routes) => {
  const seen = [];
  const server = createServer((req, res) => {
    seen.push({ path: req.url, auth: req.headers.authorization ?? null, cookie: req.headers.cookie ?? null });
    const r = routes[req.url] ?? { status: 404 };
    res.writeHead(r.status, r.location ? { Location: r.location } : {});
    res.end(r.status === 200 ? "<html>swagger-ui</html>" : "");
  });
  await new Promise((ok) => server.listen(0, "127.0.0.1", ok));
  return { url: `http://127.0.0.1:${server.address().port}`, seen, close: () => new Promise((ok) => server.close(ok)) };
};

// Same shell as the workflow's `defaults.run.shell: bash` (bash --noprofile --norc -eo pipefail).
const runStep = (appUrl) =>
  new Promise((resolve) => {
    execFile("bash", ["--noprofile", "--norc", "-eo", "pipefail", "-c", BODY], { env: { PATH: process.env.PATH, APP_URL: appUrl } }, (err, stdout, stderr) =>
      resolve({ status: err ? err.code : 0, stdout, stderr }),
    );
  });

const withApp = async (routes, fn) => {
  const app = await serve(routes);
  try {
    return await fn(app);
  } finally {
    await app.close();
  }
};

test("deploy.yml: the exposure smoke is gated on the tip, follows the /health smoke, and reads APP_URL only through env", () => {
  assert.match(STEP, /^ {8}if: steps\.tip\.outputs\.allowed == 'true'$/m);
  assert.match(STEP, /^ {10}APP_URL: \$\{\{ vars\.APP_URL \}\}$/m);
  assert.doesNotMatch(BODY, /\$\{\{/, "no expression inside the script (script injection)");
  assert.doesNotMatch(STEP, /secrets\./, "the probe is anonymous and needs no secret");
  const order = ["Smoke test (wait for the new build to serve /health)", STEP_NAME, "Summary"].map((n) => DEPLOY.indexOf(`- name: ${n}\n`));
  assert.ok(order.every((i) => i > 0) && order[0] < order[1] && order[1] < order[2], "runs after the /health smoke and before the summary");
  // Redirects are not followed: with Swagger on, /swagger is a 301 to the public UI shell (ADR-0024).
  assert.doesNotMatch(BODY, /\s(-L|--location)\b/);
  for (const path of ["/swagger", "/openapi/v1.json"]) assert.ok(BODY.includes(path), `probes ${path}`);
});

test("Swagger off (the production default): 404 on both passes silently", async () => {
  await withApp({}, async (app) => {
    const r = await runStep(app.url);
    assert.equal(r.status, 0, r.stdout + r.stderr);
    assert.match(r.stdout, /^GET \/swagger -> 404$/m);
    assert.match(r.stdout, /^GET \/openapi\/v1\.json -> 404$/m);
    assert.doesNotMatch(r.stdout, /::(error|warning)::/);
    // Anonymous: no credentials of any kind go out.
    assert.deepEqual(app.seen.map((s) => s.path), ["/swagger", "/openapi/v1.json"]);
    assert.ok(app.seen.every((s) => s.auth === null && s.cookie === null));
  });
});

test("Swagger on behind #105: /swagger 301 to the shell and the document 401/403 pass silently", async () => {
  for (const docStatus of [401, 403]) {
    const routes = {
      "/swagger": { status: 301, location: "/swagger/index.html" },
      "/swagger/index.html": { status: 200 },
      "/openapi/v1.json": { status: docStatus },
    };
    await withApp(routes, async (app) => {
      const r = await runStep(app.url);
      assert.equal(r.status, 0, r.stdout + r.stderr);
      assert.match(r.stdout, /^GET \/swagger -> 301$/m);
      assert.match(r.stdout, new RegExp(`^GET /openapi/v1\\.json -> ${docStatus}$`, "m"));
      assert.doesNotMatch(r.stdout, /::(error|warning)::/);
      assert.ok(!app.seen.some((s) => s.path === "/swagger/index.html"), "the redirect is not followed");
    });
  }
});

test("an anonymous 200 on either path fails the deploy with an ::error:: naming it", async () => {
  for (const [path, other] of [["/swagger", "/openapi/v1.json"], ["/openapi/v1.json", "/swagger"]]) {
    await withApp({ [path]: { status: 200 } }, async (app) => {
      const r = await runStep(app.url);
      assert.notEqual(r.status, 0, `${path} 200 must fail`);
      assert.match(r.stdout, new RegExp(`^::error::${path.replace(/\./g, "\\.")} answers 200 without a session`, "m"));
      assert.doesNotMatch(r.stdout, new RegExp(`::error::${other.replace(/\./g, "\\.")}`));
      // Both paths are still probed and logged, so the run shows the whole picture.
      assert.deepEqual(app.seen.map((s) => s.path), ["/swagger", "/openapi/v1.json"]);
    });
  }
  await withApp({ "/swagger": { status: 200 }, "/openapi/v1.json": { status: 200 } }, async (app) => {
    const r = await runStep(app.url);
    assert.notEqual(r.status, 0);
    assert.equal(r.stdout.match(/::error::/g).length, 2);
  });
});

test("any other 2xx also fails; 3xx, 4xx and 5xx pass", async () => {
  for (const status of [201, 204, 206]) {
    await withApp({ "/openapi/v1.json": { status } }, async (app) => {
      const r = await runStep(app.url);
      assert.notEqual(r.status, 0, `${status} must fail`);
      assert.match(r.stdout, new RegExp(`::error::/openapi/v1\\.json answers ${status}`));
    });
  }
  for (const status of [302, 400, 405, 500, 503]) {
    await withApp({ "/swagger": { status, location: "/" }, "/openapi/v1.json": { status } }, async (app) => {
      const r = await runStep(app.url);
      assert.equal(r.status, 0, `${status} must pass: ${r.stdout}`);
      assert.doesNotMatch(r.stdout, /::error::/);
    });
  }
});

test("no answer fails closed: exposure could not be ruled out", async () => {
  // A port that was just free: connection refused, so the probe logs 000.
  const app = await serve({});
  const url = app.url;
  await app.close();
  const r = await runStep(url);
  assert.notEqual(r.status, 0);
  assert.match(r.stdout, /^GET \/swagger -> 000$/m);
  assert.match(r.stdout, /^::error::\/swagger did not answer; could not rule out exposure$/m);
  assert.match(r.stdout, /^::error::\/openapi\/v1\.json did not answer/m);
});
