# Shared helpers for the restore tests (docs/agents/testing.md, review-restore-matrix).
# Rebuilt for #171 B7: CR's matrix.sh / mutations.sh / matrix-scripts.sh are not in the repo,
# so these re-create the documented counts (41 cases, 18 faults, 14 runs) with our own fixtures.
# Sourced, never run. GNU coreutils/findutils/diffutils (the CI runner); not macOS BSD tools.
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null LC_ALL=C
SEC_A="Replace PR-controlled agent config with the base copies"
SEC_S="Restore scripts/agents from base"
GIT_ID=(-c user.name=restore-test -c user.email=restore-test@example.invalid -c commit.gpgsign=false)

# extract <workflow> <step name>: that step's `run: |` body between the restore markers, de-indented.
extract() {
  awk -v want="      - name: $2" '
    /^ *# agents-drift:restore-step:begin$/ { on = 1; next }
    /^ *# agents-drift:restore-step:end$/ { on = 0 }
    !on { next }
    $0 == want { step = 1; next }
    /^      - name: / { step = 0; body = 0 }
    step && /^        run: \|$/ { body = 1; next }
    body && /^          / { print substr($0, 11); next }
    body && /^$/ { print ""; next }
    body { body = 0 }
  ' "$1"
}

# blocks <workflow> <dir>: write $dir/a.sh and $dir/s.sh (or take BLOCK_A / BLOCK_S overrides).
blocks() {
  if [ -n "${BLOCK_A:-}" ]; then cp -- "$BLOCK_A" "$2/a.sh"; else extract "$1" "$SEC_A" > "$2/a.sh"; fi
  if [ -n "${BLOCK_S:-}" ]; then cp -- "$BLOCK_S" "$2/s.sh"; else extract "$1" "$SEC_S" > "$2/s.sh"; fi
  grep -q 'review-base' "$2/a.sh" || { echo "no §A block found between the restore markers in $1" >&2; return 1; }
  grep -q 'scripts/agents' "$2/s.sh" || { echo "no scripts step found between the restore markers in $1" >&2; return 1; }
}

# template <dir> <with base scripts/agents: 0|1>: a trusted .review-base commit plus the PR tree
# (the test merge, here identical to base). Prints the base sha.
template() {
  local b=$1/.review-base
  mkdir -p -- "$b/src/Desk.Api" "$b/.claude/skills/pr-ready" "$b/docs/agents" "$b/web"
  printf 'root rules\n' > "$b/AGENTS.md"
  printf 'spec\n' > "$b/README.md"
  printf '@AGENTS.md\n' > "$b/CLAUDE.md"
  printf 'api rules\n' > "$b/src/Desk.Api/AGENTS.md"
  printf '@AGENTS.md\n' > "$b/src/Desk.Api/CLAUDE.md"
  printf '{}\n' > "$b/.claude/settings.json"
  printf 'skill\n' > "$b/.claude/skills/pr-ready/SKILL.md"
  printf 'index\n' > "$b/docs/agents/README.md"
  printf 'workflow\n' > "$b/docs/agents/workflow.md"
  printf '{"mcpServers":{}}\n' > "$b/.mcp.json"
  printf 'app\n' > "$b/web/app.ts"
  if [ "$2" = 1 ]; then
    mkdir -p -- "$b/scripts/agents/lib"
    printf 'drift\n' > "$b/scripts/agents/check-drift.mjs"
    printf 'lib\n' > "$b/scripts/agents/lib/common.mjs"
  fi
  git -C "$b" init -q
  git -C "$b" add -A
  git "${GIT_ID[@]}" -C "$b" commit -q -m base
  git -C "$b" archive HEAD | tar -x -C "$1"
  git -C "$b" rev-parse HEAD
}

# must <cmd…>: run an assertion and name it on failure.
must() { "$@" || { echo "assertion failed: $*"; return 1; }; }
no_links() { test -z "$(find "$@" -type l -print -quit)"; }
