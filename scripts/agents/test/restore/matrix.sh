#!/usr/bin/env bash
# Restore matrix (rebuilt, #171 B7): 41 PR-tree cases against the §A block from claude-review.yml,
# in three modes: A = §A alone; AS0 / AS1 = §A + the scripts step, without / with base scripts/agents.
# Usage: matrix.sh <claude-review.yml>. Env: MODES="A AS0 AS1", ONLY="06 23" (case filter),
# BLOCK_A / BLOCK_S (run these files instead of the extracted blocks; mutations.sh).
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
# shellcheck source=common.sh
. "$here/common.sh"

# Expected memory/config-shaped paths after a restore (the fixture's base set; .claude and docs/agents are diffed).
EXPECT=$'./.mcp.json\n./AGENTS.md\n./CLAUDE.md\n./src/Desk.Api/AGENTS.md\n./src/Desk.Api/CLAUDE.md'
stash() { must test -f ".review-pr/$1.pr.txt"; }
gone() { must test ! -e "$1" && must test ! -L "$1"; }

# --- the 41 cases: setup_NN mutates the PR tree; check_NN adds case-specific assertions ---
setup_01() { :; }
setup_02() { printf 'pr rules\n' > AGENTS.md; }
check_02() { stash AGENTS.md && must grep -qx 'pr rules' .review-pr/AGENTS.md.pr.txt; }
setup_03() { printf 'pr spec\n' > README.md; }
check_03() { stash README.md && must grep -qx 'pr spec' .review-pr/README.md.pr.txt; }
setup_04() { printf '@AGENTS.md\n@evil.md\n' > CLAUDE.md; }
check_04() { stash CLAUDE.md; }
setup_05() { printf 'pr api\n' > src/Desk.Api/AGENTS.md; }
check_05() { stash 'src%2FDesk.Api%2FAGENTS.md'; }
setup_06() { printf 'x\n' > web/AGENTS.md; }
check_06() { gone web/AGENTS.md && stash 'web%2FAGENTS.md'; }
setup_07() { printf 'x\n' > web/CLAUDE.md; }
check_07() { gone web/CLAUDE.md; }
setup_08() { printf 'x\n' > web/claude.md; }
check_08() { gone web/claude.md; }
setup_09() { printf 'x\n' > web/AGENT.md; }
check_09() { gone web/AGENT.md; }
setup_10() { printf 'x\n' > CLAUDE.local.md; }
check_10() { gone CLAUDE.local.md; }
setup_11() { printf 'x\n' > Agents.Md; }
check_11() { gone Agents.Md; }
setup_12() { rm -- src/Desk.Api/AGENTS.md; }
setup_13() { rm -- AGENTS.md; }
setup_14() { rm -- AGENTS.md; mkdir -- AGENTS.md; printf 'x\n' > AGENTS.md/x; }
setup_15() { rm -- src/Desk.Api/CLAUDE.md; mkdir -- src/Desk.Api/CLAUDE.md; printf 'x\n' > src/Desk.Api/CLAUDE.md/x; }
setup_16() { rm -- AGENTS.md; ln -s README.md AGENTS.md; }
setup_17() { ln -s /etc src/link; }
check_17() { gone src/link; }
setup_18() { ln -s ../.review-base web/rb; }
check_18() { gone web/rb; }
setup_19() { printf '{"hooks":{"PreToolUse":[]}}\n' > .claude/settings.json; }
check_19() { stash '.claude%2Fsettings.json'; }
setup_20() { mkdir -p -- .claude/agents; printf 'x\n' > .claude/agents/evil.md; }
check_20() { gone .claude/agents; }
setup_21() { mkdir -p -- web/.claude; printf '{}\n' > web/.claude/settings.json; }
check_21() { gone web/.claude && stash 'web%2F.claude%2Fsettings.json'; }
setup_22() { rm -rf -- .claude; printf 'x\n' > .claude; }
setup_23() { mkdir -- .cursor; printf '{}\n' > .cursor/hooks.json; }
check_23() { gone .cursor && stash '.cursor%2Fhooks.json'; }
setup_24() { printf 'x\n' > .cursorrules; }
check_24() { gone .cursorrules; }
setup_25() { printf 'x\n' > .cursorignore; printf 'x\n' > web/.CursorIgnore; }
check_25() { gone .cursorignore && gone web/.CursorIgnore; }
setup_26() { mkdir -- .grok; printf '{}\n' > .grok/settings.json; }
check_26() { gone .grok; }
setup_27() { printf 'x\n' > .claudeignore; }
check_27() { gone .claudeignore; }
setup_28() { printf '{"mcpServers":{"evil":{}}}\n' > .mcp.json; }
check_28() { stash .mcp.json; }
setup_29() { printf '{}\n' > web/.mcp.json; }
check_29() { gone web/.mcp.json; }
setup_30() { printf 'pr workflow\n' > docs/agents/workflow.md; }
check_30() { stash 'docs%2Fagents%2Fworkflow.md'; }
setup_31() { printf 'x\n' > docs/agents/new.md; }
check_31() { gone docs/agents/new.md; }
setup_32() { rm -rf -- docs/agents; ln -s ../web docs/agents; }
setup_33() { mkdir -p -- .review-pr/x; printf 'x\n' > .review-pr/x/AGENTS.md; }
setup_34() { mkdir -- .review-context; printf '[]\n' > .review-context/inline-comments.json; }
setup_35() { mkdir -p -- 'a%2Fb' a/b; printf '1\n' > 'a%2Fb/AGENTS.md'; printf '2\n' > a/b/AGENTS.md; }
check_35() { stash 'a%252Fb%2FAGENTS.md' && stash 'a%2Fb%2FAGENTS.md' && must grep -qx 1 '.review-pr/a%252Fb%2FAGENTS.md.pr.txt'; }
setup_36() { mkdir -- $'we\nird'; printf 'x\n' > $'we\nird/AGENTS.md'; }
check_36() { gone $'we\nird/AGENTS.md'; }
setup_37() { mkdir -- 'my dir'; printf 'x\n' > 'my dir/CLAUDE.md'; }
check_37() { gone 'my dir/CLAUDE.md' && stash 'my dir%2FCLAUDE.md'; }
setup_38() { printf 'x\n' > docs/README.md; }
check_38() { must test -f docs/README.md && stash 'docs%2FREADME.md'; }
setup_39() { printf 'x\n' > .claude/skills/pr-ready/AGENTS.md; }
# Fail closed: a broken trust anchor stops the job.
setup_40() { CASE_SHA=0000000000000000000000000000000000000000; }
setup_41() {
  git -C .review-base rm -q -- AGENTS.md
  git "${GIT_ID[@]}" -C .review-base commit -q -m 'no AGENTS.md'
  CASE_SHA=$(git -C .review-base rev-parse HEAD)
}
FAIL_CASES=" 40 41 "

