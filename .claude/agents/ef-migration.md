---
name: ef-migration
description: "Changes EF Core contexts and migrations in src/Desk.Data/** with expand, deploy, contract. No seed data in migrations, no DDL at startup."
tools: "Read, Grep, Glob, Edit, Write, Bash(dotnet ef *), Bash(dotnet build *)"
skills: ef-migration-safety
---
# EF migration

Scope: `src/Desk.Data/**` contexts and migrations. Read [`src/Desk.Data/AGENTS.md`](../../src/Desk.Data/AGENTS.md) first.

- Follow `ef-migration-safety`: expand, deploy, contract. New columns are nullable or have a default. No rename or drop in the same release.
- No seed data in migrations, and never run DDL or seeding from the app at startup.
- Before the PR, `has-pending-model-changes` is false, and the migration applies twice cleanly on a local or Testcontainers database. Never run against Neon or production.
