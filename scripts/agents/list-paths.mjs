#!/usr/bin/env node
/**
 * Write a checkout's tree listing in pr-data's paths.json shape, for check-drift.mjs in the
 * head-run `agents-generate` job and in local runs. check-drift never lists the head itself
 * (#507); the base-run job gets its listing from pr-data instead.
 *
 *   node scripts/agents/list-paths.mjs --root <checkout> --out <paths.json>
 */
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gitListing } from "./check-drift.mjs";

export function main(argv, { write = (s) => process.stdout.write(s) } = {}) {
  const at = (n) => (argv.indexOf(n) >= 0 ? argv[argv.indexOf(n) + 1] : undefined);
  const root = at("--root");
  const out = at("--out");
  if (!root || !out) {
    write("::error::usage: list-paths.mjs --root <checkout> --out <paths.json>\n");
    return 1;
  }
  try {
    const listing = gitListing(resolve(root));
    writeFileSync(out, JSON.stringify(listing));
    write(`list-paths: ${listing.length} entries\n`);
    return 0;
  } catch (err) {
    write(`::error::list-paths: ${err.message}\n`);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
