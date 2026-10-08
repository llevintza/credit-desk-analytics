---
name: fix-review-feedback
description: "Address Code Reviewer findings on your PR: one fix per finding on the same branch, re-run pr-ready, push, draft replies. Never resolve a thread you did not fix; never post verdicts."
---
# Fix review feedback

1. List the findings at the verdict sha (`[Code Reviewer bot] @<sha>`).
2. One commit per finding on the same branch. Bring `main` in by merge; never force-push.
3. Re-run `pr-ready` and push.
4. Put replies in a body file, posted only if Tech Coordinator asks.
5. Never resolve threads you have not fixed. Never post in another role's name (root rule). Never close issues.
6. Point Code Reviewer to the new head sha. A push after a verdict needs a new verdict at the new sha.
