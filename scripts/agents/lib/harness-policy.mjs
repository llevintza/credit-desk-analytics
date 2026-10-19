/**
 * The absolute floors and caps main's checker applies when `.claude/settings.json` or
 * `.cursor/cli.json` is first introduced (docs/agents/drift-checks.md "First introduction";
 * adapters.md "Permissions mapping"). Later PRs are compared against base instead, and may
 * only add denies or remove allows. These lists are the rev 10 sets, verbatim, plus the rev-10
 * amendment (#511).
 */

const READ_EDIT = ["**/.env", "**/.env.*", "**/secrets/**"];

/** The two `!` carve-outs Claude may carry; they are not denies (ruling 2). */
export const CLAUDE_CARVE_OUTS = Object.freeze(["Read(!**/.env.example)", "Edit(!**/.env.example)"]);

const PUSH = [
  "git push * main", "git push * main *", "git push *:main", "git push *:main *", "git push *refs/heads/main",
  "git push *refs/heads/main *", "git push * +main", "git push * +main *", "git push *--force", "git push *--force *",
  "git push * -f*", "git push -f*", "git push *--no-verify*",
];
const MERGE = [
  "gh pr merge", "gh pr merge *", "gh api *merge*", "gh api *git/refs*", "gh -R *pr merge*", "gh --repo *pr merge*",
  "gh pr -R* merge*", "gh pr --repo* merge*",
];
const LEASE = "git push *--force-*";
const ADMIN = [
  "gh repo edit *", "gh repo delete *", "gh repo rename *", "gh repo archive *", "gh repo *deploy-key*", "gh api *rulesets*",
  "gh api *protection*", "gh api *default_branch*", "gh api *environments*", "gh api *secrets*", "gh api *variables*",
  "gh api *dispatches*", "gh api *rename*", "gh api *keys*", "gh api *hooks*", "gh api *collaborators*", "gh api *actions/*",
  "gh api graphql *mutation*", "gh secret *", "gh variable *", "gh workflow *run *", "gh workflow *enable *",
  "gh workflow *disable *", "gh run *rerun*", "gh run *cancel*", "gh run *delete*",
];
const FLAG_BEFORE_VERB = ["-*", "pr -*", "issue -*", "repo -*", "release -*", "label -*", "secret -*", "variable -*", "workflow -*", "run -*", "ruleset -*", "cache -*", "alias -*", "api -*"].map((x) => `gh ${x}`);

// Rev-10 amendment (#511; ruled by Helms on Leo's behalf, 10-17): `+<refspec>` force-pushes to
// any ref, with and without a remote (only `+main` was covered), and gh aliases and extensions
// (an alias can rename a denied verb; `gh ext`/`gh extensions` are gh's own spellings).
const AMENDMENT = [
  "git push * +*", "git push +*", "gh alias set*", "gh alias import*", "gh extension install*", "gh extension exec*", "gh extension -*",
  "gh extensions *", "gh ext *",
];

/** H11: Claude's exact bare-push deny. It has no Cursor rendering and is D10-exempt. */
export const BARE_PUSH = "Bash(git push)";

/** `Bash(cmd args)` -> `Shell(cmd:args)`, the colon form (U9). */
const colon = (c) => c.replace(/^(\S+) /, "$1:");

/** MIN_DENY = 80: 6 `**\/` Read/Edit + 22 push/merge (incl. the lease) + 28 admin + 14 flag-before-verb + 9 amendment + the bare push. */
export const MIN_DENY = Object.freeze([
  ...READ_EDIT.map((g) => `Read(${g})`),
  ...READ_EDIT.map((g) => `Edit(${g})`),
  ...[...PUSH, ...MERGE, LEASE].map((c) => `Bash(${c})`),
  ...ADMIN.map((c) => `Bash(${c})`),
  "Bash(*curl*)",
  "Bash(*wget*)",
  ...FLAG_BEFORE_VERB.map((c) => `Bash(${c})`),
  ...AMENDMENT.map((c) => `Bash(${c})`),
  BARE_PUSH,
]);

/** MIN_DENY_CURSOR = 79: the same, colon form, `Edit`→`Write`, curl/wget as whole commands, no bare push. */
export const MIN_DENY_CURSOR = Object.freeze([
  ...READ_EDIT.map((g) => `Read(${g})`),
  ...READ_EDIT.map((g) => `Write(${g})`),
  ...[...PUSH, ...MERGE, LEASE].map((c) => `Shell(${colon(c)})`),
  ...ADMIN.map((c) => `Shell(${colon(c)})`),
  "Shell(curl)",
  "Shell(wget)",
  ...FLAG_BEFORE_VERB.map((c) => `Shell(${colon(c)})`),
  ...AMENDMENT.map((c) => `Shell(${colon(c)})`),
]);

/** H7/H11: a broad push deny in Cursor would block every cloud-agent fix-PR push. */
export const CURSOR_FORBIDDEN_DENY = Object.freeze(["Shell(git:push)", "Shell(git:push*)"]);

/** MAX_ALLOW (rev 10: six; the health-script entries are dropped). */
export const MAX_ALLOW = Object.freeze(["Bash(dotnet build *)", "Bash(dotnet test *)", "Bash(npm run lint)", "Bash(npm test *)", "Bash(git status)", "Bash(git diff *)"]);
export const MAX_ALLOW_CURSOR = Object.freeze(MAX_ALLOW.map((e) => e.replace(/^Bash\((.*)\)$/, (_, c) => `Shell(${colon(c)})`)));

/** The steering-path asks (Cursor has no ask list). */
export const ASK = Object.freeze(["**/AGENTS.md", "**/CLAUDE.md", ".claude/**", ".cursor/**", ".github/**", "docs/agents/**", "scripts/agents/**"].map((p) => `Edit(./${p})`));

/** D18: the only hook commands, and settings keys that could run or leak anything. */
export const HOOK_ALLOWLIST = Object.freeze(["guard", "format", "quick-test"].map((h) => `node scripts/agents/hooks/${h}.mjs`));
export const SETTINGS_FORBIDDEN_KEYS = Object.freeze(["env", "apiKeyHelper", "awsAuthRefresh", "otelHeadersHelper", "statusLine"]);
