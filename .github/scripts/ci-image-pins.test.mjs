import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";

// #255: every container image a workflow runs is pinned by digest, and the same tag and digest are
// tracked where Dependabot bumps them (deploy/ci-images/<name>/Dockerfile, or docker-compose.yml for
// the api job's postgres service). A Dependabot bump on one side without the other fails gate-tests.
const root = new URL("../../", import.meta.url);
const read = (p) => readFileSync(new URL(p, root), "utf8");

const DIGEST = /@sha256:[0-9a-f]{64}$/;
// <name>[:<tag>]@sha256:<digest>, name with an optional registry host.
const REF = /^((?:[a-z0-9.-]+(?::\d+)?\/)?[a-z0-9._-]+(?:\/[a-z0-9._-]+)*)(?::([\w.-]+))?(@sha256:[0-9a-f]{64})?$/;

/** The registry-independent name: mirror.gcr.io and docker.io serve Docker Hub's names (ADR-0025). */
export function canonical(name) {
  let n = name.replace(/^(mirror\.gcr\.io|docker\.io|registry-1\.docker\.io|index\.docker\.io)\//, "");
  n = n.replace(/^library\//, "");
  return n;
}

export function parseRef(ref) {
  const m = REF.exec(ref);
  if (!m) return null;
  return { ref, name: m[1], canonical: canonical(m[1]), tag: m[2] ?? null, digest: m[3]?.slice(1) ?? null };
}

// docker run flags that take a separate value; everything else before the image is a bare flag.
const VALUE_FLAGS = new Set(["-v", "--volume", "-e", "--env", "-w", "--workdir", "-u", "--user", "--network", "--net",
  "--add-host", "--name", "--entrypoint", "-p", "--publish", "--mount", "--env-file", "-l", "--label", "--platform",
  "--cpus", "-m", "--memory", "--tmpfs", "--cap-add", "--cap-drop", "--security-opt", "--ulimit", "-h", "--hostname"]);

/** Shell-ish words of a command, with `\` continuations and folded (`>`) blocks joined. */
const words = (cmd) => cmd.replace(/\\\n/g, " ").split(/\s+/).filter(Boolean);

/** The image of each `docker run` in a workflow (the first positional after the options). */
export function dockerRunImages(yml) {
  const out = [];
  // A `docker run` and its continuation lines: deeper-indented lines of the same run block (folded `>`, `|` or `\`).
  for (const m of yml.matchAll(/docker run((?:[^\n]|\n(?! {0,8}\S| *- ))*)/g)) {
    const ws = words(m[1]);
    for (let i = 0; i < ws.length; i++) {
      const w = ws[i];
      if (w.startsWith("-")) {
        if (VALUE_FLAGS.has(w)) i++;
        continue;
      }
      out.push(w.replace(/^["']|["']$/g, ""));
      break;
    }
  }
  return out;
}

/** `image:` keys (services, container) and `uses: docker://` refs. */
export function keyedImages(yml) {
  const out = [];
  for (const m of yml.matchAll(/^\s*(?:image|-?\s*uses):\s*["']?(?:docker:\/\/)?([^\s"'#]+)["']?/gm)) {
    if (/^\s*-?\s*uses:/.test(m[0]) && !m[0].includes("docker://")) continue;
    out.push(m[1]);
  }
  return out;
}

export function workflowImages(files) {
  const refs = [];
  for (const [file, yml] of files) for (const ref of [...dockerRunImages(yml), ...keyedImages(yml)]) refs.push({ file, ref });
  return refs;
}

/** Tracked pins: FROM lines in the ci-images Dockerfiles plus compose `image:` lines. */
export function trackedPins(dockerfiles, compose) {
  const pins = [];
  for (const [dir, text] of dockerfiles)
    for (const m of text.matchAll(/^FROM\s+(\S+)/gm)) pins.push({ source: `${dir}/Dockerfile`, dir, ...parseRef(m[1]) });
  for (const m of compose.matchAll(/^\s*image:\s*(\S+)/gm)) pins.push({ source: "docker-compose.yml", dir: "/", ...parseRef(m[1]) });
  return pins;
}

/** Every problem with the pins, as readable strings (empty means consistent). */
export function pinProblems({ workflows, dockerfiles, compose, dependabotDirs, composeTracked }) {
  const problems = [];
  const pins = trackedPins(dockerfiles, compose);
  for (const p of pins) if (!p.digest) problems.push(`${p.source}: ${p.ref} is not pinned by digest`);
  for (const [dir] of dockerfiles)
    if (!dependabotDirs.includes(`/${dir}`)) problems.push(`${dir}/Dockerfile has no Dependabot docker directories: entry`);
  for (const { file, ref } of workflowImages(workflows)) {
    const r = parseRef(ref);
    if (!r) {
      problems.push(`${file}: can't parse image ${ref}`);
      continue;
    }
    if (!r.digest) {
      problems.push(`${file}: ${ref} is not pinned by digest`);
      continue;
    }
    const same = pins.filter((p) => p.canonical === r.canonical && (p.source !== "docker-compose.yml" || composeTracked));
    if (same.length === 0) {
      problems.push(`${file}: ${ref} has no tracking Dockerfile under deploy/ci-images (or compose image)`);
      continue;
    }
    for (const p of same)
      if (p.tag !== r.tag || p.digest !== r.digest)
        problems.push(`${file}: ${ref} disagrees with ${p.source} (${p.ref})`);
  }
  return problems;
}

// --- the repo as it is ---

const wfDir = ".github/workflows/";
const WORKFLOWS = readdirSync(new URL(wfDir, root))
  .filter((f) => f.endsWith(".yml"))
  .map((f) => [`${wfDir}${f}`, read(`${wfDir}${f}`)]);
const DOCKERFILES = readdirSync(new URL("deploy/ci-images/", root))
  .filter((d) => existsSync(new URL(`deploy/ci-images/${d}/Dockerfile`, root)))
  .map((d) => [`deploy/ci-images/${d}`, read(`deploy/ci-images/${d}/Dockerfile`)]);
const COMPOSE = read("docker-compose.yml");
const DEPENDABOT = read(".github/dependabot.yml");
const dockerBlock = /- package-ecosystem: docker\n([\s\S]*?)(?=\n {2}- package-ecosystem:|$)/.exec(DEPENDABOT)?.[1] ?? "";
const DEPENDABOT_DIRS = [...dockerBlock.matchAll(/^ {6}- (\/\S*)$/gm)].map((m) => m[1]);
const COMPOSE_TRACKED = /- package-ecosystem: docker-compose\n {4}directory: \/\n/.test(DEPENDABOT);
const REPO = { workflows: WORKFLOWS, dockerfiles: DOCKERFILES, compose: COMPOSE, dependabotDirs: DEPENDABOT_DIRS, composeTracked: COMPOSE_TRACKED };

test("the repo: every workflow image is digest-pinned and agrees with its tracked pin", () => {
  assert.deepEqual(pinProblems(REPO), []);
  // The images this is meant to cover are all found (a parser regression must not pass vacuously).
  const found = new Set(workflowImages(WORKFLOWS).map(({ ref }) => parseRef(ref)?.canonical));
  for (const n of ["ghcr.io/gitleaks/gitleaks", "rhysd/actionlint", "koalaman/shellcheck", "ghcr.io/grafana/k6", "postgres"])
    assert.ok(found.has(n), `workflow image ${n} found`);
  assert.equal(DOCKERFILES.length, 4);
  assert.ok(COMPOSE_TRACKED, "Dependabot tracks docker-compose.yml");
});

// --- fixtures: each drift fails ---

const swap = (text, from, to) => {
  assert.ok(text.includes(from), `fixture anchor ${from}`);
  return text.replace(from, to);
};
const k6Digest = /ghcr\.io\/grafana\/k6:[\w.]+@sha256:([0-9a-f]{64})/.exec(read("deploy/ci-images/k6/Dockerfile"))[1];
const OTHER = "0".repeat(64);

test("a digest bumped in the Dockerfile only fails", () => {
  const dockerfiles = DOCKERFILES.map(([d, t]) => [d, d.endsWith("/k6") ? swap(t, k6Digest, OTHER) : t]);
  const p = pinProblems({ ...REPO, dockerfiles });
  assert.ok(p.some((s) => /\.github\/workflows\/ci\.yml: ghcr\.io\/grafana\/k6:.* disagrees with deploy\/ci-images\/k6\/Dockerfile/.test(s)), p.join("\n"));
});

test("a digest or tag changed in the workflow only fails", () => {
  const ci = WORKFLOWS.find(([f]) => f.endsWith("ci.yml"))[1];
  for (const changed of [swap(ci, k6Digest, OTHER), ci.replace(/ghcr\.io\/grafana\/k6:[\w.]+@/g, "ghcr.io/grafana/k6:9.9.9@")]) {
    const workflows = WORKFLOWS.map(([f, t]) => [f, f.endsWith("ci.yml") ? changed : t]);
    assert.ok(pinProblems({ ...REPO, workflows }).some((s) => /disagrees with deploy\/ci-images\/k6\/Dockerfile/.test(s)));
  }
});

test("the postgres service image follows docker-compose.yml through the mirror prefix", () => {
  const compose = COMPOSE.replace(/(postgres:17-alpine@sha256:)[0-9a-f]{64}/, `$1${OTHER}`);
  const p = pinProblems({ ...REPO, compose });
  assert.ok(p.some((s) => /mirror\.gcr\.io\/library\/postgres:.* disagrees with docker-compose\.yml/.test(s)), p.join("\n"));
});

test("a workflow image without a digest, or without a tracking pin, fails", () => {
  const extra = [".github/workflows/x.yml", "jobs:\n  a:\n    steps:\n      - run: docker run --rm -v \"$PWD\":/r:ro alpine:3.20 true\n      - run: >\n          docker run --rm\n          -e A=1\n          busybox:1.36@sha256:" + OTHER + "\n          true\n"];
  const p = pinProblems({ ...REPO, workflows: [...WORKFLOWS, extra] });
  assert.ok(p.includes(".github/workflows/x.yml: alpine:3.20 is not pinned by digest"), p.join("\n"));
  assert.ok(p.some((s) => /x\.yml: busybox:1\.36@sha256:0+ has no tracking Dockerfile/.test(s)), p.join("\n"));
});

test("container:, services image: and uses: docker:// refs are checked too", () => {
  const yml = "jobs:\n  a:\n    container:\n      image: node:22\n    services:\n      db:\n        image: redis:7@sha256:" + OTHER + "\n    steps:\n      - uses: docker://alpine:3.20\n      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1\n";
  const refs = workflowImages([["y.yml", yml]]).map((r) => r.ref);
  assert.deepEqual(refs.sort(), ["alpine:3.20", "node:22", `redis:7@sha256:${OTHER}`]);
});

test("a ci-images Dockerfile without a Dependabot entry, or a FROM without a digest, fails", () => {
  const p = pinProblems({ ...REPO, dockerfiles: [...DOCKERFILES, ["deploy/ci-images/new", "FROM alpine:3.20\n"]] });
  assert.ok(p.includes("deploy/ci-images/new/Dockerfile has no Dependabot docker directories: entry"));
  assert.ok(p.includes("deploy/ci-images/new/Dockerfile: alpine:3.20 is not pinned by digest"));
});

test("canonical and parseRef", () => {
  assert.equal(canonical("mirror.gcr.io/library/postgres"), "postgres");
  assert.equal(canonical("docker.io/rhysd/actionlint"), "rhysd/actionlint");
  assert.equal(canonical("ghcr.io/grafana/k6"), "ghcr.io/grafana/k6");
  assert.deepEqual(parseRef("localhost:5000/a/b:1@sha256:" + OTHER), {
    ref: "localhost:5000/a/b:1@sha256:" + OTHER, name: "localhost:5000/a/b", canonical: "localhost:5000/a/b", tag: "1", digest: "sha256:" + OTHER,
  });
  assert.equal(parseRef("Not An Image"), null);
  assert.ok(DIGEST.test("x@sha256:" + OTHER));
});
