// gh (G14), gh api (G15) and MCP (G16) rows of the exit-code table (testing.md). REBUILT where
// CR7/CR8 rows are not in the repo: the commands are the ones the docs list, by class.
import { test } from "node:test";
import assert from "node:assert/strict";
import { decide } from "../hooks/lib/decide.mjs";
import { apiArgs } from "../hooks/lib/gh.mjs";
import { parse } from "../hooks/lib/shell.mjs";
import { fakeRepo } from "./helpers.mjs";

const SHAPES = [
  (command) => ({ tool_name: "Bash", tool_input: { command } }),
  (command) => ({ tool_name: "Shell", tool_input: { command } }),
  (command) => ({ hook_event_name: "beforeShellExecution", command, cwd: "/w" }),
];
const rows = (list, want) => {
  for (const c of list) for (const shape of SHAPES) assert.equal(decide(JSON.stringify(shape(c)), { repoAt: fakeRepo() }).code, want, c);
};
const mcp = (tool, args = {}) => decide(JSON.stringify({ tool_name: tool, tool_input: args })).code;

test("TC's check commands and the PR/issue flows stay allowed (governance.md)", () => {
  rows([
    "gh ruleset list", "gh ruleset list --repo o/r", "gh ruleset view 4242", "gh ruleset view 4242 --repo o/r", "gh repo view --json defaultBranchRef",
    "gh repo view o/r --json defaultBranchRef -q .defaultBranchRef.name", "gh api repos/o/r/rulesets -X GET -f includes_parents=true",
    "gh api repos/o/r/rulesets/1 --method=GET", 'gh api "repos/o/r/pulls?state=open&per_page=100"', "gh repo deploy-key list", "gh run list", "gh secret list",
    "gh api repos/o/r/git/refs/heads/main", "gh api repos/o/r/pulls/9", "gh api repos/o/r/pulls/9 --paginate --jq .title -H 'Accept: application/json'",
    'gh pr create --title t --body "use git push -u origin feat"', "gh pr create --body-file b.md", "gh pr edit 5 --add-label x", "gh pr comment 5 --body-file c.md",
    "gh pr review 5 --comment -b ok", "gh pr review 5 -c --body-file r.md -R o/r", "gh pr review --comment --body=x", "gh pr reopen 5", "gh issue reopen 3",
    "gh api repos/o/r/issues/3 -X PATCH -f state=open", "gh api repos/o/r/pulls/5/reviews -f event=COMMENT -f body=x", "gh pr ready 5", "gh pr checks 5 --watch", "gh pr update-branch 5", "gh pr checkout 5", "gh pr view 96 -R o/r",
    "gh issue create --title t --body-file b.md", "gh issue comment 3 --body x", "gh api repos/o/r/pulls/12/update-branch -X PUT",
    "gh api repos/{owner}/{repo}/issues/3/comments -f body=x", "gh api repos/o/r/pulls/comments/7/reactions -f content=+1",
    "gh api repos/o/r/pulls/5/comments/7/replies -f body=x", "gh api repos/o/r/pulls/5/reviews/2/events -X POST -f event=COMMENT",
    "gh api /repos/o/r/issues -f title=t", "gh --version", "gh --help", "gh status", "gh version", "gh help pr", "gh completion -s zsh",
    "gh auth status", "gh variable get X", "gh config get editor", "gh search prs is:open", "gh release download v1",
    `gh api graphql -f query='query { viewer { login } }'`, `gh api graphql -f 'query={ viewer { login } }' -F owner=o`, 'gh api "$EP"',
  ], 0);
});

