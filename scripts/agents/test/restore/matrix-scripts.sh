#!/usr/bin/env bash
# Scripts-step matrix (rebuilt, #171 B7): 14 runs of "Restore scripts/agents from base" on its own,
# 9 with a base scripts/agents (s01–s09) and 5 at bootstrap, before base has one (s10–s14).
# Usage: matrix-scripts.sh <claude-review.yml>. Env: ONLY="s03 s07", BLOCK_S (mutations.sh).
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
# shellcheck source=common.sh
. "$here/common.sh"

setup_s01() { :; }
setup_s02() { printf 'pr drift\n' > scripts/agents/check-drift.mjs; }
setup_s03() { printf 'x\n' > scripts/agents/evil.mjs; }
setup_s04() { rm -rf -- scripts; }
setup_s05() { rm -rf -- scripts/agents; ln -s ../web scripts/agents; }
setup_s06() { rm -rf -- scripts/agents; printf 'x\n' > scripts/agents; }
setup_s07() { mkdir -- scripts/other; ln -s /etc scripts/other/link; }
setup_s08() { rm -- scripts/agents/lib/common.mjs; ln -s ../../../web/app.ts scripts/agents/lib/common.mjs; }
setup_s09() { printf 'x\n' > scripts/foo.sh; }
check_s09() { must test -f scripts/foo.sh; }
setup_s10() { mkdir -p -- scripts/agents; printf 'x\n' > scripts/agents/x.mjs; }
setup_s11() { :; }
check_s11() { must test ! -e scripts; }
# R2-N5 / S12, known benign: a PR `scripts` that is a regular file survives when base has none.
setup_s12() { printf 'x\n' > scripts; }
check_s12() { must test -f scripts; }
setup_s13() { mkdir -- scripts; ln -s ../web scripts/agents; }
setup_s14() { mkdir -- scripts; ln -s /etc scripts/link; }
FAIL_RUNS=" s07 s14 "

one() {
  local rc=0
  "setup_$1"
  RUNNER_TEMP=$RT bash --noprofile --norc -euo pipefail "$T/s.sh" > "$RT.log" 2>&1 || rc=$?
  if [[ $FAIL_RUNS == *" $1 "* ]]; then
    must test "$rc" != 0 || { echo "scripts step exited 0; expected it to fail closed"; return 1; }
    return 0
  fi
  must test "$rc" = 0 || { tail -n 5 -- "$RT.log"; return 1; }
  if [ -d .review-base/scripts/agents ]; then
    must diff -r --no-dereference -- .review-base/scripts/agents scripts/agents || return 1
    must no_links scripts || return 1
  else
    must test ! -e scripts/agents || return 1
    must test ! -L scripts/agents || return 1
  fi
  if declare -F "check_$1" > /dev/null; then "check_$1"; fi
}

T=$(mktemp -d)
T=$(cd -- "$T" && pwd -P)
trap 'rm -rf -- "$T"' EXIT
blocks "$1" "$T"
mkdir -- "$T/tpl1" "$T/tpl0"
template "$T/tpl1" 1 > /dev/null
template "$T/tpl0" 0 > /dev/null
pass=0 total=0 failed=0
for n in $(seq -w 1 14); do
  id="s$n"
  if [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $id "* ]]; then continue; fi
  total=$((total + 1))
  ws="$T/$id" RT="$T/$id.rt"
  if [ "$n" -le 9 ]; then cp -a -- "$T/tpl1" "$ws"; else cp -a -- "$T/tpl0" "$ws"; fi
  mkdir -- "$RT"
  if out=$(cd -- "$ws" && one "$id" 2>&1); then pass=$((pass + 1)); else failed=1; printf 'FAIL %s: %s\n' "$id" "$out"; fi
done
echo "matrix-scripts: $pass/$total"
exit "$failed"
