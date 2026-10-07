#!/usr/bin/env bash
# Reproduces GitHub Actions' two Linux shells (workflow syntax: defaults.run.shell).
# Unspecified default: bash -e {0}                         — NO pipefail
# shell: bash:         bash --noprofile --norc -eo pipefail {0}
#
# Usage: bash perf/pipefail-demo.sh

set -u
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

echo "=== GitHub default Linux shell: bash -e (no pipefail) ==="
set +e
bash -e -c "false | tee '$tmp'; echo \"pipeline_status=\$? (tee won)\""
echo "script_exit=$?"
echo

echo "=== GitHub shell: bash (bash --noprofile --norc -eo pipefail) ==="
bash --noprofile --norc -eo pipefail -c "false | tee '$tmp'; echo \"pipeline_status=\$? (not reached if pipefail works)\""
echo "script_exit=$?"