test("H16 canonical-form over-blocks and the gh allowlist (G14)", () => {
  rows([
    "gh -R o/r ruleset view 4242", "gh rs list", "gh rs view 4242", "gh -R o/r pr view 96", "gh pr --help", "gh api -X GET repos/o/r", "gh api -XGET repos/o/r",
    "gh api --method=GET repos/o/r", "gh api --paginate -H 'Accept: x' repos/o/r",
    // V01–V10: a flag before the verb.
    "gh secret --app actions set X", "gh workflow --ref main run ci.yml", "gh run --job 1 rerun", "gh repo --default-branch dev edit", "gh pr --subject s merge 5",
    "gh secret -R o/r set X", "gh variable --repo o/r delete X", "gh workflow -R o/r run ci", "gh run --repo o/r rerun 1", "gh secret --app actions list",
    // Unlisted verbs and groups.
    "gh pr merge 5", "gh pr lock 5", "gh pr revert 5", "gh repo edit --default-branch x", "gh repo delete o/r", "gh repo rename x", "gh repo archive",
    "gh repo sync", "gh repo set-default o/r", "gh repo fork", "gh repo create x", "gh repo deploy-key add k", "gh repo deploy-key delete 1", "gh secret set X",
    "gh variable delete X", "gh workflow run ci", "gh workflow enable ci", "gh workflow disable ci", "gh run rerun 1", "gh run cancel 1", "gh run delete 1",
    "gh label create x", "gh release create v1", "gh gist create f", "gh auth token", "gh auth login", "gh cache delete 1", "gh browse", "gh alias set co 'pr checkout'",
    "gh pr update-branch 5 --rebase", "gh co 5",
    // #520 G14 tightening: agents never close issues or PRs or post a review verdict.
    "gh pr close 5", "gh issue close 3", "gh pr review 5 --approve", "gh pr review 5 -a", "gh pr review 5 --request-changes -b x", "gh pr review 5 -r -b x",
    "gh pr review 5 -c -a", "gh pr review 5 --comment --approve=true", "gh pr review 5", "gh pr review 5 -b x", "gh pr review 5 -c $F",
    "gh my-extension", "gh extension install o/gh-x", "gh extension exec x", "gh", "gh $G list", "gh pr $V", "gh pr", "gh --version x",
  ], 2);
});

test("gh api writes: methods, fields, paths and GraphQL (G15)", () => {
  rows([
    "gh api -X PUT repos/o/r/pulls/5/merge", "gh api repos/o/r/pulls/5/merge -X PUT", "gh api repos/o/r/git/refs/heads/main -X PATCH -f sha=x",
    "gh api repos/o/r/git/refs/x --method DELETE", "gh api repos/o/r/git/refs -f sha=x -f ref=refs/heads/main", "gh api repos/o/r/contents/a.txt -X PUT -f message=m",
    "gh api repos/o/r -X PATCH -f default_branch=x", "gh api https://api.github.com/repos/o/r -X PATCH -f default_branch=x",
    "gh api repos/o/r/rulesets/1 -X=DELETE", "gh api repos/o/r/rulesets/1 -XDELETE", "gh api repos/o/r/rulesets/1 --method=Delete", "gh api repos/o/r/rulesets -F=name=x",
    "gh api repos/o/r/dispatches -fref=main", "gh api repos/o/r/branches/main/protection -X DELETE", "gh api repos/o/r/environments/production -X PUT",
    "gh api repos/o/r/actions/secrets/X -X PUT", "gh api repos/o/r/branches/main/rename -X POST -f new_name=x", "gh api repos/o/r/keys -f key=k",
    "gh api repos/o/r/hooks -f url=u", "gh api repos/o/r/collaborators/x -X PUT", "gh api repos/o/r/actions/runs/1/cancel -X POST",
    "gh api repos/o/r/pulls/5 --input body.json", "gh api repos/o/r/pulls/5 --input=b.json -X GET", "gh api repos/o/r/rulesets/1 -X \"$M\"",
    "gh api repos/o/r/pulls/5 -X PATCH -H 'X-HTTP-Method-Override: DELETE'", "gh api repos/o/r/pulls/5 -X PATCH -H \"$H\"", "gh api repos/o/r/rulesets extra",
    "gh api repos/o/r/rulesets --unknown", "gh api 'repos/o/r/issues?x=1' -f t=x", "gh api 'repos/o/r/issues#x' -f t=x", "gh api repos/o/r/issues%2F1 -f t=x",
    "gh api repos/o/r/issues/../pulls/1/merge -f x=1", "gh api repos//o/r/issues -f t=x", "gh api \"$EP\" -f t=x", "gh api $EP",
    "gh api repos/o/r/pulls/5/merges -X PUT", "gh api repos/o/r/issues/1/lock -X PUT",
    `gh api graphql -f query='mutation { mergePullRequest(input: {}) { clientMutationId } }'`, `gh api graphql -f query='query { a } mutation { updateRefs }'`,
    "gh api graphql -F query=@q.graphql", "gh api graphql --input q.json", "gh api graphql -f owner=o", `gh api graphql -f 'query=subscription { x }'`,
    `gh api graphql -f "query=$Q"`, "gh api repos/o/r/issues/3 -X PATCH -f state=closed", "gh api repos/o/r/pulls/3 -X PATCH -F state=closed",
    "gh api repos/o/r/issues/3 -X PATCH -f state_reason=x -f state=CLOSED", "gh api repos/o/r/pulls/5/reviews -f event=APPROVE", "gh api repos/o/r/pulls/5/reviews -f event=REQUEST_CHANGES",
    "gh api repos/o/r/pulls/5/reviews/2/events -X POST -f event=approve", `gh api graphql -f query='mutation { closeIssue(input: {}) { clientMutationId } }'`, "gh api graphql -f query='{ a }' -f query='{ b }'", "gh api graphql -f query='{ a }' x",
  ], 2);
  const a = apiArgs(parse("x --cache 1h -p corsair --hostname h --silent -i --slurp -t '{{.}}' --raw-field k --field=k=v -X").segments[0].words);
  assert.deepEqual([a.method, a.fields.map((f) => f.slice(0, 2)), a.extra], ["DYNAMIC", [["k", ""], ["k", "v"]], false]);
  assert.match(apiArgs(parse("x -H").segments[0].words).block, /header/);
});

