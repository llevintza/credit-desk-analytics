#!/usr/bin/env bash
# ADR-0025 (#332): check that each CI image digest resolves on the registry CI pulls it from instead
# of Docker Hub, and print the per-platform manifests behind a tag so two registries can be compared.
# Reads manifests only (`docker manifest inspect`); pulls nothing. Run from a workstation:
#   bash perf/ci-image-mirrors.sh
set -uo pipefail

refs=(
  "ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f"
  "ghcr.io/grafana/k6@sha256:3ddc8b1a33a2c3d8edc6e99b6a762ae36cba08788463458f5e6a7703e14eb77d"
  "mirror.gcr.io/rhysd/actionlint@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667"
  "mirror.gcr.io/koalaman/shellcheck@sha256:bb596a0d169b85ddd81d8b6d3a2ff6d5baf5fca10b97f575ebc647c3dff62b3d"
  "mirror.gcr.io/library/postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24"
  "mirror.gcr.io/library/node@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402"
)
# Candidates ADR-0025 rejected because they lack these digests: expected MISS, reported, never fail.
rejected=(
  "ghcr.io/rhysd/actionlint@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667"
  "ghcr.io/koalaman/shellcheck@sha256:bb596a0d169b85ddd81d8b6d3a2ff6d5baf5fca10b97f575ebc647c3dff62b3d"
  "public.ecr.aws/docker/library/postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24"
)
tags=(
  "ghcr.io/gitleaks/gitleaks:v8.30.1"
  "zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f"
  "ghcr.io/grafana/k6:1.3.0"
  "grafana/k6@sha256:3ddc8b1a33a2c3d8edc6e99b6a762ae36cba08788463458f5e6a7703e14eb77d"
)

rc=0
for r in "${refs[@]}"; do
  if docker manifest inspect "$r" >/dev/null 2>&1; then echo "OK   $r"; else echo "MISS $r"; rc=1; fi
done
echo
for r in "${rejected[@]}"; do
  if docker manifest inspect "$r" >/dev/null 2>&1; then echo "now OK (rejected candidate) $r"; else echo "MISS $r (rejected candidate)"; fi
done
echo
for t in "${tags[@]}"; do
  printf '%s\n  ' "$t"
  docker manifest inspect -v "$t" 2>/dev/null | grep -o '"Ref": "[^"]*@sha256:[0-9a-f]*' | sed 's/.*@sha256://' | cut -c1-8 | sort | tr '\n' ' '
  echo
done
exit "$rc"
