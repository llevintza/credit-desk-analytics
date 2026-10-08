---
name: adr
description: Write an ADR: copy docs/adr/0000-template.md, take the next free number (check open PRs), add the index row, include measured numbers and the perf/ script that produced them.
paths:
  - "docs/adr/**"
---
# Write an ADR

1. Copy `docs/adr/0000-template.md` to `docs/adr/NNNN-kebab-title.md`.
2. Take the next free number. Check open PRs so you do not collide.
3. Add the index row in `docs/adr/README.md`.
4. Include measured numbers where performance is affected, and the `perf/` script that produced them (root item 4).
5. Do not put secrets, real names or connection strings in the ADR.
