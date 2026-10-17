/**
 * Heavy jobs in ci.yml and the areas that run them (README §14.1). `secrets`, `workflows`,
 * `gate-tests` and `perf-deps` always run and are not listed.
 *
 * Kept apart from ci-changes.mjs (which runs git) so the parse-only ci-wiring tests import
 * nothing that can start a process (ADR-0026). This module has no imports and no side
 * effects; workflow-hardening.test.mjs and the ci-wiring (base) job check that.
 */
export const JOBS = Object.freeze({
  run_api: Object.freeze(["api", "db"]), // every .NET suite, then migrate + seed
  run_web: Object.freeze(["web"]),
  run_db_tools: Object.freeze(["db"]),
  run_compose_smoke: Object.freeze(["app"]),
  run_e2e: Object.freeze(["app"]),
  run_budgets: Object.freeze(["app", "perf"]), // seeded compose stack + perf/payload-size.mjs
  run_coverage: Object.freeze(["api", "db", "web"]), // whenever the api or web job runs
});
