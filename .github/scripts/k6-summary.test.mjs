import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";

// #271: perf/positions.js writes its own summary with handleSummary() (k6 v2 discourages
// --summary-export), and the budgets job's jq reads that shape. The fixture is the `metrics` part of a
// real handleSummary `data` object from a local k6 run of perf/positions.js (scale 1.0, trimmed to the
// metrics the step reads; the setup_data and state parts are left out on purpose).
const root = new URL("../../", import.meta.url);
const read = (p) => readFileSync(new URL(p, root), "utf8");
const POSITIONS = read("perf/positions.js");
const CI = read(".github/workflows/ci.yml");
const METRICS = JSON.parse(read(".github/scripts/fixtures/k6-handle-summary-metrics.json"));

/** handleSummary from the script itself, run outside k6 with the given __ENV. */
function handleSummary(env) {
  const m = /^export function handleSummary\(data\) \{\n[\s\S]*?\n\}\n/m.exec(POSITIONS);
  assert.ok(m, "perf/positions.js exports handleSummary(data)");
  // eslint-disable-next-line no-new-func
  return new Function("__ENV", `${m[0].replace(/^export /, "")}\nreturn handleSummary;`)(env);
}

// The k6 step's script, dedented.
const k6Step = () => {
  const start = CI.indexOf("- name: API latency p95 (k6)");
  const rest = CI.slice(start + 1);
  const step = CI.slice(start, start + 1 + rest.search(/\n {6}- /));
  return /\n {8}run: \|\n((?: {10}.*\n?|\s*\n)+)/.exec(step)[1].split("\n").map((l) => l.slice(10)).join("\n");
};
// Each jq program the step runs over the summary file, in order.
const jqPrograms = (script) => [...script.matchAll(/jq -r '((?:[^']|'\\'')*)'/g)].map((m) => m[1]);

const DATA = { metrics: METRICS, setup_data: { authorization: "Bearer secret-token", run: 1, book: 20000 }, state: { testRunDurationMs: 92000 } };

test("handleSummary writes only the metrics to SUMMARY_JSON (never setup_data) and a text summary", () => {
  const out = handleSummary({ SUMMARY_JSON: "/out/k6.json" })(DATA);
  assert.deepEqual(Object.keys(out).sort(), ["/out/k6.json", "stdout"]);
  const file = JSON.parse(out["/out/k6.json"]);
  assert.deepEqual(Object.keys(file), ["metrics"]);
  assert.doesNotMatch(out["/out/k6.json"], /secret-token|setup_data|authorization/);
  assert.match(out.stdout, /MISS, Risk preset, whole book: p50 [\d.]+ ms, p95 [\d.]+ ms, max [\d.]+ ms/);
  assert.match(out.stdout, /HIT, Risk preset: p50 [\d.]+ ms/);
  assert.match(out.stdout, /MISS, All preset, whole book: p50 [\d.]+ ms/);
  assert.match(out.stdout, /checks: \d+ passed, \d+ failed/);
  assert.doesNotMatch(out.stdout, /secret-token/);
});

test("handleSummary without SUMMARY_JSON writes the text summary only; missing metrics print n/a", () => {
  const out = handleSummary({})({ metrics: {} });
  assert.deepEqual(Object.keys(out), ["stdout"]);
  assert.match(out.stdout, /MISS, Risk preset, whole book: p50 n\/a, p95 n\/a, max n\/a/);
  assert.match(out.stdout, /checks: 0 passed, 0 failed/);
});

test("handleSummary lists each crossed threshold, or says all passed", () => {
  // The fixture run passed every threshold; CI's All MISS usually crosses p(95)<150 (#241).
  assert.match(handleSummary({})(DATA).stdout, /thresholds: all passed/);
  const metrics = structuredClone(METRICS);
  metrics.all_miss.thresholds["p(95)<150"].ok = false;
  metrics.checks.thresholds["rate==1.0"].ok = false;
  const out = handleSummary({})({ ...DATA, metrics }).stdout;
  assert.match(out, /threshold crossed: all_miss p\(95\)<150/);
  assert.match(out, /threshold crossed: checks rate==1\.0/);
  assert.doesNotMatch(out, /all passed/);
});

test("ci.yml: the k6 step uses handleSummary's file, not --summary-export", () => {
  const script = k6Step();
  assert.doesNotMatch(CI, /--summary-export/);
  assert.match(script, /-e SUMMARY_JSON=\/out\/k6\.json/);
  assert.match(script, /run --quiet - < perf\/positions\.js/);
});

test("ci.yml: the step summary's jq reads the handleSummary shape (fixture)", () => {
  const file = handleSummary({ SUMMARY_JSON: "/out/k6.json" })(DATA)["/out/k6.json"];
  const [table, checks] = jqPrograms(k6Step());
  assert.ok(table && checks, "two jq programs in the k6 step");
  const run = (prog) => {
    const r = spawnSync("jq", ["-r", prog.replace(/'\\''/g, "'")], { input: file, encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    return r.stdout.trim().split("\n");
  };
  const rows = run(table);
  const v = (k) => METRICS[k].values;
  const ms = (x) => String(Math.round(x * 10) / 10);
  const row = (label, k, budget) => {
    const t = v(k);
    return `| ${label} | ${ms(t["p(50)"])} | ${ms(t["p(95)"])} | ${ms(t.max)} | ${budget} | ${t["p(95)"] < budget ? "ok" : "OVER"} |`;
  };
  assert.deepEqual(rows, [
    row("MISS, Risk preset, whole book", "http_req_duration{scenario:miss}", 150),
    row("HIT, Risk preset", "http_req_duration{scenario:hit}", 15),
    row("MISS, All preset, whole book", "all_miss", 150),
  ]);
  assert.ok(rows.every((r) => !/n\/a|no data/.test(r)), "every cell has data");
  const c = v("checks");
  assert.equal(run(checks)[0].startsWith(`Checks: ${c.passes} passed, ${c.fails} failed`), true);
});
