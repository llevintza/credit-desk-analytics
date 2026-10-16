import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { compare, extractRanges, main, published, report, SOURCE, URL as API } from "./cloudflare-ips.mjs";

// #161: the weekly cloudflare-ips workflow compares ClientAddress.Cloudflare with Cloudflare's published
// list. These cover the comparison with a fixture; the network fetch runs only in that workflow.
const FIXTURE = JSON.parse(readFileSync(new URL("./fixtures/cloudflare-ips.json", import.meta.url), "utf8"));
const CS = readFileSync(new URL(`../../${SOURCE}`, import.meta.url), "utf8");
const WORKFLOW = readFileSync(new URL("../workflows/cloudflare-ips.yml", import.meta.url), "utf8");

const source = (ranges) => `
    internal static readonly IPNetwork[] Private = [ .. new[] { "10.0.0.0/8", "::1/128" }.Select(IPNetwork.Parse) ];
    internal static readonly IPNetwork[] Cloudflare =
    [
        .. new[]
        {
            ${ranges.map((r) => `"${r}"`).join(", ")},
        }.Select(IPNetwork.Parse),
    ];
    public const string TrustedHopsKey = "CLIENT_ADDRESS_TRUSTED_HOPS";`;

const fakeFetch = (body, { ok = true, status = 200 } = {}) => {
  const calls = [];
  const fn = async (url, init) => {
    calls.push([url, init]);
    return { ok, status, json: async () => body };
  };
  fn.calls = calls;
  return fn;
};

const run = async (opts) => {
  const out = [];
  const err = [];
  const summary = [];
  const code = await main({
    readSource: () => CS,
    log: (s) => out.push(s),
    error: (s) => err.push(s),
    summary: (s) => summary.push(s),
    ...opts,
  });
  return { code, out: out.join("\n"), err: err.join("\n"), summary: summary.join("") };
};

test("extractRanges reads only the Cloudflare block of ClientAddress.cs", () => {
  const ranges = extractRanges(CS);
  assert.equal(ranges.length, 22);
  assert.equal(ranges.filter((r) => r.includes(":")).length, 7);
  assert.ok(!ranges.includes("10.0.0.0/8"), "Private ranges are not Cloudflare's");
  assert.deepEqual(extractRanges(source(["1.2.3.0/24", "2001:DB8::/32"])), ["1.2.3.0/24", "2001:db8::/32"]);
});

test("extractRanges fails loudly when the block or its literals are gone", () => {
  assert.throws(() => extractRanges("class X {}"), /Cloudflare = \[ … \] not found/);
  assert.throws(() => extractRanges(source([])), /no CIDR literals/);
});

test("the ranges in ClientAddress.cs match the fixture (Cloudflare's list as of 2026-10-16)", () => {
  assert.deepEqual(compare(extractRanges(CS), published(FIXTURE)), { added: [], removed: [] });
});

test("published validates the API response", () => {
  assert.equal(published(FIXTURE).length, 22);
  assert.throws(() => published({ success: false, errors: [{ message: "nope" }] }), /success=false.*nope/);
  assert.throws(() => published(null), /success=false/);
  assert.throws(() => published({ success: true, result: { ipv4_cidrs: [] } }), /no ipv4_cidrs\/ipv6_cidrs/);
  assert.throws(() => published({ success: true, result: { ipv4_cidrs: ["1.2.3.0/24"], ipv6_cidrs: [] } }), /no ipv4_cidrs\/ipv6_cidrs/);
  assert.throws(() => published({ success: true, result: { ipv4_cidrs: ["1.2.3.4"], ipv6_cidrs: ["::/0"] } }), /not a CIDR: 1\.2\.3\.4/);
});

test("compare reports added and removed ranges, sorted, ignoring order, case and duplicates", () => {
  assert.deepEqual(compare(["b/1", "a/1", "A:B::/32"], ["a/1", "a:b::/32", "b/1", "a/1"]), { added: [], removed: [] });
  assert.deepEqual(compare(["a/1", "c/1", "d/1"], ["b/1", "a/1", "e/1"]), { added: ["b/1", "e/1"], removed: ["c/1", "d/1"] });
});

test("report names each added and removed range and what to do", () => {
  assert.match(report({ added: [], removed: [] }, 22), /match Cloudflare's published list \(22 ranges\)/);
  const text = report({ added: ["9.9.9.0/24"], removed: ["8.8.8.0/24"] }, 22);
  assert.match(text, /\+ 9\.9\.9\.0\/24 \(published, missing from ClientAddress\.cs\)/);
  assert.match(text, /- 8\.8\.8\.0\/24 \(in ClientAddress\.cs, no longer published\)/);
  assert.match(text, /Update ClientAddress\.Cloudflare/);
});

test("main passes when the published list matches, and fetches the API once", async () => {
  const fetch = fakeFetch(FIXTURE);
  const r = await run({ fetch });
  assert.equal(r.code, 0);
  assert.deepEqual(fetch.calls.map(([u]) => u), [API]);
  assert.ok(fetch.calls[0][1].signal, "the fetch has a timeout");
  assert.match(r.out, /match/);
  assert.match(r.summary, /^## Cloudflare IP ranges \(#161\)\n/);
});

test("main fails on drift and prints the added and removed ranges", async () => {
  const drifted = structuredClone(FIXTURE);
  drifted.result.ipv4_cidrs = drifted.result.ipv4_cidrs.filter((r) => r !== "131.0.72.0/22").concat("203.0.113.0/24");
  const r = await run({ fetch: fakeFetch(drifted) });
  assert.equal(r.code, 1);
  assert.match(r.err, /^::error::ClientAddress\.Cloudflare differs/);
  assert.match(r.out, /\+ 203\.0\.113\.0\/24/);
  assert.match(r.out, /- 131\.0\.72\.0\/22/);
  assert.match(r.summary, /203\.0\.113\.0\/24/);
});

test("main fails on an HTTP error or a bad body", async () => {
  const http = await run({ fetch: fakeFetch(FIXTURE, { ok: false, status: 503 }) });
  assert.equal(http.code, 2);
  assert.match(http.err, /::error::.*HTTP 503/);
  const bad = await run({ fetch: fakeFetch({ success: false }) });
  assert.equal(bad.code, 2);
  assert.match(bad.err, /success=false/);
});

test("main runs without a step summary (local, opt-in)", async () => {
  const r = await run({ fetch: fakeFetch(FIXTURE), summary: null });
  assert.equal(r.code, 0);
});

test("the workflow is weekly plus dispatch, read-only, SHA-pinned, secret-free, and runs this script", () => {
  assert.match(WORKFLOW, /^on:\n  schedule:\n    - cron: "\d+ \d+ \* \* [0-6]"\n  workflow_dispatch:\n\n/m);
  assert.match(WORKFLOW, /^permissions:\n  contents: read\n/m);
  assert.doesNotMatch(WORKFLOW, /: write\b|secrets\.|pull_request_target/);
  const uses = [...WORKFLOW.matchAll(/uses: (\S+)/g)].map((m) => m[1]);
  assert.ok(uses.length >= 2);
  for (const u of uses) assert.match(u, /@[0-9a-f]{40}$/, u);
  assert.match(WORKFLOW, /persist-credentials: false/);
  assert.match(WORKFLOW, /timeout-minutes: \d+/);
  assert.match(WORKFLOW, /run: node \.github\/scripts\/cloudflare-ips\.mjs\n/);
});
