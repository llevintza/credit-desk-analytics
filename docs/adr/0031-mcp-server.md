# ADR-0031: MCP server: local stdio, read-only, entitlement-scoped (API mode first, local-DB mode for local stacks, remote HTTP later)

- **Status:** Proposed
- **Date:** 2026-10-20
- **Phase / PR:** cross-cutting (#553, epic #536); built by #554–#558, remote HTTP is #559

## Context

Leo wants assistants (his own, and the repo's agents during development) to answer desk questions by calling tools: "what's our exposure to this sector", "top issuers by MV", "this fund's IRR over the last year", "who holds this deal". The Model Context Protocol (MCP) is the standard way to offer such tools. On 2026-10-20 Leo decided the shape (D15): **a local stdio server first, read-only and entitlement-scoped, talking to the API with a token or to a local database; remote HTTP later and at lower priority.** He then took every open recommendation in the epic's decision table ("[Coordinator] Leo's ruling, 2026-10-20" on #536): D16, D17, D19 and D20 below are **decided**, not options.

The server has to fit rules the repo already has:

- **Entitlements (ADR-0021).** Every read of portfolio-owned data resolves its scope through `IPortfolioEntitlements` and applies it in SQL. A second consumer must not get a way around that.
- **SQL safety (README §8, `src/Desk.Data/AGENTS.md`).** Client-supplied identifiers go through the column-catalog whitelist, and every value is a parameter.
- **Auth (ADR-0024, #353, #354).** Bearer access tokens last 5 minutes. Refresh tokens are single-use with an 8 h sliding life and a 24 h absolute cap. Presenting a refresh token that was already swapped ends that sign-in (outside a 30 s grace for the token swapped last).
- **Limits and free tiers (README §7.2, ADR-0014).** Per account: 60 requests/min with a burst of 60, 1 request in flight with a queue of 8, and insights reads in their own partition (5 in flight). Globally: 8 database permits. The block cap is 500 rows and the column cap 250. Every request that reaches production wakes Render and possibly Neon. Keep-awake pingers are forbidden.
- **The agent harness (ADR-0020).** `.mcp.json` is governed, and drift check D18 fails on **any** server there today. The guard hook's `mcp__.*` matcher (`scripts/agents/hooks/lib/mcp.mjs`, `decideMcp`) runs on every MCP tool call. Agents never touch Neon, Render or production (AGENTS.md "Hard rules").
- **No PII in output.** Account emails are personal data. A tool result ends up in a model's context and possibly in transcripts.

## Options considered

The transport and backend:

1. **(A) stdio server over the API.** A new console project `src/Desk.Mcp` built on the official C# MCP SDK (the `ModelContextProtocol` NuGet package, version pinned in #554), with stdio transport: no listener and no port. Each tool is a typed call to one existing `/api` endpoint, signed in as a real account.
2. **(B) stdio server over a local database.** The same server reads through the `Desk.Data` repositories and `GridSqlBuilder` directly, with an `IPortfolioEntitlements` built for a named local account. The host is restricted to local ones.
3. **(C) Remote streamable-HTTP endpoint at `/mcp` inside Desk.Api**, so hosted assistants can connect without a local install.
4. **(D) stdio server straight to the production database (Neon)**, through `Desk.Data` with the app role's connection string.

Auth in API mode (D16):

- **(i) `desk-mcp login`.** Its own sign-in through `POST /api/auth/login`, with the refresh token stored in a 0600 file.
- **(ii) Personal access tokens.** Read-only, scoped, revocable, at most 30 days. That is new auth surface: a token table, hashing, an SPA screen to revoke them, and its own ADR.

Where the server is registered (D20):

- **(a) User-level MCP config only**, which needs no governance change.
- **(b) The project `.mcp.json`** plus a drift-check D18 allowlist entry, which is a `[workflows]` change.

## Evaluation

| Criterion | (A) stdio over API | (B) stdio over local DB | (C) remote HTTP on Desk.Api | (D) stdio direct to Neon |
|---|---|---|---|---|
| Entitlement reuse | **Complete.** The API resolves `IPortfolioEntitlements` from the token's claims; the server never computes scope. | Same repositories and the same `IPortfolioEntitlements` contract, but a second construction site (from the local account's grants). It needs an equality test against (A) and `EntitlementArchitectureTests`. | Complete (same services in-process). It adds a scoped/exempt classification for `/mcp`. | A second construction site **against production data**. Nothing stops a bug from bypassing ADR-0021 on real numbers. |
| SQL safety | The API's whitelist is authoritative; the server only pre-checks names for a friendly error. | The same `GridSqlBuilder` and parameters; no new SQL. | Same as the API. | Same code, but a mistake reaches production. |
| New auth surface | None with (i): it uses the existing login and refresh. | None. The account is named, not authenticated, and only local hosts are allowed. | **Large.** MCP's authorization is OAuth-based (an authorization server, protected-resource metadata, client registration), or PATs (ii). | None in the app, but a production DB credential leaves the dashboards. |
| Secrets on disk | One refresh token, 0600, under the user's config directory. It is single-use, lasts 8 h sliding and 24 h absolute, and ends at logout, `disable` or `reset`. | Nothing new: the local compose connection string arrives through the environment, and the server never reads `.env`. | None on the client for (ii) beyond the PAT; OAuth client state for (a). Server-side token store. | **A long-lived Neon connection string** with the app role's rights, on a laptop. |
| Rate limits, audit, cache, maintenance mode | **All apply unchanged**, because they are the API's. | None of them (no API in the path). Acceptable only because the database is local. | All apply (`/api`-style policies on `/mcp`). | **None.** It bypasses the per-user limiter, the 8 permits, the audit, the cache and the kill switch. |
| Free-tier cost (production) | Each uncached call wakes Render and maybe Neon. Repeat calls are cache hits until the next as-of date (ADR-0014). Allowed for Leo's interactive use only (D17). | None (never production). | Each call wakes Render; streamable HTTP holds connections on the 512 MiB instance. Polling hosted clients would act as pingers. | Every call is Neon compute with no cache in front. |
| Harness and governance | User-level config now (D20 a); `.mcp.json` later through `[workflows]` (#557). | Same. | `render.yaml` keys and a CORS/CSP review if it ever ships. | Breaks the "agents never touch Neon" rule outright. |
| Verdict | **Chosen (default mode).** | **Chosen as a secondary local-only mode (#558).** | **Deferred (#559)** with its own auth ADR. | **Rejected.** |

### Output size: what 64 KB holds (D19)

ADR-0007 measured a 200-row Risk block (42 columns) at **170.4 KB as row JSON** (an object per row, keys repeated) and **61.9 KB as columnar JSON** (one header, values only). The All preset (197 columns) measured 880.5 KB and 304.2 KB. Dividing by 200 rows gives the uncompressed size of each row in an MCP tool result, which is JSON text without HTTP compression:

| Columns | Records (keys per row) | Rows in 64 KB | Header + row arrays | Rows in 64 KB |
|---|---:|---:|---:|---:|
| Risk preset (42) | ~0.85 KB/row | **~75** | ~0.31 KB/row | **~210** |
| All (197) | ~4.4 KB/row | ~14 | ~1.5 KB/row | ~42 |

So with records, the byte cap binds before the 100-row default even on the Risk preset. With a single `columns` header and one array per row, 100 Risk rows take about 31 KB, and the 500-row maximum fits in 64 KB at roughly 8–9 columns. 64 KB of JSON is on the order of 16k–20k model tokens, which is already a large tool result.

### Tool names against the guard

Every name below was run through `decideMcp` as `mcp__desk__<tool>`, with representative arguments. `query_positions` was also run with a filter model keyed by each of the 104 snake_case identifiers in `src/Desk.Data/Catalog/ColumnCatalog.cs`, because `decideMcp` walks nested argument keys looking for admin keys (`private`, `visibility`, …). None collides.

```
$ node perf/mcp-tool-names.mjs      # imports scripts/agents/hooks/lib/mcp.mjs from the checkout (main at 054b073)
allow  mcp__desk__get_as_of_dates
allow  mcp__desk__list_columns
allow  mcp__desk__list_portfolios
allow  mcp__desk__query_positions
allow  mcp__desk__get_positions_summary
allow  mcp__desk__get_fund_performance
allow  mcp__desk__list_insight_sources
allow  mcp__desk__get_insight
allow  mcp__desk__list_deals
allow  mcp__desk__get_deal
allow  mcp__desk__query_positions with a filter on each of 104 catalog identifiers
allow  mcp__desk__delete_preset  (control: a name the ADR forbids)
allow  mcp__desk__update_preset  (control: a name the ADR forbids)
allow  mcp__desk__clear_cache  (control: a name the ADR forbids)
BLOCK  mcp__desk__dispatch_ingest  (control: a name the ADR forbids)
BLOCK  mcp__desk__list_workflow_dispatch  (control: a name the ADR forbids)
all ADR-0031 tool names allowed
```

The controls show a gap. On a server that isn't GitHub-shaped, `decideMcp` passes every name except the write verbs (`merge`, `dispatch`, `rerun`, …) and the admin argument keys. **The guard does not enforce read-only on the desk server today**, so `mcp__desk__delete_preset` would pass. `query_` isn't one of its read prefixes (`get_`, `list_`, `search_`, `read_`) either; `query_positions` passes only through the "every other server" fall-through. Read-only therefore rests on the server, which registers no write tools. #557 closes the gap in the guard (Decision 6).

## Decision

**We build (A), a local stdio server over the API, as the default, and (B), local-DB mode, for local stacks only. (C) is deferred to #559 and (D) is rejected.** In detail:

1. **Shape.** `src/Desk.Mcp` is a console app on the official C# MCP SDK with stdio transport. It opens no listener and no port. It ships as a `dotnet tool` (`desk-mcp`) packed from the repo build and installed from a local folder; nothing is published to a public feed. stdout carries only the protocol; logs go to stderr and never contain tokens, passwords, emails or connection strings. It offers tools only (no MCP resources or prompts), each annotated `readOnlyHint: true`, `idempotentHint: true` and `openWorldHint: false`.
2. **API mode (default; #554–#556).**
   - The server is a typed client of `/api`. Entitlements, rate limits, caching, audit and maintenance mode apply unchanged.
   - **Origins.** `login` and `serve` take `--api <origin>`. `http://` is refused except for `localhost`, `127.0.0.1` and `[::1]`. **D17:** any origin other than those local hosts and compose service names also needs an explicit `--live` (the `perf/security-headers.mjs` precedent). Only Leo passes it, with his own account. Agents use a local stack with an account they created locally with `Desk.UserAdmin`, as for e2e and k6. The AGENTS.md line goes in with #557.
   - **Auth (D16 = (i)).**
     - `desk-mcp login --api <origin>` prompts for the email and the password on the terminal, never as arguments or environment variables. It calls `POST /api/auth/login` and stores only the origin and the refresh token, never the email or the password.
     - The token file lives in the user's config directory (`$XDG_CONFIG_HOME/desk-mcp/`, else `~/.config/desk-mcp/`; on Windows `%APPDATA%\desk-mcp\` with a user-only ACL), one file per origin. It is created 0600 and replaced by write-temp-then-rename. It is never under the repo.
     - `serve` keeps the access token in memory. It refreshes **only on demand**: when the token has expired at call time, or once on a 401. There is no background timer, so an idle session never wakes the API.
     - Before refreshing, `serve` takes a file lock and re-reads the file, so two `serve` processes sharing one sign-in don't present the same refresh token twice. Doing that would trip reuse detection (#353) and sign both out. The rotated token is written back before the lock is released.
     - The server keeps its **own** sign-in and never takes a token copied from a browser tab: reuse detection would end the tab's session or the server's.
     - When the refresh is refused (reuse, 24 h cap, account disabled or expired), the tool returns "sign in again with `desk-mcp login`".
     - `desk-mcp logout --api <origin>` calls `POST /api/auth/logout` and deletes the token file.
   - **PATs (ii)** are not built. They are reconsidered with #559, under their own ADR.
3. **Local-DB mode (#558).**
   - `desk-mcp serve --db --as <local account email>` reads `ConnectionStrings__App` from the environment.
   - **Before any connection**, it refuses any host but `localhost`, `127.0.0.1`, `[::1]` and the compose service names, with exit 2 and a message naming the host only. Neon is never a valid target.
   - It reads the account's role and grants once at start, and `ENTITLEMENTS_MODE` as the API does. From them it builds a fixed `IPortfolioEntitlements`, which stays synchronous with no I/O afterwards (ADR-0021).
   - The tools call the same `Desk.Data` repositories and `GridSqlBuilder` as the API: `AsNoTracking`, a `CancellationToken` throughout, and a 10 s command timeout. There is no new SQL outside them, and no read of `MetaSnapshot.UnscopedPortfolios` or `MetaRepository.PortfoliosAsync` (`EntitlementArchitectureTests`' allowlist does not grow).
   - An equality test pins its results to API mode for the same query.
4. **Tools.** Read-only, with `get_`/`list_`/`query_` names, each mapped to one existing endpoint (or, for `list_insight_sources`, to none):

   | Tool (story) | Endpoint | Inputs | Output and scoping |
   |---|---|---|---|
   | `get_as_of_dates` (#554) | `GET /api/meta/as-of` | none | The loaded as-of dates, newest first. Not portfolio-owned. |
   | `list_columns` (#554) | `GET /api/meta/columns` | none | Name, group, type and aggregation per catalog column. |
   | `list_portfolios` (#554) | `GET /api/meta/portfolios` | none | The **entitled** portfolios only (ADR-0021). |
   | `query_positions` (#555) | `POST /api/positions/query` | `as_of?`, `portfolios?` (intersected with the entitled set by the API), `columns?` (default: the Risk preset), `filters?` (the API's filter model), `sort?`, `limit?` (default 100, max 500) | `columns` header + row arrays, then the totals row over **all** filtered rows. The description says the totals mix net sums and gross \|MV\|- or face-weighted averages (README §8, #191, #459). |
   | `get_positions_summary` (#555) | `POST /api/positions/query` (summary only) | as `query_positions`, without `limit` | The totals row only. |
   | `get_fund_performance` (#556) | `GET /api/funds/{id}/performance` | `fund_id`, `from?`, `to?` (months) or `range?` | Monthly rows; all-or-nothing per the ADR-0021 fund amendment (refused when not every portfolio of the fund is entitled). |
   | `list_insight_sources` (#556) | none (no request) | none | The five sources (`core`, `market`, `surveillance`, `pricing`, `reference`) and the aggregation grain of their grids. |
   | `get_insight` (#556) | `GET /api/insights/{source}` | `source`, `grid?`, `as_of?`, `portfolios?`, `limit?` | Each grid as header + row arrays, capped per grid; scoped in SQL by the API. |
   | `list_deals` (#556) | `GET /api/deals` | `as_of?`, `portfolios?`, `limit?` | Deals with the representative bond and the caller's holdings. The endpoint returns every deal from cache, and the server applies the row cap. |
   | `get_deal` (#556) | `GET /api/deals/{id}` | `deal_id`, `as_of?`, `portfolios?` | Tranches, 12 months of surveillance, and the caller's scoped positions. 404 → a clean "no such deal". |

   **Not exposed:**
   - presets (they write);
   - CSV export;
   - the marks stream and overlay totals (streams);
   - `/api/admin/*`, `/api/auth/*`;
   - `/api/me`, because it returns the account's email;
   - Lab and dev knobs.

   **Output rules:**
   - **No emails or other personal data** appear in any output or error.
   - ProblemDetails map to MCP tool errors carrying the status and title, never a token, email or connection string.
   - Requests carry `User-Agent: desk-mcp/<version>`. They are audited as the signed-in account, like any API call.
5. **Caps (D19 = 100 / 500 rows, 64 KB, truncation flagged).**
   - **Rows:** default 100, maximum 500 (the API block cap).
   - **Columns:** names are checked against `list_columns` before a request is sent; the API's whitelist and its 250-column cap stay authoritative.
   - **Bytes:** 65,536 bytes of UTF-8 per tool result.
   - **Row shape:** one `columns` header plus row arrays, not an object per row. Per the table above, that fits about 2.8 times the rows in the same bytes.
   - **Truncation flag:** when either cap cuts, the result says so: `truncated: true`, `truncated_by: "rows" | "bytes"`, `rows_returned`, and `rows_matched` when the endpoint reports it.
6. **Limits.**
   - A client-side token bucket of **30 requests/min**, half the per-account 60/min. The partitions are keyed on the account id (#361), so the SPA under the same account shares the bucket.
   - A **concurrency of 1**, matching the per-account limiter; that also keeps insights reads well inside their 5-in-flight partition.
   - On 429 it honours `Retry-After`, waits at most 60 s, then reports "rate limited".
   - On 503 in maintenance mode it reports the maintenance message without retrying.
7. **Harness (D20 = (a)).**
   - Registration is **user-level config only** for now. For Claude Code that is `claude mcp add --scope user desk -- desk-mcp serve --api http://localhost:8080`; other harnesses use their own user-level file. Nothing goes into `.mcp.json`.
   - #557 (`[workflows]`, Helms signs off) later adds:
     - the project `.mcp.json` entry;
     - the drift-check D18 allowlist for exactly that server and command shape;
     - the AGENTS.md D17 line;
     - **a desk-server rule in `decideMcp`** that passes only `get_`/`list_`/`query_` names on `mcp__desk__*` and blocks everything else (so `mcp__desk__delete_preset` blocks);
     - guard fixtures for every tool in the table.
   - Until then, tool names must stay on the "allow" side of the run above. #554 adds that run as a test.
8. **Free tiers.** In API mode against production, every uncached call can wake Render and Neon, and a token refresh touches `auth.sessions`. That's accepted for Leo's interactive use. **Scheduled, looping or polling use is forbidden** (no pingers, README §7.2), and the server has no timer of its own.
9. **Remote HTTP (#559, deferred).** A streamable-HTTP `/mcp` endpoint on Desk.Api would need:
   - an auth decision in its own ADR: MCP's OAuth-based authorization (an authorization server, a large new surface), or read-only PATs (hash-only, ≤ 30 days, revocable from the SPA); the existing 5-minute bearer tokens with refresh rotation suit hosted clients poorly;
   - scoped/exempt classification and entitlement stub tests;
   - per-user and global limits as for `/api`, plus maintenance mode;
   - a CORS and CSP review;
   - OpenAPI exclusion or metadata per ADR-0019;
   - a free-tier review (held connections on 512 MiB; no polling clients).

## Consequences

- **Easier:**
  - Assistants answer desk questions with the same numbers, scope and caching as the pages. There is no second implementation of entitlements or SQL in API mode, and no new public surface or auth surface at all.
  - The first slice (#554) is small: three meta tools and the sign-in.
- **Harder, or the risks we accept:**
  - A refresh token sits on disk for up to 24 h. It is 0600, single-use and revocable (`desk-mcp logout`, `Desk.UserAdmin disable`/`reset`), and the account's grants still bound what it can read.
  - Local-DB mode is a second consumer of `Desk.Data` outside the API. The equality test and `EntitlementArchitectureTests` guard its scoping, and the host check keeps it off Neon.
  - The byte cap binds before the row cap on wide column sets: about 42 rows of all 197 columns. Agents must ask for fewer columns, and the truncation flag tells them when.
  - Until #557 lands, the guard does not stop a write-like desk tool name; read-only depends on the server registering only read tools, and on review.
  - Leo's production use spends his account's rate-limit budget alongside his browser session.
- **Revisit when:**
  - #559 is picked up (remote HTTP, and PATs with it);
  - the C# SDK's breaking changes force a re-pin;
  - tool results routinely truncate at 64 KB;
  - the 30/min budget proves too tight for interactive use.
