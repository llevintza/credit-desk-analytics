#!/usr/bin/env node
// ADR-0022 evidence: replays the change classifier over the last N first-parent
// commits of a ref and prints which CI jobs each would have run.
// Usage: node perf/ci-changes-replay.mjs [count=15] [ref=origin/main]
import { execFileSync } from "node:child_process";
import { classify } from "../.github/scripts/ci-changes.mjs";

const [count = "15", ref = "origin/main"] = process.argv.slice(2);
const git = (...args) => execFileSync("git", args, { encoding: "utf8" }).trim();

console.log("| Commit | Subject | Areas | Heavy jobs run | Why every flag |");
console.log("|---|---|---|---|---|");
for (const sha of git("log", "--first-parent", "--format=%H", `-${Number(count)}`, ref).split("\n")) {
  const subject = git("log", "-1", "--format=%s", sha).slice(0, 60).replaceAll("|", "\\|");
  const paths = git("diff", "--name-only", "--no-renames", `${sha}^`, sha).split("\n").filter(Boolean);
  const r = classify(paths);
  const areas = Object.keys(r.flags).filter((k) => r.flags[k]).join(", ") || "docs only";
  const jobs = Object.keys(r.jobs).filter((k) => r.jobs[k]).map((k) => k.slice(4)).join(", ") || "none";
  console.log(`| \`${sha.slice(0, 7)}\` | ${subject} | ${areas} | ${jobs} | ${r.reasons[0] ?? ""} |`);
}
