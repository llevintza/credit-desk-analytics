#!/usr/bin/env node
// ADR-0031 tool names against the guard (scripts/agents/hooks/lib/mcp.mjs, decideMcp): each planned desk MCP tool as
// mcp__desk__<tool> with representative arguments, plus query_positions with a filter model keyed by every snake_case
// identifier in the column catalog (decideMcp walks nested keys for admin keys). The controls print what the guard
// does with write-like names. Exit 1 when a planned name blocks. Usage: node perf/mcp-tool-names.mjs [repo root]
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

const root = process.argv[2] ?? ".";
const { decideMcp } = await import(pathToFileURL(resolve(root, "scripts/agents/hooks/lib/mcp.mjs")).href);

const tools = {
  get_as_of_dates: {},
  list_columns: {},
  list_portfolios: {},
  query_positions: { as_of: "2026-10-19", portfolios: [3], columns: ["market_value"], sort: [{ col_id: "market_value", sort: "desc" }], limit: 100 },
  get_positions_summary: { as_of: "2026-10-19", portfolios: [3] },
  get_fund_performance: { fund_id: 1, from: "2025-10", to: "2026-09" },
  list_insight_sources: {},
  get_insight: { source: "market", as_of: "2026-10-19", limit: 100 },
  list_deals: { as_of: "2026-10-19", limit: 100 },
  get_deal: { deal_id: 1 },
};

// Every snake_case string literal in the column catalog, as a filter-model key (decideMcp walks nested keys).
const cols = [...new Set(readFileSync(resolve(root, "src/Desk.Data/Catalog/ColumnCatalog.cs"), "utf8").match(/"[a-z][a-z0-9_]*"/g).map((s) => s.slice(1, -1)))];
const filters = Object.fromEntries(cols.map((c) => [c, { filter_type: "number", type: "greaterThan", filter: 0 }]));

let failed = 0;
for (const [tool, args] of Object.entries(tools)) {
  const d = decideMcp({ tool: `mcp__desk__${tool}`, args });
  if (d.code !== 0) failed++;
  console.log(`${d.code === 0 ? "allow" : "BLOCK"}  mcp__desk__${tool}${d.reason ? `  (${d.reason})` : ""}`);
}
const q = decideMcp({ tool: "mcp__desk__query_positions", args: { ...tools.query_positions, filters } });
if (q.code !== 0) failed++;
console.log(`${q.code === 0 ? "allow" : "BLOCK"}  mcp__desk__query_positions with a filter on each of ${cols.length} catalog identifiers${q.reason ? `  (${q.reason})` : ""}`);
for (const bad of ["delete_preset", "update_preset", "clear_cache", "dispatch_ingest", "list_workflow_dispatch"]) {
  const d = decideMcp({ tool: `mcp__desk__${bad}`, args: {} });
  console.log(`${d.code === 0 ? "allow" : "BLOCK"}  mcp__desk__${bad}  (control: a name the ADR forbids)`);
}
console.log(failed === 0 ? "all ADR-0031 tool names allowed" : `${failed} ADR-0031 tool name(s) blocked`);
process.exit(failed === 0 ? 0 : 1);
