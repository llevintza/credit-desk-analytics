#!/usr/bin/env node
/**
 * Secret and policy guard hook (ADR-0020 §6; docs/agents/adapters.md "Hooks", "What the guard
 * blocks"; exit-code table in docs/agents/testing.md). Best-effort defense in depth: the
 * primary controls are the `main` ruleset, CI and human review.
 *
 *   node scripts/agents/hooks/guard.mjs < hook-input.json
 *
 * Exit 0 allows, exit 2 blocks (with a stderr reason, and the Cursor-native deny reply on
 * stdout). Any exception exits 2 (G5), and so does the 1 s hard cap (whatever the input).
 * Under CI it appends to the hook canary first, then evaluates as usual. `node:` only, and it
 * never runs git, a network call or anything from the payload.
 */
import { createHash } from "node:crypto";
import { appendFileSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { decide } from "./lib/decide.mjs";
import { callKey, recall, remember } from "./lib/record.mjs";

export const HARD_CAP_MS = 1000;
export const PATTERNS_FILE = fileURLToPath(new URL("./secret-patterns.txt", import.meta.url));
export const UNRECOGNIZED_LOG = join(tmpdir(), "agents-guard-unrecognized.log");

/**
 * secret-patterns.txt (generated from `.gitleaks.toml`): one `<id> <regex>` per line, `#`
 * comments. Go's leading `(?i)` becomes the `i` flag. A missing file means no extra patterns
 * (the `Password=` rule is built in); a bad regex throws, so writes fail closed.
 */
export function loadPatterns(file = PATTERNS_FILE) {
  let text;
  try {
    text = readFileSync(file, "utf8");
  } catch (e) {
    if (e.code === "ENOENT") return [];
    throw e;
  }
  return text.split("\n").filter((l) => l.trim() && !l.startsWith("#")).map((l) => {
    const m = /^([\w.-]+)\s+(.+)$/.exec(l.trim());
    if (!m) throw new Error(`secret-patterns.txt: malformed line ${JSON.stringify(l.slice(0, 80))}`);
    const ci = m[2].startsWith("(?i)");
    return { id: m[1], re: new RegExp((ci ? m[2].slice(4) : m[2]).replace(/\(\?P</g, "(?<"), ci ? "i" : "") };
  });
}

async function readAll(stream) {
  const chunks = [];
  for await (const c of stream) chunks.push(typeof c === "string" ? Buffer.from(c) : c);
  return Buffer.concat(chunks).toString("utf8");
}

/** Evaluate one hook input. Returns the exit code; never throws. */
export async function main({
  stdin = process.stdin, stdout = process.stdout, stderr = process.stderr, env = process.env,
  now = Date.now, patternsFile = PATTERNS_FILE, recordFile, logFile = UNRECOGNIZED_LOG, decideImpl = decide,
} = {}) {
  const deadline = now() + HARD_CAP_MS;
  if (env.CI || env.GITHUB_ACTIONS === "true") {
    try {
      appendFileSync(join(env.RUNNER_TEMP || tmpdir(), "agents-hooks-ran"), "guard\n");
    } catch {
      // The canary is a signal for the review job; evaluation goes on without it.
    }
  }
  let d;
  try {
    const raw = await readAll(stdin);
    let patterns;
    const lazy = () => (patterns ??= loadPatterns(patternsFile));
    const recorded = (call) => {
      const prior = recall(callKey(call, process.cwd()), now(), recordFile);
      return prior && { code: 2, reason: `${prior.reason} (recorded for this call)`, recorded: true };
    };
    d = decideImpl(raw, { patterns: lazy, deadline, recorded });
    if (now() > deadline) d = { ...d, code: 2, reason: `the ${HARD_CAP_MS} ms hard cap was reached` };
    if (d.call?.recognized && !d.recorded) remember(callKey(d.call, process.cwd()), d, now(), recordFile);
    if (d.unrecognized) {
      stderr.write("agents guard: unrecognized hook input; not enforcing\n");
      const hash = createHash("sha256").update(raw).digest("hex").slice(0, 16);
      try {
        appendFileSync(logFile, `${new Date(now()).toISOString()} bytes=${Buffer.byteLength(raw)} sha256=${hash}\n`);
      } catch {
        // Logging is best-effort.
      }
    }
  } catch (e) {
    d = { code: 2, reason: `guard error, failing closed (G5): ${e?.message ?? e}` };
  }
  if (d.code !== 2) return 0;
  stderr.write(`agents guard: blocked: ${d.reason}\n`);
  if (d.call?.shape === "cursor-native") stdout.write(`${JSON.stringify({ permission: "deny", user_message: `agents guard: ${d.reason}` })}\n`);
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const cap = setTimeout(() => {
    process.stderr.write(`agents guard: blocked: the ${HARD_CAP_MS} ms hard cap was reached\n`);
    process.exit(2);
  }, HARD_CAP_MS);
  main().then((code) => {
    clearTimeout(cap);
    process.exitCode = code;
  }, () => process.exit(2));
}
