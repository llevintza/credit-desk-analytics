#!/usr/bin/env bash
# Mutation faults (rebuilt, #171 B7): 18 faults in the marked restore blocks (11 §A, 4 scripts step,
# 3 bootstrap SM5–SM7). Each must be caught by the case it was written against, which names the
# assertion it defeats (R3-N6: matrix + mutations are the bash block's coverage). SM0 is the control:
# the unmutated blocks pass both suites in full. Usage: mutations.sh <claude-review.yml>.
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
# shellcheck source=common.sh
. "$here/common.sh"

# id | block (A or S) | del:<exact line> or sub:<from>=><to> | catching case(s)
FAULTS=(
  'A01|A|del:test "$(git -C .review-base rev-parse HEAD)" = "$BASE_SHA"|40'
  'A02|A|del:test -f .review-base/AGENTS.md|41'
  'A03|A|del:find . \( -path ./.git -o -path ./.review-base \) -prune -o -type l -exec rm -f -- {} +|17'
  'A04|A|del:rm -rf -- .review-pr .review-context|34'
  'A05|A|sub: -o -iname AGENT.md=>|09'
  'A06|A|sub:-iname .cursor -o =>|23'
  'A07|A|sub:-iname CLAUDE.md -o=>-name CLAUDE.md -o|08'
  'A08|A|del:  cp -P -T -- ".review-base/$f" "$f"|01'
  'A09|A|del:cp -P -T -- .review-base/README.md ./README.md|01'
  'A10|A|del:rm -rf -- ./docs/agents ./README.md|31'
  "A11|A|sub: -o -ipath './docs/agents/*'=>|30"
  'S01|S|del:rm -rf -- ./scripts/agents|s03'
  'S02|S|del:if [ -d .review-base/scripts/agents ]; then cp -a -T -- .review-base/scripts/agents ./scripts/agents; fi|s01'
  'S03|S|del:if [ -e ./scripts ]; then links=$(find ./scripts -type l -print -quit); test -z "$links"; fi|s07'
  'S04|S|del:if [ -d .review-base/scripts/agents ]; then mkdir -p -- ./scripts; fi|s04'
  'SM5|S|sub:if [ -d .review-base/scripts/agents ]; then cp=>if true; then cp|s11'
  'SM6|S|sub:if [ -d .review-base/scripts/agents ]; then mkdir=>if true; then mkdir|s12'
  'SM7|S|sub:then test ! -e ./scripts/agents=>then test -e ./scripts/agents|s11'
)

T=$(mktemp -d)
trap 'rm -rf -- "$T"' EXIT
blocks "$1" "$T"

echo "SM0 control: unmutated blocks"
bash "$here/matrix.sh" "$1" > "$T/sm0.log" 2>&1 || { cat -- "$T/sm0.log"; echo "SM0: matrix.sh fails on the unmutated blocks"; exit 1; }
bash "$here/matrix-scripts.sh" "$1" > "$T/sm0s.log" 2>&1 || { cat -- "$T/sm0s.log"; echo "SM0: matrix-scripts.sh fails on the unmutated blocks"; exit 1; }

caught=0
for f in "${FAULTS[@]}"; do
  IFS='|' read -r id blk op cases <<< "$f"
  src="$T/$(tr 'AS' 'as' <<< "$blk").sh"
  text=$(cat -- "$src")
  case $op in
    del:*)
      line=${op#del:}
      [ "$(grep -cxF -- "$line" "$src")" = 1 ] || { echo "$id: stale fault, the line is not in the block exactly once: $line"; exit 1; }
      grep -vxF -- "$line" "$src" > "$T/m.sh" || true ;;
    sub:*)
      from=${op#sub:}; from=${from%%=>*}; to=${op#*=>}
      [[ $text == *"$from"* ]] || { echo "$id: stale fault, not in the block: $from"; exit 1; }
      printf '%s\n' "${text/"$from"/"$to"}" > "$T/m.sh" ;;
  esac
  if [ "$blk" = A ]; then
    out=$(BLOCK_A="$T/m.sh" MODES=A ONLY="$cases" bash "$here/matrix.sh" "$1" 2>&1) && rc=0 || rc=$?
  else
    out=$(BLOCK_S="$T/m.sh" ONLY="$cases" bash "$here/matrix-scripts.sh" "$1" 2>&1) && rc=0 || rc=$?
  fi
  if [ "$rc" != 0 ] && grep -q "^FAIL \(A \)\?$cases" <<< "$out"; then
    caught=$((caught + 1)); echo "ok $id caught by $cases"
  else
    echo "MISSED $id (case $cases)"; printf '%s\n' "$out"
  fi
done
echo "mutations: $caught/${#FAULTS[@]} faults caught"
[ "$caught" = "${#FAULTS[@]}" ]
