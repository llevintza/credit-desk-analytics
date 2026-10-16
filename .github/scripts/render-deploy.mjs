// Render deploy id and status for the release job in deploy.yml (#302).
//   node render-deploy.mjs hook     reads the deploy hook's response on stdin and HOOK from env; writes
//                                   deploy_id and service_id to $GITHUB_OUTPUT, logs the id, adds it to the summary.
//   node render-deploy.mjs status   after a failed /health smoke: reads DEPLOY_ID, SERVICE_ID and RENDER_API_KEY
//                                   from env and reports Render's status for that deploy, or that it couldn't be read.
// Both always exit 0: the deploy id and status are diagnostics, so the smoke fails the same way with or without
// them. Neither prints the hook URL, the key or a hostname. The key travels in env only and is masked by the runner.
import { appendFileSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const API = "https://api.render.com/v1";
const DEPLOY_ID = /^dep-[a-z0-9]+$/;
const SERVICE_ID = /^srv-[a-z0-9]+$/;

/** The deploy id from the hook's JSON response (`{"deploy":{"id":"dep-…"}}`), or null. */
export function deployId(body) {
  let json;
  try {
    json = JSON.parse(body);
  } catch {
    return null;
  }
  const id = json?.deploy?.id;
  return typeof id === "string" && DEPLOY_ID.test(id) ? id : null;
}

/** The service id from the hook URL's path (`…/deploy/srv-…?key=…`), or null. The key is never returned. */
export function serviceId(hook) {
  let url;
  try {
    url = new URL(hook);
  } catch {
    return null;
  }
  const id = url.pathname.split("/").filter(Boolean).at(-1);
  return id && SERVICE_ID.test(id) ? id : null;
}

const MEANING = {
  build_failed: "the Docker build failed on Render",
  update_failed: "the build passed but the new instance failed to start",
  pre_deploy_failed: "the pre-deploy command failed",
  canceled: "the deploy was canceled (a newer deploy may have replaced it)",
  deactivated: "a newer deploy replaced this one",
  live: "Render reports it live, but /health did not report this commit",
  created: "still in progress (slow build or cold start)",
  queued: "still in progress (slow build or cold start)",
  build_in_progress: "still in progress (slow build or cold start)",
  update_in_progress: "still in progress (slow build or cold start)",
  pre_deploy_in_progress: "still in progress (slow build or cold start)",
};

/** One line for a Render deploy status; build/start failures and a slow start read differently. */
export function describe(id, status) {
  return `Render deploy ${id}: ${status}: ${MEANING[status] ?? "unrecognised status"}`;
}

export const unreadable = (why) => `Render deploy status could not be read (${why})`;

const summaryWriter = (env) => (env.GITHUB_STEP_SUMMARY ? (s) => appendFileSync(env.GITHUB_STEP_SUMMARY, s) : undefined);

export function hook({ env = process.env, body = readFileSync(0, "utf8"), log = console.log } = {}) {
  const id = deployId(body);
  const service = serviceId(env.HOOK ?? "");
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `deploy_id=${id ?? ""}\nservice_id=${service ?? ""}\n`);
  if (id) log(`Render deploy id: ${id}`);
  else log("::warning::The Render deploy hook returned no deploy id; a failed smoke cannot read the deploy status.");
  summaryWriter(env)?.(`### Render deploy\n- Deploy id: ${id ? `\`${id}\`` : "none returned by the hook"}\n`);
  return 0;
}

/** The status line for this deploy; never throws and never fails on a missing key. */
export async function status({ env = process.env, fetch = globalThis.fetch, log = console.log } = {}) {
  const id = env.DEPLOY_ID ?? "";
  const service = env.SERVICE_ID ?? "";
  let line;
  let level = "warning";
  if (!DEPLOY_ID.test(id)) line = unreadable("no deploy id from the hook");
  else if (!SERVICE_ID.test(service)) line = unreadable("no service id in the hook URL");
  else if (!env.RENDER_API_KEY) line = unreadable("no API key");
  else {
    try {
      const res = await fetch(`${API}/services/${service}/deploys/${id}`, {
        headers: { accept: "application/json", authorization: `Bearer ${env.RENDER_API_KEY}` },
        signal: AbortSignal.timeout(15_000),
      });
      if (!res.ok) line = unreadable(`HTTP ${res.status}`);
      else {
        const s = (await res.json())?.status;
        if (typeof s === "string" && /^[a-z_]+$/.test(s)) [line, level] = [describe(id, s), "error"];
        else line = unreadable("no status in the response");
      }
    } catch {
      // The error text can carry the request URL; say only that it failed.
      line = unreadable("request failed");
    }
  }
  log(`::${level}::${line}`);
  summaryWriter(env)?.(`- ${line}\n`);
  return 0;
}

export async function main(argv = process.argv.slice(2), deps = {}) {
  if (argv[0] === "hook") return hook(deps);
  if (argv[0] === "status") return status(deps);
  (deps.log ?? console.log)("usage: render-deploy.mjs hook|status");
  return 2;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) process.exitCode = await main();
