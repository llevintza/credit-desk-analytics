// Compares ClientAddress.Cloudflare (src/Desk.Api) with Cloudflare's published IP ranges (#161).
// A range Cloudflare adds that the app doesn't know keys every client behind it on the edge, which
// brings back the shared login bucket (#116). Run weekly by .github/workflows/cloudflare-ips.yml;
// exits 1 on drift (printing the added and removed ranges) and 2 when either list can't be read.
// The fetch belongs to that workflow only: agent sessions run the fixture tests, never this file.
import { appendFileSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const API_URL = "https://api.cloudflare.com/client/v4/ips";
export const SOURCE = "src/Desk.Api/Limits/ClientAddress.cs";

const CIDR = /^[0-9a-f.:]+\/\d{1,3}$/i;
const norm = (r) => r.trim().toLowerCase();

/** The CIDR literals inside `Cloudflare = [ … ];` in ClientAddress.cs. */
export function extractRanges(source) {
  const block = /\bCloudflare\s*=\s*\[([\s\S]*?)\];/.exec(source);
  if (!block) throw new Error(`Cloudflare = [ … ] not found in ${SOURCE}`);
  const ranges = [...block[1].matchAll(/"([^"]+)"/g)].map((m) => norm(m[1])).filter((r) => CIDR.test(r));
  if (ranges.length === 0) throw new Error(`no CIDR literals in ClientAddress.Cloudflare (${SOURCE})`);
  return ranges;
}

/** ipv4_cidrs + ipv6_cidrs from the API body; throws on anything that isn't a full, successful list. */
export function published(body) {
  if (!body?.success) throw new Error(`Cloudflare API returned success=false: ${JSON.stringify(body?.errors ?? [])}`);
  const { ipv4_cidrs: v4, ipv6_cidrs: v6 } = body.result ?? {};
  if (!Array.isArray(v4) || !Array.isArray(v6) || v4.length === 0 || v6.length === 0)
    throw new Error("Cloudflare API returned no ipv4_cidrs/ipv6_cidrs");
  const ranges = [...v4, ...v6].map((r) => norm(String(r)));
  const bad = ranges.find((r) => !CIDR.test(r));
  if (bad) throw new Error(`Cloudflare API returned a value that is not a CIDR: ${bad}`);
  return ranges;
}

/** added: published but missing from the app; removed: in the app but no longer published. */
export function compare(expected, actual) {
  const have = new Set(expected.map(norm));
  const want = new Set(actual.map(norm));
  return {
    added: [...want].filter((r) => !have.has(r)).sort(),
    removed: [...have].filter((r) => !want.has(r)).sort(),
  };
}

export function report({ added, removed }, count) {
  if (added.length === 0 && removed.length === 0)
    return `ClientAddress.Cloudflare and ${SOURCE} match Cloudflare's published list (${count} ranges).`;
  return [
    `ClientAddress.Cloudflare differs from ${API_URL}:`,
    ...added.map((r) => `+ ${r} (published, missing from ClientAddress.cs)`),
    ...removed.map((r) => `- ${r} (in ClientAddress.cs, no longer published)`),
    `Update ClientAddress.Cloudflare in ${SOURCE} (and its "checked" date) in an app PR.`,
  ].join("\n");
}

export async function main({
  fetch = globalThis.fetch,
  readSource = () => readFileSync(SOURCE, "utf8"),
  log = console.log,
  error = console.error,
  summary = process.env.GITHUB_STEP_SUMMARY ? (s) => appendFileSync(process.env.GITHUB_STEP_SUMMARY, s) : undefined,
} = {}) {
  let expected, actual;
  try {
    expected = extractRanges(readSource());
    const res = await fetch(API_URL, { headers: { accept: "application/json" }, signal: AbortSignal.timeout(30_000) });
    if (!res.ok) throw new Error(`GET ${API_URL}: HTTP ${res.status}`);
    actual = published(await res.json());
  } catch (e) {
    error(`::error::${e.message}`);
    return 2;
  }
  const diff = compare(expected, actual);
  const text = report(diff, actual.length);
  log(text);
  summary?.(`## Cloudflare IP ranges (#161)\n\n\`\`\`\n${text}\n\`\`\`\n`);
  if (diff.added.length || diff.removed.length) {
    error(`::error::ClientAddress.Cloudflare differs from Cloudflare's published list (+${diff.added.length} -${diff.removed.length})`);
    return 1;
  }
  return 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) process.exitCode = await main();