# Independent oracle for every passing case (diff -r and a broader name match than §A's own check).
generic() {
  local f found
  must no_links . \( -path ./.git -o -path ./.review-base \) -prune -o || return 1
  must test -z "$(git -C .review-base status --porcelain)" || return 1
  must test "$(git -C .review-base rev-parse HEAD)" = "$CASE_SHA" || return 1
  while IFS= read -r -d '' f; do
    must test ! -L "$f" || return 1
    must cmp -s -- ".review-base/$f" "$f" || return 1
  done < <(git -C .review-base ls-files -z -- README.md .mcp.json '*AGENTS.md' '*CLAUDE.md')
  must diff -r --no-dereference -- .review-base/.claude .claude || return 1
  must diff -r --no-dereference -- .review-base/docs/agents docs/agents || return 1
  found=$(find . \( -path ./.git -o -path ./.review-base -o -path ./.review-pr -o -path ./.claude -o -path ./docs/agents \) -prune -o \
    \( -iname 'agent*.md' -o -iname 'claude*.md' -o -iname '.cursor*' -o -iname .grok -o -iname .claude -o -iname .claudeignore -o -iname .mcp.json \) -print | sort)
  must test "$found" = "$EXPECT" || { printf 'memory/config paths:\n%s\n' "$found"; return 1; }
  must test -d .review-pr || return 1
  must test ! -L .review-pr || return 1
  must test -z "$(find .review-pr -mindepth 1 \( ! -type f -o ! -name '*.pr.txt' \) -print -quit)" || return 1
  must test ! -e .review-context || return 1
  case $MODE in
    AS1) must diff -r --no-dereference -- .review-base/scripts/agents scripts/agents ;;
    AS0) must test ! -e scripts/agents ;;
  esac
}

# one <id>: run one case in the current directory (a fresh copy of the template); 0 when it behaves.
one() {
  local rc=0
  CASE_SHA=$SHA
  "setup_$1"
  mkdir -- "$RT"
  BASE_SHA=$CASE_SHA RUNNER_TEMP=$RT bash --noprofile --norc -euo pipefail "$T/a.sh" > "$RT.log" 2>&1 || rc=$?
  if [ "$rc" = 0 ] && [ "$MODE" != A ]; then
    BASE_SHA=$CASE_SHA RUNNER_TEMP=$RT bash --noprofile --norc -euo pipefail "$T/s.sh" >> "$RT.log" 2>&1 || rc=$?
  fi
  if [[ $FAIL_CASES == *" $1 "* ]]; then
    must test "$rc" != 0 || { echo "restore exited 0; expected it to fail closed"; return 1; }
    return 0
  fi
  must test "$rc" = 0 || { tail -n 5 -- "$RT.log"; return 1; }
  generic || return 1
  if declare -F "check_$1" > /dev/null; then "check_$1"; fi
}

T=$(mktemp -d)
T=$(cd -- "$T" && pwd -P)
trap 'rm -rf -- "$T"' EXIT
blocks "$1" "$T"
failed=0
for MODE in ${MODES:-A AS0 AS1}; do
  mkdir -- "$T/tpl-$MODE"
  SHA=$(template "$T/tpl-$MODE" "$([ "$MODE" = AS1 ] && echo 1 || echo 0)")
  pass=0 total=0
  for n in $(seq -w 1 41); do
    if [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $n "* ]]; then continue; fi
    total=$((total + 1))
    ws="$T/$MODE-$n" RT="$T/$MODE-$n.rt"
    cp -a -- "$T/tpl-$MODE" "$ws"
    if out=$(cd -- "$ws" && one "$n" 2>&1); then pass=$((pass + 1)); else failed=1; printf 'FAIL %s %s: %s\n' "$MODE" "$n" "$out"; fi
  done
  echo "matrix $MODE: $pass/$total"
done
exit "$failed"
