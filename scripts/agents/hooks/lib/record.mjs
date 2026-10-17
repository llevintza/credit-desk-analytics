/**
 * Once-per-tool-call record (adapters.md C7, testing.md "Guard under two hook sources").
 *
 * Grok and Cursor can deliver one tool call twice (the imported Claude hook and the generated
 * `.cursor/hooks.json` entry). The key is the normalized call (kind, command or path and
 * contents, cwd), not the tool name or shape, so both deliveries share it. Only BLOCKS are
 * replayed, for a few seconds: the record lives in a shared temp dir, so an allow record could
 * be planted by an earlier command, and re-evaluating an allow is cheap and gives the same
 * answer. A recorded block is never turned into an allow; a missing or unreadable record
 * means full evaluation.
 */
import { createHash } from "node:crypto";
import { readFileSync, renameSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

export const RECORD_FILE = join(tmpdir(), "agents-guard-calls.json");
export const BLOCK_TTL_MS = 5000;

export function callKey(call, cwd) {
  const parts = [call.kind, call.command ?? null, call.path ?? null, call.contents ?? null, call.kind === "mcp" ? [call.tool, call.args] : null, call.cwd ?? cwd];
  return createHash("sha256").update(JSON.stringify(parts)).digest("hex");
}

function load(file) {
  try {
    const all = JSON.parse(readFileSync(file, "utf8"));
    return all !== null && typeof all === "object" && !Array.isArray(all) ? all : {};
  } catch {
    return {};
  }
}

/** The recorded block for this key, if one is fresh. */
export function recall(key, now, file = RECORD_FILE) {
  const e = load(file)[key];
  return e?.code === 2 && typeof e.reason === "string" && now - e.at >= 0 && now - e.at <= BLOCK_TTL_MS ? e : undefined;
}

/** Record a block. Best-effort: a failed write only means the next delivery re-evaluates. */
export function remember(key, decision, now, file = RECORD_FILE) {
  if (decision.code !== 2) return;
  const all = load(file);
  for (const [k, e] of Object.entries(all)) if (!(now - e?.at <= BLOCK_TTL_MS)) delete all[k];
  all[key] = { code: 2, reason: decision.reason, at: now };
  try {
    const tmp = `${file}.${process.pid}.tmp`;
    writeFileSync(tmp, JSON.stringify(all));
    renameSync(tmp, file);
  } catch {
    // Not recorded; the second delivery evaluates again and reaches the same block.
  }
}
