# ADR-0025: CI image pulls without the Docker Hub anonymous limit

- **Status:** Proposed
- **Date:** 2026-10-13
- **Phase / PR:** [workflows] follow-up (#332)

## Context

The repo went public on 2026-10-09. Since then, CI jobs that pull from Docker Hub fail now and then with `toomanyrequests: You have reached your unauthenticated pull rate limit` (exit 125). GitHub-hosted runners share egress IPs, so the per-IP anonymous limit is shared with everyone else on those IPs. Main CI run 37989948923 lost `secrets`, `workflows`, `e2e`, `budgets` and `compose-smoke` this way, and its Deploy was skipped.

Constraints: no new secrets (secrets live only in `production`, plus the capped key in `claude-review`), every image stays digest-pinned to the same content, gitleaks stays on, Dependabot keeps tracking the pins, and nothing is loosened.

Every Docker Hub pull in CI on `main` @ `342dce8`:

| Image (digest-pinned) | Where | Job(s) |
|---|---|---|
| `zricethezav/gitleaks:v8.30.1` | `docker run` in `ci.yml`, `gitleaks.yml`; `deploy/ci-images/gitleaks` | `secrets`, weekly gitleaks |
| `rhysd/actionlint:1.7.12` | `docker run`; `deploy/ci-images/actionlint` | `workflows` |
| `koalaman/shellcheck:stable` | `docker run`; `deploy/ci-images/shellcheck` | `workflows` |
| `grafana/k6:1.3.0` | `docker run`; `deploy/ci-images/k6` | `budgets` |
| `postgres:17-alpine` | `services:` in `api`; `docker-compose.yml`; Testcontainers literals in `tests/` | `api`, `compose-smoke`, `budgets`, `e2e` |
| `node:22-alpine` | `deploy/Dockerfile` (built by compose) | `compose-smoke`, `budgets`, `e2e` |
| `testcontainers/ryuk` (chosen by the Testcontainers version) | Testcontainers | `api` |

`mcr.microsoft.com/dotnet/*` images are already off Docker Hub.

## Options considered

1. **`docker/login-action` with a read-only Docker Hub token.** This needs a new secret outside `production`, and AGENTS.md doesn't allow that.
2. **Reference the same digests on a registry without that limit.** Use the project's own ghcr.io image where one exists, otherwise `mirror.gcr.io` (Google's cache of Docker Hub). `mirror.gcr.io` makes no promise to keep any one image cached, and a direct `mirror.gcr.io/...` reference has no fallback.
3. **Configure `mirror.gcr.io` as a daemon `registry-mirrors` entry on the runner.** Image references stay `docker.io` (so Dependabot, compose, the Dockerfile and Testcontainers literals don't change). The daemon tries the mirror first and falls back to Docker Hub on a miss.
4. **`actions/cache` + `docker save/load`, keyed on digest.** The first pull of each digest still goes to Docker Hub, the cache is per branch scope, and the images (postgres, node, the SDK build) are large enough to cost the 10 GB cache budget.

## Evaluation

| Criterion | 1 login | 2 direct refs | 3 daemon mirror | 4 cache |
|---|---|---|---|---|
| New secret | yes | no | no | no |
| Same content (digest) | yes | yes (verified below) | yes (digest pin is content-addressed) | yes |
| Fallback when the alternative misses | n/a | none | Docker Hub | Docker Hub |
| Covers Testcontainers + Ryuk, compose, `docker build` | yes | no (C# literals, compose, Dockerfile would all move) | yes | partly |
| Covers job `services:` | yes | yes | no (services start before steps) | no |
| Dependabot keeps tracking | yes | yes for ghcr.io; untested for mirror.gcr.io | yes | yes |

Digest check, run from a workstation with `docker manifest inspect` (it reads the manifest without pulling). A reference by digest only resolves if that exact content exists on that registry.

**How to reproduce:** `bash perf/ci-image-mirrors.sh` (exit 1 on any MISS). The ghcr.io/ECR MISS lines below come from the same command, run by hand on the other candidates.

```
OK   ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0…bbb7f
OK   ghcr.io/grafana/k6@sha256:3ddc8b1a…eb77d
MISS ghcr.io/rhysd/actionlint@sha256:b1934ee5…15667
MISS ghcr.io/koalaman/shellcheck@sha256:bb596a0d…62b3d
OK   mirror.gcr.io/rhysd/actionlint@sha256:b1934ee5…15667
OK   mirror.gcr.io/koalaman/shellcheck@sha256:bb596a0d…62b3d
OK   mirror.gcr.io/library/postgres@sha256:b0f9560a…52b24
MISS public.ecr.aws/docker/library/postgres@sha256:b0f9560a…52b24
OK   mirror.gcr.io/library/node@sha256:0a7108bf…2e402
```

The per-platform manifests behind `ghcr.io/gitleaks/gitleaks:v8.30.1` and `zricethezav/gitleaks@sha256:c00b…` are the same four (`6b2d5224 8af0bd93 b109bc5f e4037a31`). The same holds for `ghcr.io/grafana/k6:1.3.0` and `grafana/k6:1.3.0` (`1a247de7 3b447058 7a249a2d a90b459a`). So the tags on ghcr.io point at the same index.

## Decision

Combine 2 and 3:

- gitleaks and k6 move to their publishers' ghcr.io images at the **same digests** (`ci.yml`, `gitleaks.yml`, `deploy/ci-images/{gitleaks,k6}/Dockerfile`). These pulls no longer touch Docker Hub at all.
- `.github/actions/docker-hub-mirror` adds `https://mirror.gcr.io` to the runner's `registry-mirrors`, merged into the existing `daemon.json`. It reloads dockerd with SIGHUP, which keeps running service containers alive. It fails the job if `docker info` doesn't list the mirror. `workflows`, `api`, `compose-smoke`, `budgets` and `e2e` run it right after checkout. That covers actionlint, shellcheck, Testcontainers postgres and Ryuk, the compose postgres and the `node` base of the app image, with Docker Hub as the fallback.
- The `api` job's `services: postgres` starts before any step, so it references `mirror.gcr.io/library/postgres:17-alpine@sha256:b0f9…` directly. That's the same digest as compose and Testcontainers.

## Consequences

- A normal PR run makes no anonymous Docker Hub pull, unless mirror.gcr.io misses an image. Then that one pull falls back to Docker Hub, which is the behaviour before this change.
- If mirror.gcr.io ever drops `library/postgres@b0f9…`, the `api` job's service fails to start. There's no fallback for that one pull. It's among the most-pulled images and CI requests it on every run, so we accept the risk. The fix would be to start that database from a step (after the mirror action) instead of `services:`.
- `deploy/ci-images/k6/Dockerfile` had drifted to `grafana/k6:2.3.0` (a Dependabot bump) while `ci.yml` still runs 1.3.0. It is realigned to the 1.3.0 digest CI actually runs. A k6 2.x upgrade needs its own PR that moves both lines and re-measures the budgets. #255 (assert the ci-images digests match `ci.yml`) would catch this drift.
- Dependabot's docker ecosystem now reads the gitleaks and k6 pins from ghcr.io (public, anonymous tag listing). If it can't, the symptom is missing bump PRs, not a broken build.
- Local commands in docs still show the Docker Hub names. They pull the same digests, and the rate limit isn't a problem off shared runner IPs.
- Revisit if GitHub-hosted runners get a Docker Hub arrangement, if mirror.gcr.io starts limiting, or when we move off `services:` for the `api` database.
