#!/usr/bin/env node
// Guard hook time budget (docs/agents/adapters.md "Time budget": ≤500 ms at p99, 1 s hard cap):
// runs scripts/agents/hooks/guard.mjs as a fresh node process per call, the way a harness does,
// over a fixed mix of command shapes, and prints p50/p99/max. Exit 1 when p99 is over budget.
//
//   node perf/guard-timing.mjs [runs]
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const GUARD = fileURLToPath(new URL("../scripts/agents/hooks/guard.mjs", import.meta.url));
const COMMANDS = [
  "git status", "cat .env", "npm test && git push -u origin feat", "git push origin main", "gh pr view 5",
  "gh api repos/o/r/pulls/5 -X PATCH -f title=x", "git commit -m \"$(cat <<'EOF'\nx\nEOF\n)\"", "bash -c 'git push origin feat'",
];
const runs = Number(process.argv[2] ?? 80);
const ms = [];
for (let i = 0; i < runs; i++) {
  const input = JSON.stringify({ tool_name: "Bash", tool_input: { command: COMMANDS[i % COMMANDS.length] } });
  const t = process.hrtime.bigint();
  spawnSync(process.execPath, [GUARD], { input, env: { ...process.env, CI: "", GITHUB_ACTIONS: "" } });
  ms.push(Number(process.hrtime.bigint() - t) / 1e6);
}
ms.sort((a, b) => a - b);
const at = (q) => ms[Math.min(ms.length - 1, Math.floor(ms.length * q))].toFixed(0);
console.log(`guard hook, fresh node per call: n=${runs} p50=${at(0.5)} ms p99=${at(0.99)} ms max=${ms.at(-1).toFixed(0)} ms (budget p99 ≤ 500 ms)`);
process.exitCode = Number(at(0.99)) > 500 ? 1 : 0;