test("MCP tools (G16): blocked everywhere, the GitHub allowlist, other servers", () => {
  for (const [tool, args] of [
    ["mcp__github__merge_pull_request", { owner: "o", repo: "r", pullNumber: 1 }], ["mcp__github__update_ref", { ref: "heads/main" }],
    ["mcp__github__create_repository_ruleset", {}], ["mcp__github__push_files", { owner: "o", repo: "r", branch: "main", files: [] }],
    ["mcp__github__push_files", { owner: "o", repo: "r", files: [] }], ["mcp__github__create_branch", { owner: "o", repo: "r", branch: "MAIN" }],
    ["mcp__gh__update_repo", { default_branch: "x" }], ["mcp__x__update_repository", { visibility: "public" }], ["mcp__tools__set_thing", { nested: { archived: true } }],
    ["mcp__gh__create_label", { owner: "o", repo: "r", name: "x" }], ["mcp__notes__merge_pull_request", { repository: "o/r" }], ["mcp__x__rename_branch", {}],
    ["mcp__x__add_deploy_key", {}], ["mcp__x__add_collaborator", {}], ["mcp__x__create_webhook", {}], ["mcp__x__cancel_workflow_run", {}],
    ["mcp__x__enable_workflow", {}], ["mcp__x__set_permission", {}],
    // #517 review: write verbs behind a read prefix, and branch writes to main on any server.
    ["mcp__x__list_merge_x", {}], ["mcp__x__get_and_dispatch", {}], ["mcp__x__push_files", { branch: "main" }], ["mcp__foo__create_or_update_file", {}],
    ["mcp__fs__create_file", { path: "a", branch: "main" }], ["mcp__x__create_branch", {}], ["MCP:merge_pull_request", { owner: "o", repo: "r" }],
  ]) assert.equal(mcp(tool, args), 2, tool);
  for (const [tool, args] of [
    ["mcp__github__get_file_contents", { owner: "o", repo: "r", path: "README.md" }], ["mcp__github__list_workflow_runs", { owner: "o", repo: "r" }],
    ["mcp__github__search_code", { q: "x" }], ["mcp__github__create_pull_request", { owner: "o", repo: "r", base: "main", head: "feat" }],
    ["mcp__github__push_files", { owner: "o", repo: "r", branch: "feat", files: [] }], ["mcp__github__create_branch", { owner: "o", repo: "r", branch: "refs/heads/x" }],
    ["mcp__github__update_pull_request_base", { owner: "o", repo: "r" }], ["mcp__other__update_pull_request_branch", { repo: "r" }],
    ["mcp__github__add_issue_comment", { owner: "o", repo: "r" }], ["mcp__memory__create_entities", { entities: [] }], ["mcp__fs__write_file", { path: "a" }],
    ["mcp__fs__create_file", { path: "a" }], ["mcp__x__push_files", { branch: "feat" }], ["mcp__github__list_workflows", { owner: "o", repo: "r" }],
  ]) assert.equal(mcp(tool, args), 0, tool);
  assert.equal(decide(JSON.stringify({ toolName: "github__merge_pull_request", toolInput: { owner: "o", repo: "r" } })).code, 2); // Grok shape
});
