# src/Desk.UserAdmin: account admin CLI

Area file for `src/Desk.UserAdmin/` (console CLI for invite-only accounts). The root `AGENTS.md` still applies in full and its hard rules win. Spec: README §7.1; ADR-0005; ADR-0021 for grants.

## Rules

- **Local and test databases only.** Run the CLI only against the compose Postgres you started or Testcontainers; never against Neon or production, and never with a connection string a human exported for you. Real accounts are created by a human (README §7.1, §13 step 8).
- **Passwords:** `PasswordGenerator` output is printed once to stdout and stored only as a hash. Never log it or put it in stderr, audit, exceptions, tests or PR text. No `EnableSensitiveDataLogging`.
- **Output contract:** keep `Password (shown once, share it out of band): <pw>` byte-stable. `UserAdminTests` and CI's `budgets` job (`.github/workflows/ci.yml`) parse it, so changing it needs a `[workflows]` PR for `ci.yml` first.
- **Security stamp:** `disable` and `reset` rotate it on purpose (live sessions end). `grant`/`revoke` (ADR-0021, #122) must **not** rotate it; a change takes effect at the next 5-minute stamp refresh (ADR-0021 E5). Each behaviour has a test.
- **Configuration:** environment only (`ConnectionStrings__App` or `DATABASE_URL`). No default connection string, config file or `--connection` flag.
- **Exit codes:** 0 ok; 1 bad arguments, account problem or no connection string; 3 unexpected error (type and message only); 130 cancelled.
- **Not deployed:** the runtime image and the db-tools bundle don't include this CLI. Don't add it to either, or to any workflow that holds production secrets.
- New commands or options update README §7.1 and the usage text in the same PR.

## Commands

- README §7.1 has the full list; the root table has "Create a user". Never paste a printed password anywhere.

## Tests

- `tests/Desk.Api.Tests/UserAdminTests.cs` (Testcontainers via `PostgresApiFactory`; the CLI grants `InternalsVisibleTo` to `Desk.Api.Tests`). Docker required; never skip.
