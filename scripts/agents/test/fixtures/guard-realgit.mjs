// Real-git guard fixtures (testing.md "Guard build-work requirements"). REBUILT: CR5's 118
// and CR6's 94 probe rows are not in the repo, so these rows are rebuilt from the examples and
// classes in testing.md and adapters.md (Leo, 2026-10-17). IDs are kept where the docs name a
// row; the rest are labelled by requirement. Each row: the repo it runs in (`work` is on
// `feat` with a local `main` ahead of the remote; `onmain` is on `main` with a new commit),
// optional setup, the command, the guard's exit code, and whether remote `main` moves when
// the command runs unguarded (null: no sandbox ground truth, the exit code only).
const row = (id, cmd, exit, moves, extra = {}) => ({ id, cmd, exit, moves, repo: "work", ...extra });
const onmain = { repo: "onmain" };

export default [
  // Allowed: the feature-branch flows (testing.md 0-exit row; rev 7 benign).
  row("A01", "git push -u origin feat", 0, false),
  row("A02", "git push origin feat", 0, false),
  row("A03", "git push", 0, false),
  row("A04", "git push origin HEAD", 0, false),
  row("A05", "git push -u origin HEAD", 0, false),
  row("A06", "git push origin HEAD:cursor/fix-1", 0, false),
  row("A07", "git push origin HEAD:refs/heads/cursor/fix-2", 0, false),
  row("A08", "git checkout -q -b x && git push -u origin x", 0, false),
  row("A09", "true && git push -u origin feat", 0, false),
  row("A10", "git pull -q --rebase && git push origin feat", 0, false),
  row("A11", "git push --set-up origin feat", 0, false),
  row("A12", "git push origin v1", 0, false, { setup: "git tag v1" }),
  row("A13", "git push --delete origin old", 0, false, { setup: "git push -q origin feat:old" }),
  row("A14", "git push origin feat:feat2", 0, false),
  row("N63", "git rebase -q origin/main && git push origin feat", 0, false),
  row("A15", "git push --dry-run origin feat", 0, false),
  row("A16", "echo 'git push origin main'", 0, false),
  row("A17", "git push origin 'feat'", 0, false),
  row("A18", "git switch -q main && git switch -q feat && git push origin feat", 0, false),
  // G9: every force form, lease included, to any branch (L04, L05, N10, N64 by policy).
  row("N10", "git push --force-with-lease origin feat", 2, false),
  row("N64", "git rebase -q origin/main && git push --force-with-lease origin feat", 2, false),
  row("L04", "git push -f origin feat", 2, false),
  row("L05", "git push origin +feat", 2, false),
  row("G9a", "git push -uf origin feat", 2, false),
  row("G9b", "git push origin feat --force", 2, false),
  row("G9c", "git push --force-if-includes origin feat", 2, false),
  row("G9d", "git push --no-verify origin feat", 2, false),
  // Destinations that are main.
  row("D01", "git push origin main", 2, true),
  row("D02", "git push origin HEAD:main", 2, true),
  row("D03", "git push origin feat:main", 2, true),
  row("D04", "git push origin refs/heads/main", 2, true),
  row("D05", "git push origin heads/main", 2, true),
  row("D06", "git push origin 'main'", 2, true),
  row("D07", 'git push origin "HEAD:main"', 2, true),
  row("D08", "git push origin HEAD:refs/heads/main", 2, true),
  row("D09", "git push origin x:main", 2, false),
  row("D10", "git push origin :main", 2, false, { setup: "git push -q origin feat:keep" }),
  row("D11", "git push --delete origin main", 2, false),
  row("D12", "git push origin main --dry-run", 2, false),
  row("D13", "git push origin MAIN", 2, null),
  row("D14", "git push origin 'ma'\"in\"", 2, true),
  row("D15", "git push origin $'\\x6d'ain", 2, true),
  // On main: HEAD, @ and no-refspec pushes resolve to main.
  row("H01", "git push", 2, true, onmain),
  row("H02", "git push origin HEAD", 2, true, onmain),
  row("H03", "git push -u origin HEAD", 2, true, onmain),
  row("H04", "git push origin @", 2, true, onmain),
  row("H05", "git push origin", 2, true, onmain),
  row("H06", "git push origin HEAD", 2, false, { setup: "git checkout -q --detach" }),
  // G12: abbreviations, and pushes of every branch.
  row("N56", "git push --mirro origin", 2, true),
  row("N57", "git push --al origin", 2, true),
  row("N58", "git push --branc origin", 2, true),
  row("N59", "git push --no-verif origin feat", 2, false),
  row("N69", "git push --d origin feat", 2, false),
  row("G12a", "git push --all origin", 2, true),
  row("G12b", "git push --mirror origin", 2, true),
  row("G12c", "git push --repo=origin", 2, null),
  // Dynamic, glob and wildcard refspecs.
  row("G4a", "git push origin $(echo main)", 2, true),
  row("G4b", "B=main; git push origin $B", 2, true),
  row("G4c", "git push origin `echo main`", 2, true),
  row("G4d", "git push origin 'refs/heads/*:refs/heads/*'", 2, true),
  row("G4e", 'git push origin "+refs/heads/feat:refs/heads/main"', 2, true),
  // G2: state changed earlier in the payload.
  row("S08", "git checkout -q main && git push origin HEAD", 2, true),
  row("S09", "git checkout -q main && git push", 2, true),
  row("S10", "git switch -q main && git push", 2, true),
  row("B18", "git checkout -q -b cursor/fix-y && git push origin HEAD", 2, false),
  row("B19", "cd .. && git push", 2, false),
  row("S11", "{ cd ..; git push origin HEAD; }", 2, false),
  row("Q22", "git rebase -q --root main && git push origin HEAD", 2, true),
  row("S12", "git config remote.origin.push refs/heads/feat:refs/heads/main && git push origin feat", 2, true),
  row("S13", "export HOME=/nonexistent && git push origin feat", 2, false),
  row("S14", "git co main && git push origin HEAD", 2, true, { setup: "git config alias.co checkout" }),
  // G3: git environment and -c.
  row("G02", "GIT_DIR=.git git push origin feat", 2, false),
  row("G04", "git -c remote.origin.push=refs/heads/feat:refs/heads/main push origin feat", 2, true),
  row("G05", "git --git-dir=.git push origin main", 2, true),
  row("G06", "HOME=/nonexistent git push origin feat", 2, false),
  row("G09", "git -C . push origin main", 2, true),
  row("G10", "git push origin", 2, true, { setup: "git config remote.origin.mirror true" }),
  // G11: destinations mapped through config, symrefs, push.default.
  row("N51", "git push -u origin feat", 2, true, { setup: "git config remote.origin.push refs/heads/feat:refs/heads/main" }),
  row("N52", "git push origin alias", 2, true, { setup: "git symbolic-ref refs/heads/alias refs/heads/main" }),
  row("N53", "git push origin", 2, true, { setup: "git config push.default matching" }),
  row("N54", "git push", 2, true, { setup: "git config push.default upstream && git config branch.feat.merge refs/heads/main" }),
  row("N55", "git push origin feat", 2, true, { setup: "git config push.default upstream && git config branch.feat.merge refs/heads/main" }),
  // Aliases (G3 -c alias, configured aliases, `!` aliases).
  row("AL1", "git -c alias.p='push origin main' p", 2, true),
  row("AL2", "git p", 2, true, { setup: "git config alias.p 'push origin main'" }),
  row("AL3", "git sp", 2, true, { setup: "git config alias.sp '!git push origin main'" }),
  row("AL4", "git sp", 0, false, { setup: "git config alias.sp '!git push origin feat'" }),
  // G17: config and hook files.
  row("Q04", "cat .git/config && git push origin feat", 2, false),
  row("Q05", "echo 'git push origin main' > .git/hooks/pre-commit", 2, false),
  row("Q06", "git config core.hooksPath hooks", 2, false),
  // Other push programs and unwrapped forms (later PRs analyse the wrappers; they fail closed).
  row("N32", "hub push origin main", 2, null),
  row("SP1", "git send-pack ../remote.git main", 2, null),
  row("E15", "echo main | xargs git push origin", 2, true),
  row("W01", "env git push origin main", 2, true),
  row("W02", "bash -c 'git push origin main'", 2, true),
];
