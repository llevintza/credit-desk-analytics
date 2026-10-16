# Credit Desk Analytics

Front-office analytics for a **structured-credit trading desk**. Very large position grids (large in rows **and** columns), fund performance over user-chosen ranges, multi-source "insight boards" of many small grids, and deal exploration. Built end to end on **.NET 10 + Postgres + Angular**, and tuned at every layer for data loading and rendering.

> **This README is the implementation spec.** A coding agent (or a person) must be able to build the whole system from this file plus [`AGENTS.md`](AGENTS.md) (working rules) and [`docs/adr/`](docs/adr/) (decision records written as the work proceeds). Where this spec says **MUST**, it's an acceptance criterion; **SHOULD** is a strong default that may be overridden by an ADR with measurements.

---

## Contents

1. [Purpose and context](#1-purpose-and-context)
2. [Architecture](#2-architecture)
3. [Tech stack](#3-tech-stack)
4. [Repository layout](#4-repository-layout)
5. [Data model and seed data](#5-data-model-and-seed-data)
6. [Pages and scenarios](#6-pages-and-scenarios)
7. [Security, accounts and resource protection](#7-security-accounts-and-resource-protection)
8. [Backend requirements](#8-backend-requirements)
9. [Frontend requirements and trade-desk UI](#9-frontend-requirements-and-trade-desk-ui)
10. [Performance budgets and how they're measured](#10-performance-budgets-and-how-theyre-measured)
11. [Testing](#11-testing)
12. [Local development](#12-local-development)
13. [Deployment: Render + Neon](#13-deployment-render--neon)
14. [CI](#14-ci)
15. [Delivery plan: one PR per phase](#15-delivery-plan-one-pr-per-phase)
16. [Out of scope / later](#16-out-of-scope--later)
17. [Status](#17-status)

---

## 1. Purpose and context

### The users and their day

- **About 60 internal users:** portfolio managers, traders and analysts on one structured-credit desk (CLO, RMBS, CMBS, ABS, CRT). It isn't public-facing, and there are no external investors.
- **Overnight batch:**
  - Pricing and risk jobs run overnight and finish at about **06:30 ET**.
  - From then until the next batch the data is **static**: no intraday ticks, except the optional stretch overlay in P6.
  - Users arrive at about 07:30 and open their start-of-day screens.
- **They live in Excel, and they want the app to behave like Excel:**
  - Every row, every column, scroll anywhere.
  - Sort, filter, totals that always reflect the current filter.
  - Copy and paste, export.
- **Priorities:**
  1. **Correct numbers.** Silent errors are the worst kind.
  2. **Speed.**
  3. Look and feel. Dashboards and interactivity matter more and more, but they come third.
- **Delivery cadence:** weekly, sometimes twice a week. Every feature ships as a small, reviewable PR.

### What "fast" means here (the targets behind §10)

| Moment | Target (warm instance) |
|---|---|
| Open the start-of-day grid (20,000 × 200) and see the first rows | **< 1.0 s** |
| Scroll anywhere (vertical and horizontal) | 60 fps, no blank rows longer than 150 ms |
| Change sort or filter | New first block **< 400 ms**, and the summary row updates in the same response |
| Switch fund-performance range | **< 300 ms** |
| Insight board (20 grids, 5 sources) | The first tile paints < 500 ms, all tiles < 1.5 s, and one slow source never blocks the others |

### The engineering story this repo tells

Every layer is a deliberate, **measured** decision, recorded as an ADR in the PR that made it:

- **Database:** a wide snapshot for "today", narrow history for trends, the right indexes, and a correct aggregation grain.
- **Data access:** **Dapper** where the result shape is dynamic (the grid), and **EF Core** where the model is stable (presets, accounts, config). DbContext lifetime, tracking and concurrency are handled correctly.
- **API and DTOs:** send only what the screen needs (the visible row block × the displayed columns), in a columnar shape. Totals over **all** filtered rows ride in the same response. Overnight data is cached until the next batch.
- **Angular:** AG Grid Community **Infinite Row Model** + column virtualization; signals + OnPush; `switchMap` so stale responses never win; progressive rendering for multi-source pages.

---

## 2. Architecture

```mermaid
flowchart LR
  subgraph Browser
    UI["Angular 22 SPA<br/>AG Grid Community<br/>signals + OnPush"]
  end
  PAGES["GitHub Pages<br/>(the same SPA, ADR-0024)"]
  subgraph Render["Render: one Docker web service"]
    direction TB
    STATIC["Static files<br/>(built SPA in wwwroot)"]
    API["ASP.NET Core 10 minimal APIs<br/>/api/*"]
    CACHE[("IMemoryCache<br/>keyed by as-of date")]
    API --- CACHE
  end
  subgraph Neon["Neon Postgres"]
    CORE[(core)]
    MKT[(market)]
    SURV[(surveillance)]
    PRC[(pricing)]
    REF[(reference)]
    AUTH[(auth)]
  end
  UI -- "static shell (CDN)" --> PAGES
  UI -- "or same origin" --> STATIC
  UI -- "fetch /api, Authorization: Bearer (CORS from Pages; gzip/br, ETag)" --> API
  API -- "Dapper (dynamic grid SQL)" --> CORE
  API -- "EF Core (presets, accounts)" --> AUTH
  API -- "per-source queries in parallel" --> MKT & SURV & PRC & REF
```

Rendered diagrams, for sharing outside GitHub: [service architecture](docs/architecture/service-architecture.png) and [deployment and CI/CD](docs/architecture/deployment.png). The SVG sources sit alongside in `docs/architecture/`; re-render the PNGs after editing one with `node docs/architecture/render.mjs` (Playwright Chromium from `e2e/`, 2×).

**Key decisions (each one gets an ADR, see §15):**

- **One Render web service** serves the built SPA **and** the API from the same origin, and **GitHub Pages** serves the same SPA at `https://llevintza.github.io/credit-desk-analytics/` (ADR-0024, 2026-10-11).
  - **Why Pages too:** front-end deploys without a Docker build, a public URL that stays up while the free API sleeps, and a login page that paints at once during a Render cold start.
  - **Auth:** ASP.NET Core Identity **bearer tokens** (5-minute access token, 8 h refresh token, 24 h absolute). The original same-origin `SameSite=Strict` cookie (ADR-0005) would be a blocked third-party cookie from `github.io`. CORS allows exactly the configured origins, without credentials.
  - **Rejected:** a Render static site plus a separate API service (a second instance for nothing), and a `SameSite=None` cookie (blocked or partitioned as third-party).
- **Neon Postgres** in production and **Postgres 17 in Docker** locally: one SQL dialect everywhere.
- **Five logical data sources** (`core`, `market`, `surveillance`, `pricing`, `reference`), each with **its own connection-string setting**.
  - They live in one Neon database today, as separate schemas.
  - Any of them can move to another database without code changes, which makes the "several data sources on one page" scenario real.
- **Cache-first reads:** the data changes once a day, so every grid/aggregate response is cached in-process until the next as-of date and served with an `ETag`. This protects the free-tier database as much as it speeds up the UI.

---

## 3. Tech stack

Pinned majors. Minors and patches are kept current.

| Layer | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (LTS) | ASP.NET Core **minimal APIs** |
| Data access, dynamic | **Dapper** 2.x + **Npgsql** | Grid queries whose column set, sort and filter are chosen per user at runtime |
| Data access, stable model | **EF Core 10 + Npgsql provider** | Accounts (ASP.NET Core Identity), presets, audit, seed metadata, **migrations** |
| Database | **Postgres 17** (Neon in production) | Schemas per data source; `COPY` for bulk load |
| Frontend | **Angular 22** | Standalone components, **signals**, **OnPush**, zoneless change detection, new control flow (`@if`/`@for`) |
| Reactive | **RxJS 7.8** | `switchMap`/`debounceTime` for query streams; bridged to signals with `toSignal` |
| Grid | **AG Grid Community 36** | **Infinite Row Model**, column virtualization, pinned rows and columns. **No Enterprise features** (no SSRM, server pivot UI, or Excel export) |
| Serialization | System.Text.Json (columnar DTO); **MessagePack** as an optional `Accept` variant | Chosen by measurement (ADR-0007) |
| Tests | **xUnit** + **Testcontainers** (Postgres), **Vitest** (Angular), **Playwright** (e2e + screenshots) | |
| Load/perf | **k6** (API), Playwright traces + the Performance API (UI), a Node payload-size script | |
| Local | **Docker Compose** | `postgres`, `app` |
| Hosting | **Render** (Docker web service) + **Neon** (Postgres) | Free tier first; upgrade paths documented |

---

## 4. Repository layout

```
credit-desk-analytics/
├─ README.md                 ← this spec (+ §17 Status table, updated by every PR)
├─ AGENTS.md                 ← working rules for the implementing agent
├─ CLAUDE.md                 ← pointer to AGENTS.md
├─ docs/
│  ├─ adr/                   ← architecture decision records (template + index)
│  └─ screenshots/           ← Playwright screenshots referenced from PRs
├─ src/
│  ├─ Desk.Api/              ← ASP.NET Core 10: endpoints, auth, rate limiting, caching, serves SPA from wwwroot
│  ├─ Desk.Data/             ← EF Core DbContexts + migrations; Dapper query builders; source registry
│  ├─ Desk.Seeder/           ← deterministic synthetic data generator (console), bulk COPY
│  └─ Desk.UserAdmin/        ← CLI: add/list/disable/reset accounts (prints a generated password once)
├─ tests/
│  ├─ Desk.Api.Tests/        ← integration tests on Testcontainers Postgres
│  ├─ Desk.Data.Tests/       ← query-builder and whitelist unit tests (phase 3)
│  └─ Desk.Seeder.Tests/     ← generator unit tests + Testcontainers seeding tests
├─ web/                      ← Angular 22 workspace (app + Vitest unit tests)
├─ e2e/                      ← Playwright tests + screenshot specs
├─ perf/                     ← LoadBenchmark (ADR-0004), k6 scripts, payload-size script
├─ deploy/
│  ├─ Dockerfile             ← multi-stage: node build web → dotnet publish → runtime image
│  └─ start.sh               ← env check → exec app (no migrations at boot)
├─ render.yaml               ← Render Blueprint (one service, autoDeploy off)
├─ docker-compose.yml
├─ .env.example              ← key names only, never values
└─ .github/
   ├─ workflows/             ← ci.yml (api, web, e2e, budgets), deploy.yml (migrate → seed → deploy → smoke), db-ops.yml
   └─ pull_request_template.md
```

---

## 5. Data model and seed data

All data is **synthetic** and generated **deterministically** (`SEED=42` by default, so the same seed always gives the same database). It must look and behave like real structured-credit desk data: realistic names, distributions, correlations, NULLs where real data has NULLs, and a few deliberate edge cases.

### 5.1 Schemas (= data sources)

| Schema / source | Contents | Connection setting |
|---|---|---|
| `core` | deals, bonds (tranches), portfolios, funds, positions (wide snapshot + narrow history), trades, fund performance | `ConnectionStrings__Core` |
| `market` | rates curves (UST/SOFR swap), credit-spread indices by sector × rating, daily marks history | `ConnectionStrings__Market` |
| `surveillance` | monthly remittance / loan-tape **aggregates** per deal: delinquency buckets, CPR/CDR/severity, WAC, WALA, LTV/FICO bands, OC/IC test results | `ConnectionStrings__Surveillance` |
| `pricing` | vendor marks (three synthetic vendors) vs internal marks per bond per day; challenge flags | `ConnectionStrings__Pricing` |
| `reference` | issuers, servicers, trustees, rating scales, sector taxonomy, holiday calendar | `ConnectionStrings__Reference` |
| `auth` / `app` | Identity tables, user presets, audit log, seed metadata | `ConnectionStrings__App` |

In production all six settings point at the same Neon database (`DATABASE_URL`) unless overridden. The code **MUST** resolve a connection per source through a single `IDataSourceRegistry` (one `NpgsqlDataSource` per distinct connection string).

### 5.2 Core tables

| Table | Rows (seed) | Shape |
|---|---|---|
| `core.fund` | 4 | id, name, inception_date, strategy |
| `core.portfolio` | 12 | id, fund_id, name, manager, benchmark |
| `core.deal` | ~1,500 | deal_id, name (e.g. `CLO 2024-3`), sector (CLO/RMBS/CMBS/ABS/CRT), sub_sector, issuer_id, servicer_id, trustee_id, vintage (year), closing_date, collateral_type, original_balance `numeric(18,2)`, currency, status |
| `core.bond` | ~9,000 | bond_id, deal_id, cusip (synthetic, valid check digit), class (A1/A2/B/C/D/E/…), seniority_rank, original_balance, current_balance, factor, coupon_type (FIXED/FLOAT), coupon_or_margin, index (SOFR/none), rating_sp/moodys/fitch, legal_final, expected_maturity |
| `core.position_snapshot` | **20,000 per as-of date**, **2 as-of dates** (today and the prior business day) | **wide, about 200 columns**, see 5.3 |
| `core.position_history` | about 24 month-ends × 20,000 | **narrow**: as_of_date, position_id, market_value, face, price, spread, dv01, cs01, wal, pnl_mtd |
| `core.trade` | ~150,000 over 2 years | trade_id, bond_id, portfolio_id, trade_ts `timestamptz` (realistic intraday times), side, face, price, counterparty_id, trader |
| `core.fund_performance` | about 40–70 months per fund (from each fund's inception through the month before as-of) | fund_id, as_of_month (month-end `date`), nav `numeric(18,2)`, balance, irr_itd, irr_ytd, net_flows |

**Deliberate edge cases the seed MUST include** (tests depend on them):
- at least **3 deals with zero bonds** (newly announced, pricing pending)
- deals with exactly 1 bond
- bonds with **NULL coupon** (residual/equity tranches)
- positions with **zero face** (fully paid down)
- trades at **23:59:59.xxx** on month-ends
- a fund whose inception is mid-month
- duplicate dates in a cash-flow series (for window-frame tests)

### 5.3 The wide position snapshot (about 200 columns)

One row per (as_of_date, position_id). Column groups, all `double precision` except money (`numeric(18,2)`) and identifiers or text:

| Group | Columns (count) |
|---|---|
| Keys and identity | as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, class, sector, sub_sector, vintage, rating_composite, currency (14) |
| Holding | face, current_face, factor, book_price, book_value, market_value, accrued, unrealized_pnl, pct_of_portfolio_mv (9) |
| Pricing | price, price_chg_1d, yield, spread_bp, oas_bp, dm_bp, z_spread_bp, price_source, vendor_dispersion_bp (9) |
| Rate risk | mod_duration, eff_duration, convexity, dv01, key_rate_dv01 at 2y/5y/10y/20y/30y (9) |
| Credit risk | spread_duration, cs01, jtd (jump-to-default), expected_loss_pct, attachment_pct, detachment_pct, credit_enhancement_pct (7) |
| Cash flow | wal, window_start, window_end, next_pay_date, coupon_current, coupon_next (6) |
| Collateral performance | cpr_1m/3m/12m, cdr_1m/3m/12m, severity_3m/12m, dq_30, dq_60, dq_90plus, foreclosure_pct, reo_pct, wac, wala, ltv_wavg, fico_wavg (18) |
| Deal tests | oc_test_cushion, ic_test_cushion, warf, diversity_score, ccc_bucket_pct (5) |
| P&L attribution (MTD) | carry, roll_down, rates_pnl, spread_pnl, idiosyncratic_pnl, fx_pnl, residual_pnl (7) |
| **Scenario grid** | `scn_r{-200,-100,-50,-25,0,+25,+50,+100,+200,+300}_s{-100,-50,-25,0,+25,+50,+100,+200,+300,+500}` → **price under rate × spread shock, 10 × 10 = 100 columns** |
| Stress summaries | worst_case_price, best_case_price, scenario_range, stress_loss_mv (4) |
| Flags and text | watchlist_flag, restricted_flag, comment_count, last_trade_date, analyst (5) |

That's about **193 columns.** Pad with additional, clearly named analytics to reach ≥ 200 if needed. The **column catalog** (name, group, type, display format, aggregation: sum / wavg-by-market-value / none) **MUST** be data (`app.column_catalog`), seeded alongside the table. The API whitelist and the UI column definitions are both generated from it.

### 5.4 Size budget (Neon free tier: 0.5 GB storage per project at the time of writing; check the current limits)

| Object | Arithmetic | Estimate |
|---|---|---|
| position_snapshot | 2 dates × 20,000 rows × ~1.9 KB/row | ~76 MB (+ PK and 2 indexes ≈ 10 MB) |
| position_history | 24 × 20,000 rows × ~110 B | ~53 MB (+ index ≈ 15 MB) |
| trade | 150,000 × ~100 B | ~15 MB (+ indexes ≈ 8 MB) |
| surveillance / pricing / market | ~60 MB total | ~60 MB |
| deal, bond, reference, app | small | < 10 MB |
| app.audit (90 days, `AUDIT_RETENTION_DAYS`) | 90 × audit rows/day × ~193 B (heap + PK + `IX_audit_at`) | ~17 MB per 1,000 rows/day; break-even with the 350 MB budget at ~4,500 rows/day (ADR-0022) |
| **Total (seed, excluding app.audit)** | | **≈ 250 MB; with app.audit it MUST stay < 350 MB** |

The seeder **MUST** print the final size (`pg_database_size`) and **fail** above 400 MB **before commit**, then roll back so the previous data and `app.seed_metadata` stay untouched. A later `--if-changed` skip must not fail the deploy just because a previous over-budget row is still in the database.

**Later reseeds peak at about 2×.** `TRUNCATE` inside a transaction keeps the old relfilenodes until `COMMIT`, so a `--force` (or SeedVersion bump) at scale 1.0 temporarily needs ~old + new. The seeder prints `SEED_PEAK_EST_MB` (current size + new data) and, on a run that would TRUNCATE, **refuses (exit 2) before TRUNCATE when it exceeds `--cap-mb`** (default 512 MB), leaving data and `app.seed_metadata` untouched (#109; §10). Neon Free has been documented as both 0.5 GB (this spec's planning number) and 1 GB; confirm the project's cap before a production reseed.

**Measured (phase 1, scale 1.0, SEED=42, Postgres 17, linux-x64):**
- **1,563,791 rows** across 20 tables.
- First `--if-changed` on an empty migrated DB: `DB_SIZE_MB=270`, `ELAPSED_S=11.1` (generate+load 10.5 s).
- Forced reseed: `DB_SIZE_MB=271`, `ELAPSED_S=11.5`; peak `pg_database_size` during the transaction **533.8 MB**.
- The snapshot is **202 columns**.
- The total book is about $6.9B market value across 20,001 positions.

See ADR-0003 and ADR-0004.

### 5.5 Seeder requirements

- A .NET console app (`src/Desk.Seeder`). Options:
  - `--seed` (default 42)
  - `--as-of yyyy-MM-dd` (default: the last business day)
  - `--scale` (0.1 for tests, 1.0 default)
  - **exactly one mode is required** (no mode exits 1, because a reseed truncates every seeded table):
    - `--if-changed`: skip when the version, seed and scale match `app.seed_metadata` (what the deploy pipeline uses)
    - `--force`: always reseed (`db-ops` reseed)
    - `--size-report`
  - `--max-mb` (default 400): budget for the **committed** size, checked just before COMMIT; over it the load rolls back (exit 2).
  - `--cap-mb` (default 512, must be > 0): storage cap for the reseed **peak**, checked **before TRUNCATE** on runs that would reseed (version, seed or scale change, or `--force`). Over it, or when the current size can't be read, the seeder refuses with exit 2 and changes nothing. An `--if-changed` skip never checks it. See §10.
  - Both `--max-mb` and `--cap-mb` (and the seeder's `MB` output: `SEED_PEAK_EST_MB`, `DB_SIZE_MB`) are MiB (1024² bytes); the peak estimate rounds up.
- Exit codes: 0 seeded or skipped, 1 bad arguments or pending migrations, 2 over `--max-mb` or `--cap-mb` (nothing committed), 3 unexpected error (SQLSTATE 53100 disk full gets its own message), 130 cancelled (before or after COMMIT; the message says which).
- The prior business day is generated as the snapshot's second as-of date.
- **Cancellation (Ctrl+C or a CI timeout) before COMMIT rolls back the single seeding transaction,** leaving the previous data intact. Exit code 130. A cancellation **after COMMIT** (during `ANALYZE` or the final size report) keeps the new data and its `app.seed_metadata` row, so the next `--if-changed` run skips; it prints `ERROR: cancelled after the seed was committed (version …, scale …); ANALYZE/size report skipped.` and also exits 130 (#285). A cancellation during the final size read of an `--if-changed` skip or of `--size-report` (neither writes anything) prints `ERROR: cancelled; nothing was changed (seed skipped / size report only).` and exits 130, whether it surfaces as a cancellation or as a database error (#324).
- **Each table draws from its own RNG stream** (xoshiro256**, pinned by a test), so adding rows to one table never shifts another table's values.
- **Bulk load via Npgsql binary `COPY`** (`BeginBinaryImport`). EF `AddRange` is only for small tables. ADR-0004 **MUST** include the measured comparison of the two for the snapshot table.
- **Idempotent:** writes a row to `app.seed_metadata` (seed, scale, version, completed_at). If that row matches, skip.
- **Realism:**
  - Correlated draws: spread ~ rating × sector × vintage; price ~ spread × duration; delinquencies ~ vintage × sector.
  - Positions are drawn from bonds with a realistic concentration (some portfolios hold 3,000+ lines).
  - Scenario prices are generated from duration, convexity and spread duration, so they're internally consistent.
  - Names are plausible but fictional. **No real issuer, servicer or company names.**
- **Runs in < 90 s** at scale 1.0 against local Postgres, and < 5 min against Neon.

---

## 6. Pages and scenarios

Every page lists: **user story**, **API contract**, **SQL approach**, **front-end approach**, **acceptance criteria**, **budget**. All endpoints require authentication (§7) and live under `/api`.

### P1. Start-of-day Positions (20,000 rows × ~200 columns)

**User story:** *"At 07:30 I open my portfolio and see every position with every measure, like a spreadsheet. I scroll anywhere, sort and filter instantly, and the totals at the bottom always reflect what's filtered."*

**Front end:**
- AG Grid Community, `rowModelType: 'infinite'`, `cacheBlockSize: 200`, `maxBlocksInCache: 50`, `blockLoadDebounceMillis: 100`.
- **Column virtualization on** (the default; never disable it).
- `getRowId` = position_id.
- Pinned left: deal_name, class, cusip. A pinned **bottom summary row**.
- **Column presets:** per-user saved column state (order, width, visibility, pinned, sort, filters), loaded before the first request. Several named presets per user ("Risk", "Surveillance", "Scenarios", "All").
- **Only the displayed columns are requested.** When the displayed column set changes (show/hide or preset switch), purge the infinite cache and re-request. For horizontal scrolling inside the displayed set, no request is needed.
- **Quick filter box:** debounced at 300 ms, with `distinctUntilChanged` + `switchMap`, so a stale request is cancelled and can never overwrite a newer one.
- **CSV export** (Community) of the current filter and displayed columns, streamed from the API, not the browser cache.
- A status bar showing visible/total rows, last request ms, and cache HIT/MISS.

**API:**

```http
POST /api/positions/query
Content-Type: application/json
Accept: application/json            (or application/x-msgpack, see ADR-0007)

{
  "asOf": "2026-10-07",
  "portfolioIds": [3, 7],
  "startRow": 0,
  "endRow": 200,
  "columns": ["deal_name","class","cusip","market_value","dv01","spread_bp", "..."],
  "sortModel": [{ "colId": "market_value", "sort": "desc" }],
  "filterModel": {
    "sector":     { "filterType": "set",    "values": ["CLO","RMBS"] },
    "spread_bp":  { "filterType": "number", "type": "greaterThan", "filter": 250 },
    "deal_name":  { "filterType": "text",   "type": "contains",    "filter": "2024" }
  },
  "quickFilter": "clo 2024"
}
```

```jsonc
// 200 OK, ETag: "w/as-of-2026-10-07:sha1(query)", Server-Timing: db;dur=38, ser;dur=6, X-Cache: HIT|MISS
{
  "columns": ["position_id","deal_name","class","cusip","market_value","dv01","spread_bp"],
  "data": [[1,2,3,…],["CLO 2024-3",…],["A1",…],["…"],[1250000.00,…],[512.3,…],[145,…]], // data[col][row]
  "rowCount": 18342,                          // total rows matching the filter (drives the scrollbar)
  "summary": { "market_value": 8.12e9, "dv01": 3.4e6, "spread_bp": 212.4 }, // over ALL filtered rows
  "asOf": "2026-10-07",
  "generatedAt": "2026-10-07T11:02:13Z"
}
```

**Server rules:**
- **Whitelist:** column ids, sort ids and filter ids are resolved through the column catalog. Unknown ids are **dropped**, never concatenated into SQL. Every value is a parameter.
- `position_id` is always included (the row id) and always appended to `ORDER BY` as a **deterministic tie-breaker** (otherwise offset paging duplicates or skips rows).
- Block size is clamped to ≤ 500 rows, and displayed columns to ≤ 250.
- **One round trip:** the page query and the totals query (`COUNT(*)` + per-column aggregates from the catalog: SUM, or |market value|-weighted average) are sent together (`QueryMultipleAsync` or a single batch).
- **Weighting rule:** weights are `ABS(market_value)`; see §8 (#131).
- **Paging:** `OFFSET … FETCH` by default. ADR-0008 evaluates keyset paging, which the Infinite Row Model's random-access jumps make harder, and records the measured trade-off at deep offsets.
- **Caching:** the key is (as-of, canonicalized query JSON, user's portfolio entitlements). Entries live until the next as-of date. Support `If-None-Match` → **304**.
- The `CancellationToken` flows to Npgsql, so scrolling past a block cancels its query. The command timeout is 10 s.
- **Indexes:** `(as_of_date, portfolio_id)` + `INCLUDE` of the common key columns; plus partial or btree indexes for the top sort columns, chosen by `EXPLAIN (ANALYZE, BUFFERS)` evidence recorded in the PR.

**Acceptance criteria:**
- [ ] With 18k+ rows matching, scrolling to the last row shows correct data, and the summary row equals a `SUM` computed independently in SQL (test).
- [ ] An injection attempt in `sortModel.colId`, `filterModel` keys or `columns` is dropped (test), with no 500.
- [ ] Changing the displayed columns re-requests with the new `columns` and nothing else.
- [ ] A second identical request returns `X-Cache: HIT`, and `If-None-Match` returns 304.
- [ ] Rapid typing in the quick filter results in **one** completed request (others cancelled), proven by a Playwright network assertion.

**Budget:**
- First block (200 rows × the "Risk" preset of about 40 columns) ≤ **60 KB** compressed.
- "All" preset (200 × 200) ≤ **250 KB** compressed.
- API p95 ≤ 150 ms warm (cache MISS) and ≤ 15 ms (HIT).
- First rows painted < 1.0 s warm.

### P2. Fund Performance (2 rows × N months)

**User story:** *"Show me my fund's balance and IRR month by month for QTD, YTD, last 12 months, since inception, or a custom range."*

**API:**

```http
GET /api/funds/{fundId}/performance?range=YTD            (QTD | YTD | 1Y | ITD | CUSTOM&from=2025-01-31&to=2026-09-30)
```

```jsonc
{
  "fundId": 2,
  "months": ["2026-01-31","2026-02-28","2026-03-31","…"],
  "rows": [
    { "label": "Balance", "format": "money0", "values": [100000000.00, 104000000.00, …] },
    { "label": "IRR",     "format": "pct2",   "values": [0.0500, 0.0620, …] }
  ]
}
```

- **SQL:** long format (`SELECT as_of_month, balance, irr_itd FROM core.fund_performance WHERE fund_id=@f AND as_of_month BETWEEN @from AND @to ORDER BY as_of_month`). The **pivot to columns happens at the edge** (C# or client). There's no dynamic SQL `PIVOT`; ADR-0011 records the measured or argued trade-off.
- **Server validation:** every `values` array has `months.length` entries, otherwise 500 with a logged invariant violation. A range with no data returns empty arrays, not an error.
- **Front end:**
  - Column defs are **built from `months`** in a `computed()`. The row label column is pinned. Month headers read `Jan 26`.
  - Range buttons, plus a date-range picker for CUSTOM.
  - `switchMap` cancels the in-flight request when the range changes.
  - A sparkline per row is optional.
- **Acceptance:**
  - [ ] QTD, YTD, 1Y and ITD give the right month counts on fixed seed data (test).
  - [ ] A fund with mid-month inception starts at its first month-end.
  - [ ] Flipping ranges quickly never shows a stale range.
- **Budget:** < 300 ms; payload < 5 KB.

### P3. Insights Board (20 small grids from 5 data sources)

**User story:** *"Give me one page that slices the book 20 ways (by sector, vintage, rating, servicer, delinquency bucket, vendor-price dispersion…) so I can spot patterns, the way I'd pivot raw data in Excel."*

**Grids, about 5 × 5 each, grouped by source:**

| Source | Grids (examples) |
|---|---|
| `core` (6) | MV by sector × rating; DV01 by sector × duration bucket; P&L attribution by portfolio; top 5 issuers by MV; concentration by vintage; watchlist by sector |
| `market` (3) | spread change 1W/1M by sector × rating; curve moves; spread percentile vs 2Y |
| `surveillance` (5) | 60+ DQ by sector × vintage; CPR/CDR by servicer; OC cushion buckets; WARF by CLO vintage; LTV/FICO bands |
| `pricing` (3) | vendor dispersion buckets; internal vs vendor by sector; challenged marks count |
| `reference` (3) | servicer exposure; trustee exposure; rating-agency split |

**API:** **one endpoint per source**, not per grid and not one per page:

```http
GET /api/insights/{source}?asOf=2026-10-07&portfolioIds=3,7
→ { "source": "surveillance", "grids": [ { "id": "dq60_sector_vintage", "title": "...", "columns": [...], "rows": [[...]], "format": {...} }, ... ] }
```

- Inside an endpoint, the source's group-by queries run **in parallel**, each with **its own connection / DbContext** (`IDbContextFactory<T>` or `NpgsqlDataSource.OpenConnectionAsync` per task). **Never share a DbContext across concurrent tasks.** A test **MUST** prove the shared-context version throws and the per-task version passes.
- Fan-out is capped by a `SemaphoreSlim` (configurable; default 4 per request) **and** the global DB concurrency limiter (§7).
- Results are cached per (source, as-of, portfolios).
- **Correct aggregation grain:** never sum a parent-level field after joining to children. Aggregate the child first. A test covers deal original balance by sector (the classic fan-out double count).

**Front end:**
- **Five independent streams**, one per source (`toObservable(inputs) → debounceTime → switchMap(http) → startWith(loading) → catchError(error tile)` → `toSignal`), so tiles render **progressively**.
- A slow source shows skeleton tiles while the others are already painted. A failing source shows an error tile with retry, and the rest of the page is unaffected.
- Tiles are lightweight tables (or minimal AG Grid instances; ADR to decide by measurement), laid out in a CSS grid: `repeat(auto-fill, minmax(320px, 1fr))`.
- A **"naive mode" toggle** (dev or admin only) fires 20 separate requests, for the Performance Lab comparison.

**Acceptance:**
- [ ] With an artificial 2 s delay injected into one source (a dev-only header), the other four render first (Playwright).
- [ ] One source returning 500 shows exactly that source's error tiles.
- [ ] Changing the as-of date cancels in-flight calls.

**Budget:** first tile < 500 ms; all < 1.5 s warm.

### P4. Deal Explorer

**User story:** *"Show me all deals, each with one representative bond, then drill into a deal's full capital structure."*

- **Every deal appears even with zero bonds.** Postgres equivalent of SQL Server `OUTER APPLY`:

  ```sql
  SELECT d.deal_id, d.name, d.sector, b.class, b.current_balance
  FROM core.deal d
  LEFT JOIN LATERAL (
      SELECT b.class, b.current_balance
      FROM core.bond b
      WHERE b.deal_id = d.deal_id
      ORDER BY b.seniority_rank, b.bond_id   -- "representative" = most senior, deterministic
      LIMIT 1) b ON true;
  ```

- ADR-0013 compares `LATERAL … LIMIT 1` with `ROW_NUMBER() … = 1` + `LEFT JOIN` using `EXPLAIN (ANALYZE, BUFFERS)`, with an index on `bond(deal_id, seniority_rank, bond_id)`.
- **Master/detail:** selecting a deal loads its tranches (capital structure stack chart + grid), its surveillance trend (12 months), and its positions across portfolios.
- **Acceptance:**
  - [ ] The seed's zero-bond deals appear with blank bond columns (test).
  - [ ] Deal-level totals don't double count (test).

### P5. Performance Lab (the demo page)

**User story:** *"Show, with live numbers, why the grid is built the way it is."*

**Toggles for the P1 query:**
- payload format: row-object JSON / columnar JSON / MessagePack
- block size: 100 / 200 / 500
- columns: displayed-only / all 200
- cache: on / off (admin only)
- compression: br / gzip / none (dev only)
- P3: naive 20 calls vs 5 per-source calls

**It displays, per run:**
- bytes on the wire (`PerformanceResourceTiming.encodedBodySize`) and decoded size
- TTFB, download time, `JSON.parse` / decode time, grid render time (a `performance.mark` around `setRowData`/block success)
- server `Server-Timing` breakdown (db, serialize), cache status

A results table keeps the last 20 runs, with a "copy as markdown" button so the numbers can go straight into a PR or ADR.

**Acceptance:** all toggles work, and the numbers come from real requests, not hard-coded.

### P6 (stretch). Intraday overlay

An SSE stream (`/api/marks/stream`) of simulated mark changes for a subset of bonds, merged onto the overnight snapshot in P1:
- Each event carries `(bond_id, seq, event_ts)`.
- The client ignores events whose `seq` is ≤ the last applied one, so a reconnect or restart never applies stale data.
- Changed cells flash, and totals refresh from the server periodically.

---

## 7. Security, accounts and resource protection

The **website is public** (anyone can reach the login page). The **data is not**: everything under `/api` except `/api/auth/login` and `/api/auth/refresh` requires a bearer access token (`/health` is outside `/api` and public).

### 7.1 Accounts (invite-only)

- **ASP.NET Core Identity** on EF Core (Postgres, `auth` schema). **There is no registration endpoint.**
- **Roles:** `viewer` (all pages, read-only) and `admin` (plus the usage page, cache controls, naive-mode toggles).
- **Each account has an `ExpiresAt`;** login is refused after it. Reviewer accounts get short expiries.
- **Lockout:** 5 failed attempts gives a 15-minute lockout. Password policy is Identity defaults with a minimum length of 14.
- **Session (ADR-0024, replacing ADR-0005's cookie):**
  - `POST /api/auth/login` returns Identity bearer tokens: an access token for **5 minutes**, sent as `Authorization: Bearer`, and a refresh token for **8 hours**, renewed by each `POST /api/auth/refresh` (sliding). Neither can outlive **24 h** from sign-in (absolute). No cookies.
  - Each refresh token works **once**: presenting one that was already swapped ends that sign-in for whoever holds its tokens (reuse detection, #353, ADR-0024). The one exception is the token swapped last, for **30 s** after the swap (`AUTH_REFRESH_REUSE_GRACE_SECONDS`, at most 120): it gets the current token pair back without a new swap, so a page reload that aborts a refresh doesn't sign the user out.
  - A refresh re-reads the account: a disable or password reset (`Desk.UserAdmin`, which rotates the security stamp), or an expiry, ends the session at the next refresh, within 5 minutes. Account expiry is also checked on every request.
  - `POST /api/auth/logout` ends that sign-in: its refresh token on every instance and after a restart (stored in `auth.sessions` with the refresh rotation, read only at login, refresh and logout, #354), and its access token at once on the instance that handled the logout. Elsewhere a copied access token works until it expires (at most 5 minutes) or that instance refuses its refresh. `disable`/`reset` end every session for good.
  - The SPA keeps the access token in memory and the refresh token in the tab's `sessionStorage`, so closing the tab ends the session. Script on the page can read both: the XSS trade-off is in ADR-0024.
  - No antiforgery: the browser never attaches a header token by itself, so CSRF doesn't apply.
  - **CORS:** `CORS_ALLOWED_ORIGINS` (comma or space separated `https://` origins, `http://` only in Development; unset = none) allows a cross-site front end. Production sets `https://llevintza.github.io` (`render.yaml`). No credentials; preflights are answered before maintenance mode and the rate limiter.
  - The data-protection keys are persisted to the database, so tokens survive restarts.
- **User admin CLI** (`src/Desk.UserAdmin`):

  ```bash
  dotnet run --project src/Desk.UserAdmin -- add    --email reviewer@example.com --role viewer --expires 2026-11-30
  dotnet run --project src/Desk.UserAdmin -- list
  dotnet run --project src/Desk.UserAdmin -- disable --email reviewer@example.com
  dotnet run --project src/Desk.UserAdmin -- reset   --email reviewer@example.com
  dotnet run --project src/Desk.UserAdmin -- grant   --email reviewer@example.com --portfolio 3,7
  dotnet run --project src/Desk.UserAdmin -- revoke  --email reviewer@example.com --portfolio 7
  ```

  It generates a strong random password, **prints it once** to stdout, stores only the hash, and **never logs it**. `grant` and `revoke` change the account's portfolio grants without rotating the security stamp (nobody is signed out); `list` shows each account's grants (`none`, the ids, or `all (admin)`). Against production it runs locally with `ConnectionStrings__App` pointed at Neon. Credentials are shared out of band and are **never** committed or put in issues or PRs.
- **Portfolio entitlements (ADR-0021, model B):** `ENTITLEMENTS_MODE` picks who sees which portfolios.
  - `all` (the default): every account sees every portfolio.
  - `grants`: an admin sees every portfolio; anyone else sees only the portfolios granted to them in `app.portfolio_grant` (`Desk.UserAdmin grant`). **No grants means no portfolios** (fail closed). A request for portfolios outside the grant silently returns the granted subset or 0 rows, never 403. Grants for portfolios that a reseed removed drop out.
  - Any other value fails startup. Rollback is setting it back to `all`; no migration is needed.
  - Grants are read into the sign-in claims at login and at every refresh, never per request: a grant or revoke applies at the user's next refresh, within 5 minutes, and doesn't sign them out (the accepted lag, ADR-0021 E5).
  - Every endpoint that returns portfolio-owned data (positions, export, `/api/meta/portfolios`, fund performance, insights, deal holdings) resolves its scope through `IPortfolioEntitlements` and applies it in SQL; cache keys and ETags carry the portfolio set. A test fails for any `/api` path that isn't classified as scoped or exempt.
  - Rollout (ADR-0021): deploy with `all`, load viewer grants with the CLI and the demo `portfolios`, check them with `list` and a single-portfolio test account, then set `ENTITLEMENTS_MODE=grants` in the Render environment.
- **Optional seeded demo accounts:** read from the `DEMO_ACCOUNTS_JSON` env var (secret, set in the Render dashboard). Absent means none.
  - An entry is applied **only when its account is first created**, on that email's first login attempt. Changing an entry's password, role, expiry or portfolios later does not touch the existing account; manage that account with the UserAdmin CLI (`reset` for a new password, `disable` to stop it, `grant`/`revoke` for portfolios).
  - An optional `portfolios` array (portfolio ids) becomes the account's grants, saved with its role; an entry without it gets none, so in `grants` mode it sees no portfolios.
  - An account that can't be given its role is removed again and the login is refused, so the next attempt retries the creation. An account without a role is never signed in; if its removal fails, the error codes (never the password) are logged as an error; disable that account with the UserAdmin CLI.
  - When two first logins race, the one that loses is refused until the winner's account has its role; its retry signs in.

### 7.2 Protecting the free tiers (Render instance hours, Neon compute and storage)

- **Rate limiting** (`AddRateLimiter`):
  - per user: token bucket, 60 requests/min, burst 20
  - per IP on `/api/auth/login`: 5/min
  - per signed-in user concurrency: 1 request in flight, queue 8, then 429 (`RATE_LIMIT_PER_USER_CONCURRENCY` / `RATE_LIMIT_PER_USER_QUEUE`); exports are capped separately (below)
  - per signed-in user, insights source reads (`GET /api/insights/{source}`) in their own partition: 5 in flight, queue 15, then 429 (`RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY` / `RATE_LIMIT_PER_USER_INSIGHTS_QUEUE`), so the P3 board's five sources load in parallel (ADR-0005, #357)
  - **global concurrency limiter** on DB-bound endpoints: 8 concurrent, queue 32, then 429 with `Retry-After`
  - **permits vs the pool (#211):** the permits must fit the Npgsql pool, or the pool limits first and requests time out instead of getting a 429. Each permit holds one connection, except an insights read, which holds up to `INSIGHTS_PARALLELISM` (4), all of them together at most `INSIGHTS_MAX_CONNECTIONS` (10). The worst split of the 8 permits holds 15 connections; with 5 for background work (meta load, audit, DataProtection, `/health/db`) that is the default `DB_MAX_POOL_SIZE` of 20 exactly. At start the API logs a warning naming the four settings when `RATE_LIMIT_GLOBAL_CONCURRENCY`, `INSIGHTS_MAX_CONNECTIONS` or `INSIGHTS_PARALLELISM` is raised, or `DB_MAX_POOL_SIZE` lowered, past that (§13.1)
- **Query guards:** command timeout 10 s; block ≤ 500 rows; ≤ 250 columns; CSV export ≤ 25,000 rows (`EXPORT_MAX_ROWS` may lower it, never raise it; over the cap, see §8 `X-Export-Truncated`).
- **Exports:** 1 at a time per user and `EXPORT_GLOBAL_SLOTS` (2) in total, without waiting: 429 with `Retry-After`. Deadline `EXPORT_TIMEOUT_SECONDS` (60) on the whole stream: 503 before any output; mid-stream the connection is aborted, so a truncated download fails.
- **Cache-first:** grid, aggregate and performance responses are cached until the next as-of date. A healthy demo session should hit Neon only on first views.
- **Kill switch:** `MAINTENANCE_MODE=true` makes every `/api` call return 503 with a friendly message **without touching the database**. The login page shows a banner.
- **Audit:** `app.audit` records (user, endpoint, rows returned, ms, cache status, timestamp) and logins (success and failure). Admin page **Usage** shows requests per user per day, cache hit ratio and slowest queries.
- **Audit retention:** audit rows are kept for `AUDIT_RETENTION_DAYS` days (default **90**; whole days, 1 to 36,500; any other value keeps 90 and logs a warning). Older rows are deleted at most once every 24 h per process (each start re-arms it), in batches of about 50,000 (each its own statement and commit): right after an audit insert when one is due (the first insert after each start, then every 24 h), and, so idle periods drain while the instance stays up (on Render free it sleeps after about 15 min idle, before a 60-minute check), by a timer that checks every `AUDIT_PURGE_CHECK_MINUTES` minutes (default **60**; whole minutes, 1 to 1,440; any other value keeps 60 and logs a warning). A check only reads the clock; the timer touches the database only when a purge is due, never at start, so a cold start doesn't wake Neon. A failed timer purge is logged and retried at the next check, at most twice in a row, then the next day (ADR-0022).
- **Health:** `/health` is static (never touches the DB, so platform probes don't wake Neon). `/health/db` does a real check and, like `/health`, is anonymous and outside `/api`. It answers only up (200, `{"status":"up"}`) or down (a 503 problem, no detail), checks the database at most once per 10 s however often it is called, and is down without a check in maintenance mode. It has no rate limit; the 10 s shared check stands in for one (Helms's ruling on #113). An externally polled `/health/db` keeps Neon awake: Leo accepted that trade-off on 2026-10-15 (option (a) on #365), so don't point an uptime monitor at it unless you mean to pay for that compute.

### 7.3 Hardening

- Security headers:
  - **CSP:** `default-src 'self'`; no inline scripts; `style-src 'self' 'unsafe-inline'` only if AG Grid requires it, documented.
  - HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `Permissions-Policy` minimal.
- **Errors:** ProblemDetails everywhere, with no stack traces outside Development.
- **Logs:** structured, with no secrets and no full connection strings.
- Dependabot for NuGet, npm, GitHub Actions and Docker.

---

## 8. Backend requirements

- **Endpoints** (minimal APIs, grouped, OpenAPI-documented):

  | Method | Path | Purpose |
  |---|---|---|
  | POST | `/api/auth/login`, `/api/auth/logout` | Session |
  | GET | `/api/me` | Current user, roles, expiry |
  | GET | `/api/meta/as-of`, `/api/meta/columns`, `/api/meta/portfolios` | As-of dates, column catalog (drives the UI), the portfolios the user is entitled to (ADR-0021) |
  | GET/PUT/DELETE | `/api/presets/{page}` | User column presets (EF) |
  | POST | `/api/positions/query` | P1 block + summary |
  | POST | `/api/positions/export` | P1 CSV stream |
  | GET | `/api/funds/{id}/performance` | P2 |
  | GET | `/api/insights/{source}` | P3 |
  | GET | `/api/deals`, `/api/deals/{id}` | P4 |
  | GET | `/api/admin/usage`, POST `/api/admin/cache/clear` | Admin |
  | GET | `/health`, `/health/db` | Probes |

- **Data access rules:**
  - **Dapper for dynamic-shape reads** (P1, P3, P4 lists), built by a `GridQueryBuilder` that only emits whitelisted identifiers and parameters. Unit-tested on its SQL output.
  - **EF Core for the stable model:**
    - `AddPooledDbContextFactory`.
    - `AsNoTracking()` + `Select` projections into DTOs for every read. Entities never leave the data layer.
    - `AsSplitQuery()` wherever more than one collection is included.
    - `ExecuteUpdateAsync` / `ExecuteDeleteAsync` for set-based writes.
    - Scoped lifetime per request; a factory for anything parallel or singleton-owned (no captive dependencies).
  - **Every async call takes the request's `CancellationToken`.** No `.Result`, `.Wait()` or `async void`.
  - **Money is `decimal`** (`numeric` in SQL). Rounding happens only at the display edge. Weighted averages weigh each row by `ABS(market_value)`, the position's size, so shorts can't cancel longs (#131). Rows where the measure is `NULL` don't count toward the weight, and zero or no weight returns `null`, never `NaN`. `NaN` can't reach the summary: `market_value` and every column the summary sums or weights reject `'NaN'` at write time (CHECK constraints `ck_snapshot_<column>_not_nan` on the `numeric` columns, #192), the `float8` ones reject `'NaN'` and `±'Infinity'` (`ck_snapshot_<column>_finite`, #304), and a test fails if a new summed or weighted catalog column isn't guarded. SUM columns (`market_value`, `dv01`, `cs01`, `pnl_*`, …) stay signed (net).
- **Serialization:**
  - Columnar DTOs (`columns` + `data[c][r]`).
  - `System.Text.Json` source generation for hot DTOs.
  - MessagePack variant on `Accept: application/x-msgpack` (ADR-0007 decides whether it stays on by default).
- **API docs and testing:** the OpenAPI document is at **`/openapi/v1.json`** (built-in `AddOpenApi`), with **Swagger UI at `/swagger`** for trying every endpoint. Every endpoint has a name, summary and tag.
  - It's on in **Development**. Elsewhere it's off unless `SWAGGER_ENABLED=true`, which you can set in the Render dashboard to try the deployed API.
  - Phase 2 put it behind the admin login, since the site is public (#94). Since bearer tokens (ADR-0024), the OpenAPI document stays admin-only, while the Swagger UI shell (the stock bundle, no API data) loads without a session and signs its calls with the session of the tab it is opened in.
  - The choice is recorded in ADR-0019.
  - The phase-2 CSP must allow Swagger UI's assets on `/swagger` only.
- **Response compression:** Brotli and gzip, on HTTPS too.
- **Caching:** `IMemoryCache` with a size limit, keyed per the P1 rules, expiring at the next batch time (06:30 America/New_York), plus `ETag` / `If-None-Match`.
- **Observability:**
  - A `Server-Timing` header (db, serialize, total) on every data endpoint, and `X-Cache: HIT|MISS`. On the CSV export both are set before the first byte: `db` is the time to the first row, `ser` is 0 (rows are written as they stream), and `X-Cache` is always `MISS`.
  - Structured logs with request id.
  - The slowest 20 queries per day are visible on the admin page.
- **CSV export truncation:** the export counts the matching rows up to one past its cap (`LIMIT cap + 1`, no sort) in the same round trip as the rows. When more rows match than the cap, it writes the cap's rows and sends `X-Export-Truncated: true` before the first byte; otherwise the header is absent.
- **Static SPA:** `UseStaticFiles` + `MapFallbackToFile("index.html")`. Long-cache hashed assets, and `no-cache` for `index.html`.
- **Same origin, so no CORS** is configured in production. In development the Angular dev server proxies `/api` to the API.

---

## 9. Frontend requirements and trade-desk UI

### 9.1 Look and feel: a tool for people who read numbers all day

- **Dark-first** theme (with a persisted light toggle). AG Grid **Quartz** theme customized through CSS variables, in **compact density**: row height about 24 px, header about 28 px.
- **Typography:** self-hosted **Inter** for UI text (no external font CDN; CSP is `self`). Numbers use **`font-variant-numeric: tabular-nums`** and are **right-aligned**.
- **Number formats** come from the column catalog:

  | Kind | Format |
  |---|---|
  | money | `#,##0`, or `#,##0.00` for prices |
  | price | 3 dp |
  | spread | bp, 0 dp |
  | percentage | 2 dp |
  | duration | 2 dp |

  Negatives show in **red** with a minus sign or parentheses (a user setting).
- **A colorblind-safe palette option** for up/down (blue/orange instead of green/red).
- **The scenario grid (100 columns)** gets a **heat-map** cell style: a diverging scale centered on the base price.
- Subtle zebra striping; a frozen header; pinned identifier columns; a hover tooltip with full precision; column group headers by catalog group.
- **No decoration for its own sake:** dense, calm, readable at 1440p and on 4K.

### 9.2 Application shell

- **Top bar:**
  - app name
  - **as-of date** selector (today / prior day)
  - a **freshness chip** ("Data as of 06:30 ET · overnight batch")
  - fund and portfolio multi-select
  - theme toggle
  - user menu (shows account expiry for reviewer accounts)
- **Left nav:** Positions, Fund Performance, Insights, Deals, Performance Lab, Usage (admin).
- **Status bar** (bottom): visible/total rows, last request ms, `X-Cache` HIT/MISS, payload size, API state (Ready / Waking / Maintenance).
- **Login page:** minimal and branded. While the free-tier instance cold-starts, it shows "Waking the server…" with progress and polls `/health`.

### 9.3 Keyboard

| Key | Action |
|---|---|
| `/` | Focus the quick filter |
| `Alt+1…6` | Switch pages |
| `Ctrl+Shift+E` | Export CSV |
| `Ctrl+Shift+P` | Switch column preset |
| Arrows, PgUp/PgDn, Home/End | Grid navigation (AG Grid native) |
| `Ctrl+C` | Copy the selected cells (Community range-less copy of the focused cell or row; document the limit) |

### 9.4 Engineering rules

- **Components:** standalone only, **`ChangeDetectionStrategy.OnPush` everywhere**, zoneless.
- **State and streams:** **signals** for state, **RxJS** for streams over time; bridge with `toSignal`/`toObservable`.
- **Subscriptions:** no manual `subscribe` without `takeUntilDestroyed`. Prefer `toSignal` or the async pipe.
- **Query streams:** **`switchMap`** for any query that can be superseded; `debounceTime` + `distinctUntilChanged` for typed input.
- **Never mutate arrays or objects bound to the view;** set new references.
- **No function calls in templates** that do work; use `computed()`.
- **`@for` always tracks a stable id.**
- **Formatters:** cached `Intl.NumberFormat` instances. Never construct one per cell.
- **API client:** a typed client (hand-written or generated from OpenAPI) in one `data-access` folder. Components never build URLs.
- **Columnar → row conversion** happens only for the block being handed to the grid (`toRows(block)`), and is unit-tested.
- **Error handling:** skeleton loaders and error tiles. One failure never blanks a page.
- **Accessibility:** focus-visible styles, ARIA labels on controls, contrast AA in both themes.

---

## 10. Performance budgets and how they're measured

| Budget | Value | Measured by | Enforced |
|---|---|---|---|
| P1 first block, "Risk" preset | ≤ 60 KB compressed | `perf/payload-size.mjs` against a running API | **CI fails** above budget |
| P1 first block, "All" (200 cols) | ≤ 250 KB compressed | same | CI warns |
| P1 API p95, cache MISS / HIT | ≤ 150 ms / ≤ 15 ms (local, warm); MISS for both the Risk and All presets | k6 `perf/positions.js`: the whole book's uncached first view (#129), HIT with `X-Cache: HIT` asserted | CI reports (`budgets` job step summary, scale 1.0); non-blocking until #87 |
| P1 first rows painted | < 1.0 s warm | Playwright trace + `performance.mark` | Reported in the PR |
| P2 response | < 300 ms, < 5 KB | k6 + payload script | Reported |
| P3 first tile / all tiles | < 500 ms / < 1.5 s | Playwright | Reported |
| Failed-login floor (#230) | every login 401 ≥ 500 ms (+0–50 ms jitter), success not padded; unknown email vs wrong password within noise | `perf/login-floor.mjs` on the local compose stack (#306): 401 p50 532 ms / p95 552–557 ms for both paths, success p50 48–49 ms / p95 52–53 ms (ADR-0005) | `LoginTimingTests` (CI); the timing is reported |
| Seeder runtime | < 90 s local at scale 1.0 | the seeder's own timer | Reported |
| DB size | < 350 MB | the seeder (`pg_database_size`) | **Seeder fails** above 400 MB |
| Reseed peak (old + new data until COMMIT) | ≤ `--cap-mb` (default 512 MB) | the seeder (`SEED_PEAK_EST_MB`) | **Seeder refuses** before TRUNCATE (exit 2) |
| JS bundle (initial) | < 500 KB compressed | `web/scripts/bundle-budget.mjs` after `ng build`: Brotli q1 in the API's 16 KB writes (#252) | **CI fails** above budget |

Every PR that touches a measured path **MUST** paste before/after numbers in its body (the PR template has the table).

**Reseed peak and the storage cap (#109).**
- **Why a reseed peaks:** `TRUNCATE` inside the seed transaction keeps the old relfilenodes until `COMMIT`, so a reseed temporarily needs about old + new data. A local `--force` at scale 1.0 peaked at **533.8 MB** (`SEED_PEAK_EST_MB=542`, phase 1), over the 0.5 GB planning cap. Today a full book commits at 276–280 MB, so a full-book reseed estimates **~547–550 MB**. On a 512 MiB data directory the same run failed mid-COPY with SQLSTATE 53100 (data intact).
- **The guard:** before any destructive statement, a run that would TRUNCATE computes `SEED_PEAK_EST_MB` = current `pg_database_size` + the measured scale-1.0 size × scale. Above `--cap-mb` (default **512 MB**), or if the size can't be read (fail closed), it exits 2 and changes nothing. `--max-mb` stays the separate committed-size budget, checked before COMMIT.
- **At the 512 MB default, a full scale-1.0 reseed is refused on purpose.** A first seed into an empty database (~280 MB peak) passes; reseeding a full book doesn't. Neon announced 1 GB per Free project on 2026-10-02, but the default stays 512 MB until Leo confirms the project's real cap. Production passes it through the `production` environment variable `SEED_CAP_MB` (deploy, default 512) or the db-ops `cap_mb` input (reseed, default 512), §14.2/§14.3 (#224).
- **Runbook: before a `SeedVersion` or `SEED_SCALE` bump, or a db-ops reseed.**
  - **Estimate first.** Peak ≈ production's last `DB_SIZE_MB` (the deploy log's seed step, or `app.seed_metadata.database_size_bytes`) + 271 MB × scale. With production at 276 MB, a `SeedVersion` bump at scale 1.0 estimates ~547 MB, and any `SEED_SCALE` above ~0.87 crosses 512 MB.
  - **What a refusal looks like.** The seed step prints `SEED_PEAK_EST_MB=…` and `ERROR: reseed peak estimate is … MB …, over the 512 MB storage cap. Refused before TRUNCATE`, then exits 2. In **deploy** this happens *after* "Migrate Neon", so the Render deploy hook and the smoke test don't run and the deploy goes red. The previous data stays and the migrated schema stays backward compatible (§14.4). In **db-ops `reseed`**, the job fails with the same message and nothing changes. At the default cap, a db-ops reseed of a full book at scale 1.0 is always refused.
  - **Raise the cap first, when the estimate is over it:** Leo confirms the Neon cap, then Tech Coordinator sets the `production` environment variable `SEED_CAP_MB` (deploy) or dispatches db-ops `reseed` with a matching `cap_mb`. A seeder PR that raises `SeedOptions.DefaultCapMegabytes` also works. The wiring is #224 (Helms ruling on PR #217).
  - **If a bump was already refused:** set or raise `SEED_CAP_MB` (or revert the bump), then re-run Deploy from `main` via `workflow_dispatch` (Tech Coordinator, §14.2).
- **Stale local data:** dev databases seeded at `1.0.0` by earlier PR heads report the same version, so `--if-changed` skips and keeps the stale rows. Reseed them locally with `--force` (add `--cap-mb 1024` at scale 1.0). Never `--force` against Neon or production.

---

## 11. Testing

- **API integration tests** (`tests/Desk.Api.Tests`, xUnit + **Testcontainers Postgres 17**, seeded at `--scale 0.1` with `SEED=42`):
  - The grid whitelist drops unknown and malicious identifiers. Values are parameterized (inspect the generated SQL).
  - **Paging stability:** concatenating all blocks gives every id exactly once, under any sort.
  - **Summary row = independent SQL aggregate** over the same filter.
  - The LATERAL query keeps zero-bond deals. The aggregation grain is correct (no fan-out double counting).
  - **Shared DbContext across `Task.WhenAll` throws** (`InvalidOperationException`), and the per-task factory version passes.
  - Fund performance month counts per range. Mid-month inception.
  - **Auth:**
    - no session gives 401
    - an expired account is refused
    - lockout after 5 failures
    - the rate limiter returns 429 with `Retry-After`
    - maintenance mode returns 503 without opening a DB connection (assert on the connection counter)
  - Cache: HIT on repeat; 304 with `If-None-Match`.
- **Unit tests:** `GridQueryBuilder` SQL snapshots; column catalog to format mapping; weighted-average edge cases (zero/NULL weights give `null`).
- **Web unit tests (Vitest):** `toRows` columnar mapping; P2 column-def builder; number formatting (negatives, precision, colorblind palette); the debounced, `switchMap` query service (fake timers).
- **E2E (Playwright, against `docker compose`):**
  - log in
  - P1: scroll to the last row, then check the summary row; preset switch triggers a re-request with the new columns; quick filter fires exactly one completed request
  - P2: range switch
  - P3: progressive render with one delayed source; error isolation
  - P4: zero-bond deals visible
  - **Screenshots of every page in dark and light themes** saved to `docs/screenshots/` and attached to the PR.

---

## 12. Local development

**Prerequisites:** .NET 10 SDK, Node 22 LTS, Docker.

```bash
cp .env.example .env                       # .env is gitignored; never commit it
sed -i.bak "s/^POSTGRES_PASSWORD=$/POSTGRES_PASSWORD=$(openssl rand -hex 16)/" .env && rm .env.bak
set -a; . ./.env; set +a                   # load POSTGRES_PASSWORD / DATABASE_URL into this shell
docker compose up -d postgres              # Postgres 17 on localhost:5432 (db creditdesk, password from .env)
dotnet tool restore
dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data   # apply migrations
dotnet run --project src/Desk.Seeder -- --if-changed --scale 1.0                    # seed (skips if current)
dotnet run --project src/Desk.Api          # http://localhost:5180 (/health, /api)
cd web && npm ci && npm start              # http://localhost:4200 (proxies /api and /health to :5180)
```

The tools read `DATABASE_URL` (or `ConnectionStrings__<Source>`) from the environment. There's deliberately **no built-in default connection string**: credentials only ever come from `.env` locally, from CI configuration, or from the production secret. Both Npgsql keyword strings and `postgres://` URIs work. Creating accounts (`Desk.UserAdmin`) arrives in phase 2.

- **Agent sessions and credentials:** never export a Neon or production connection string in a shell that runs an agent session (Claude Code, Cursor, Grok). The human exports only local compose values for a session; tests use Testcontainers (ADR-0020 OQ-12).

Or the whole stack in containers: `docker compose up --build` gives the app on http://localhost:8080. Migrate and seed from the host as above; the app never migrates at startup.

**`.env.example`** (key names only):

```dotenv
DATABASE_URL=
ConnectionStrings__Core=
ConnectionStrings__Market=
ConnectionStrings__Surveillance=
ConnectionStrings__Pricing=
ConnectionStrings__Reference=
ConnectionStrings__App=
SEED_DEMO=false
SEED=42
SEED_SCALE=1.0
MAINTENANCE_MODE=false
RATE_LIMIT_PER_USER_PER_MIN=60
RATE_LIMIT_GLOBAL_CONCURRENCY=8
DEMO_ACCOUNTS_JSON=
ASPNETCORE_ENVIRONMENT=Development
```

---

## 13. Deployment: Render + Neon

### 13.1 Neon

- One project, one database, Postgres 17.
- Use the **direct** connection endpoint, not `-pooler`, for the app. The API keeps its own Npgsql pool, and migrations need session features.
- The pool is capped explicitly: `Maximum Pool Size` = `DB_MAX_POOL_SIZE` (1–100, default 20; anything else falls back to 20, so a typo can't lift the cap), set on every connection string `IDataSourceRegistry` resolves (§5.1). Npgsql's own default of 100 is above what a small Neon compute accepts (`max_connections` scales with compute size). 20 covers the §7.2 request path (8 concurrent DB permits, up to 15 connections with the insights fan-out; start-up warns when it doesn't fit) plus background users (audit writer, DataProtection key reads), and two instances overlapping during a deploy (2 × 20) still leave room for migrations and manual sessions. It replaces any `Maximum Pool Size` (`MaxPoolSize`) already in a connection string, like `Command Timeout`; a `Minimum Pool Size` above the cap fails the first query. Rationale: ADR-0002, *Connection budget*.
- Connection string format: `Host=…;Database=…;Username=…;Password=…;SSL Mode=Require;Trust Server Certificate=false`. It's stored **only** in the Render dashboard as `DATABASE_URL` (`sync: false`).
- **Expect autosuspend:** the first query after idle may take about 0.5–1 s extra. Cache-first reads keep this rare.

### 13.2 Render (one Docker web service)

`render.yaml`:

```yaml
services:
  - type: web
    name: credit-desk-analytics
    runtime: docker
    dockerfilePath: ./deploy/Dockerfile
    plan: free            # bump to "starter" for always-on during a demo window
    region: virginia
    healthCheckPath: /health
    autoDeploy: false     # deploys are triggered by GitHub Actions (deploy hook) after migrations, see §14.2
    envVars:
      - key: DATABASE_URL
        sync: false
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
      - key: SEED_DEMO
        value: "false"
      - key: MAINTENANCE_MODE
        value: "false"
      - key: CORS_ALLOWED_ORIGINS   # the GitHub Pages front end (ADR-0024); unset = no cross-origin access
        value: https://llevintza.github.io
      - key: DEMO_ACCOUNTS_JSON
        sync: false
```

- **`deploy/Dockerfile`**, multi-stage:
  1. `node:22` builds `web/` (`npm ci && npm run build`).
  2. `mcr.microsoft.com/dotnet/sdk:10.0` publishes `Desk.Api` and copies the SPA into `wwwroot`. The **EF migrations bundle** is built in GitHub Actions (`deploy.yml` / `db-ops.yml`), not in this image (ADR-0016).
  3. `mcr.microsoft.com/dotnet/aspnet:10.0` is the runtime, as a non-root user. `ASPNETCORE_HTTP_PORTS=${PORT:-8080}` via `start.sh`.
- **`deploy/start.sh`** only starts the app: it fails fast with a clear message if `DATABASE_URL` is missing, then runs `exec dotnet Desk.Api.dll`.
  - **Migrations and seeding are not done at boot.** GitHub Actions runs them before triggering the deploy (§14.2).
  - That way a cold start on the free tier never runs DDL, and a failed migration never reaches a running container.
- **Free-tier realities:**
  - The instance spins down after about 15 min idle, and the next request takes about 30–60 s. The login page's "Waking the server…" state covers it.
  - Monthly free instance hours are limited, so **no keep-awake pinger.**
  - For a scheduled demo or review window, switch `plan` to `starter` temporarily.
- **`/health` returns the build's git SHA** (`{"status":"ok","version":"<sha>"}`, baked in with a Docker build arg), so the pipeline can confirm the new build is live.

### 13.3 One-time setup (done by hand by the repo owner)

**Detailed, click-by-click guide: [`docs/deployment-setup.md`](docs/deployment-setup.md)** (Neon, Render, GitHub protections, then production secrets, the Claude review environment, the first deploy, rotation and troubleshooting). The summary:

1. **Neon:**
   - Create project `credit-desk-analytics` (Postgres 17, region close to Render's).
   - Copy the **direct** connection string.
2. **Render:**
   - New → Blueprint → this repo (`render.yaml`).
   - Set `DATABASE_URL` in the dashboard.
   - Copy the service's **Deploy Hook URL** (Settings → Deploy Hook).
3. **GitHub protections first** (before any production secret):
   - Environment **`production`:** deployment branch `main` only. **No required reviewers** — none exists that could approve, and merges to `main` auto-deploy. Controls are the pre-merge review gate, required status checks (once the main ruleset is active), `deploy.yml` migrate/smoke, and README §14.4.
   - Ruleset on `main` (once active): required status checks: every CI job except `review` (`secrets`, `api`, `web`, `coverage`, `compose-smoke`, `workflows`, `db-tools`, `gate-tests`). No required approving review (every bot acts as `llevintza` and cannot self-approve). No force-push or deletion of `main`.
4. **Then** add the `production` environment secrets (not `APP_URL` yet):

   | Kind | Name | Value |
   |---|---|---|
   | secret | `NEON_DATABASE_URL` | Neon direct connection string (Npgsql format) |
   | secret | `RENDER_DEPLOY_HOOK_URL` | Render deploy hook URL |
   | variable | `SEED_SCALE` | `1.0` |
   | variable | `SEED_CAP_MB` (optional) | reseed peak cap in MB; unset means 512 (§10, #224). Set it only after Leo confirms the Neon cap |

5. **Claude review (Leo's decision, 2026-10-07):** environment **`claude-review`** (no branch restriction, no required reviewers) holding spend-capped `ANTHROPIC_API_KEY`. Never `production`, never a repository secret. Same-repo PRs use this key; forks and Dependabot skip. The `review` check stays non-required.
6. **Last:** after Tech Coordinator's go-ahead, set `production` environment variable `APP_URL` to the service URL (no trailing slash). That variable turns deploys on.
7. **Merge to `main`.** The deploy workflow migrates, seeds, deploys and smoke-tests.
8. **Create reviewer accounts** with the UserAdmin CLI locally, pointed at Neon (§7.1). Share credentials out of band.

---

## 14. CI/CD

### 14.1 CI: `.github/workflows/ci.yml`, on every PR and on `main`

1. **api:**
   - `dotnet build -warnaserror`
   - `dotnet test` with **coverlet.MTP** Cobertura (Testcontainers needs Docker, available on the `ubuntu-24.04` runner). Generated OpenAPI / `obj` sources are excluded (`GeneratedCodeAttribute`, `**/obj/**`, `**/*.generated.cs` in `tests/testconfig.json` and the same `--coverlet-exclude-by-file` flags). Do **not** exclude `CompilerGeneratedAttribute` (that drops `Program.cs` lambdas). Do not use `dotnet test --collect "XPlat Code Coverage"` (VSTest collector; this repo is MTP).
   - `dotnet ef migrations has-pending-model-changes` must be false
2. **web:**
   - `npm ci && npm run lint && npm test -- --watch=false --coverage && npm run build`
   - Vitest coverage via `@vitest/coverage-v8` (lcov + text-summary)
   - `npm test` first runs `test:tooling`: `node --test` with `--experimental-test-coverage` over `web/scripts` and `web/eslint-rules` at **≥80% line and branch** (#317); the report prints in the web job log
   - the README §10 bundle budget is enforced by `web/scripts/bundle-budget.mjs`, run by `npm run build` after `ng build`: the Brotli size (q1, in the API's 16 KB writes, #252) of the initial JS + CSS that `index.html` loads, plus every file those reach through a static `import`/`export … from` or CSS `@import`, followed transitively (#204, #259); exit 1 above 500 KB, and it fails closed when it can't measure (#145). The raw-size `angular.json` budgets remain a second guard
3. **coverage:** job summary of line/branch % per project, with covered/total counts in each cell, plus a **slack** line per suite (#227): how many covered lines or branches can be lost before the no-drop check fails, and how many more make a baseline bump due; informational, no rule change. It also shows the coverlet scope (exclusions); **diff coverage ≥ 80%** vs the merge-base with the PR base; **overall % must not drop** vs `perf/coverage-baseline.json` at BASE_SHA. **Precision and tolerance (#318):** the no-drop check compares the measured percentage at full precision (covered/total, unrounded) with the 1-decimal floor, and allows a dip of at most `baselineMatchTolerancePercent` (0.1pp; the gate rejects any value above 0.1) **in the drop direction only**: with a 98.8% floor, 98.74% passes and 98.6% fails. The committed baseline may never sit below the floor (no tolerance there). It must match measured within the same tolerance in either direction, compared at measured precision, so run-to-run jitter neither fails the gate nor forces a baseline edit, while a real improvement of more than 0.1pp (e.g. 98.92% vs a committed 98.8) still has to be committed. The tolerance applies to line and branch for both suites. The summary prints covered/total counts per suite. CI checks out BASE_SHA into `_base` (`fetch-depth: 0`, `persist-credentials: false`) and **always** runs **`_base/perf/coverage-gate.mjs`**. There is no HEAD fallback and no `_default` checkout. A BASE_SHA without the gate (retarget, stale base, deleted gate, rewound main) **fails closed** before `node` — bootstrap is closed after #5 (`741b19eb`); rebase onto main. Thresholds, tolerance, and the floor are read from BASE_SHA (`git show $BASE_SHA:…`), never from the PR head or the pushed commit. Exact JSON schema; NaN-safe comparisons (`!(actual >= floor)`); schema failures are written to the log and step summary. A PR that lowers a min, turns off `overallMustNotDrop`, or widens the tolerance fails. PRs whose base is not the default branch fail closed. `pull_request` `edited` re-runs the gate (retarget). Empty / zero / unknown `github.event.before` **fails closed**. On `push` to main, BASE_SHA is `github.event.before`; the gate runs from `_base` with `--default-dir _base --default-sha "$BASE_SHA"`; empty `--base-ref` is empty (not `"true"`); the retarget check runs only on `pull_request`. A baseline may be lowered **ONLY for a documented change in measurement scope**, never to absorb a real coverage drop. Any lowering must be its own `[workflows]` PR with `perf/coverage-override.json` `{from, to, reason}` (applied only when that file differs from BASE_SHA; checked against the BASE_SHA floor and measured numbers) and sign-off from Code Reviewer, Tech Coordinator and Helms. A PR may raise the committed baseline to match measured coverage. A missing base SHA, merge-base, or `git show`/`git diff` error **fails closed**. Changed `src/` or `web/src` files with no coverage data count as 0% toward the diff gate (never skipped). Thresholds live in `perf/coverage-thresholds.json`.
4. **compose-smoke:** `docker compose up -d --build` and the same `/health` + `/` + `/api/me` checks the deploy smoke test runs
5. **secrets:** gitleaks over the branch history (`--log-opts=HEAD`)
6. **workflows:** actionlint + shellcheck
7. **db-tools:** `.github/actions/build-db-tools` on a clean checkout (no prior `dotnet restore`/`dotnet build`, no secrets, no production environment, no DB). Asserts `dbtools/efbundle` and `dbtools/seeder/Desk.Seeder`. The `api` job also uses this action, but only after `dotnet build`, which does not catch a missing restore on deploy/db-ops.
8. **gate-tests:** `node --test --experimental-test-coverage` on `perf/coverage-gate.mjs` at **≥80% line and branch**. It also runs `.github/scripts/concurrency-wiring.test.mjs`, which asserts the CI and Deploy concurrency wiring below (#207) and that every workflow job runs on `ubuntu-24.04` (#321). `.github/scripts/ci-image-pins.test.mjs` (#255) checks every container image a workflow runs (`docker run`, `image:`, `uses: docker://`). Each must be pinned by digest and match the tag and digest of its Dependabot-tracked pin, `deploy/ci-images/<name>/Dockerfile` or, for the `api` job's postgres service, `docker-compose.yml`, after stripping the `mirror.gcr.io`/`docker.io`/`library/` prefixes (ADR-0025). Each ci-images Dockerfile also needs its Dependabot `directories:` entry. A bump on one side only fails `gate-tests`.
9. **budgets:** the compose stack migrated and seeded at scale 1.0, with a throwaway account (generated password, masked). `node perf/payload-size.mjs` fails above the Risk first-block budget and warns above All (README §10), and fails when the largest P2 response (fund 1 ITD, `GET /api/funds/1/performance?range=ITD`) is 5 KB or more on the wire (README §6). It fails closed (exit 2, #136): every call must be a 200 whose body decodes (br/gzip) and parses, and each P1 block must carry the requested columns + `id` with 200 rows in every column and `rowCount` ≥ 200, so a short or empty 200 can't print `ok`. Its tests (`perf/payload-size.test.mjs`, a stub API) run in `gate-tests`. Then `perf/LastBlockCheck` (#43 AC4): with ≥ 18,000 rows, the final 500 ids of the Risk view sorted by `market_value` desc and every summary SUM must equal independent SQL. Any mismatch exits 1 and fails the job. Then k6 (`perf/positions.js`, `grafana/k6` pinned by digest, #135) on the same stack, with the per-user rate limit raised for that run's app only (a compose override in the job; never `render.yaml` or the production defaults): p50/p95/max for the worst-case MISS (Risk and All presets, the whole book under a never-asked filter) and the HIT (`X-Cache: HIT` asserted) go to the step summary against 150 / 15 ms, with OVER marked. The step is `continue-on-error` until #87 makes it required: a breach fails the step and annotates the run, but not the job.
10. **e2e:** the compose stack (with `e2e/docker-compose.e2e.yml`: higher per-user limits for one automated user) migrated and seeded at scale 0.2, a throwaway viewer (generated, masked password), then Playwright: log in, the P1 acceptance flows, no console or CSP errors, and dark/light screenshots of every page. The report and screenshots are uploaded as artifacts.

**Per-area runs (#169, [ADR-0023](docs/adr/0023-per-area-ci-jobs.md)):** on `pull_request`, a `changes` job runs `.github/scripts/ci-changes.mjs` **from the BASE_SHA checkout** over `git diff --name-only --no-renames BASE...HEAD` and sets one flag per area. Docs (`*.md` anywhere except under `src/` and `web/src/`, where only `AGENTS.md`/`CLAUDE.md` count; `docs/**`; `.claude/skills/**`) set none. Shared triggers (`.github/**` other than Markdown, `Directory.*.props`, `global.json`, `dotnet-tools.json`, `*.sln`/`*.slnx`, lockfiles, Docker/compose files, `perf/coverage-*`, `tests/testconfig.json`, `.gitleaks.toml`), any path that matches no area, and an empty diff set every flag.

| Area | Paths | Jobs it runs |
|---|---|---|
| `api` | `src/Desk.Api/**`, `src/Desk.Data/**`, `src/Desk.UserAdmin/**`, `tests/Desk.Api.Tests/**`, `perf/*/**` (the .NET benchmark projects in `CreditDesk.slnx`), `deploy/start.sh` (linked into `Desk.Api.Tests`) | `api`, `coverage` (.NET) |
| `web` | `web/**` | `web`, `coverage` (web) |
| `db` | `src/Desk.Data/**`, `src/Desk.Seeder/**`, `tests/Desk.Data.Tests/**`, `tests/Desk.Seeder.Tests/**` | `api` (all .NET suites, migrate, seed), `db-tools`, `coverage` (.NET) |
| `app` | `deploy/**`, `e2e/**`, `render.yaml`, plus any `api`, `web` or `db` change | `compose-smoke`, `e2e`, `budgets` |
| `perf` | `perf/**` | `budgets` |

`secrets`, `workflows` and `gate-tests` always run. Jobs are skipped by a job-level `if:` (never a workflow-level `paths` filter), so a skipped job keeps its exact name and still reports. A job skips only when its flag is exactly `false`: on `push` to `main`, on a base without the classifier, or when `changes` fails, the flags are empty and **every job runs**. Such a skip is not a "skipped" suite under gate clause 1; any other skip is (Code Reviewer checks the `changes` job summary). Clause 1 still applies in full to every job that runs. `coverage` passes `--suites` with the suites that ran; a suite that did not run is skipped by the gate, not passed on stale data, and a suite that ran without coverage data, or a skipped suite whose sources changed, fails closed.

**Image pulls (#332, [ADR-0025](docs/adr/0025-ci-image-pulls-without-docker-hub-limits.md)):** CI makes no anonymous Docker Hub pull on a normal run, and every image stays pinned to the same digest. gitleaks and k6 come from their publishers' ghcr.io images. Jobs that pull other Docker Hub images (`workflows`, `api`, `compose-smoke`, `budgets`, `e2e`) first run `.github/actions/docker-hub-mirror`, which adds `mirror.gcr.io` as the runner's registry mirror, with Docker Hub as the fallback. The `api` job's postgres service starts before any step, so it references `mirror.gcr.io/library/postgres` at the same digest directly. No registry credentials are used.

**Runner image (#321):** every job in every workflow (CI, Deploy, db-ops, gitleaks, Pages, Claude review) runs on the pinned `ubuntu-24.04` image, never the moving `ubuntu-latest` label, which GitHub moves to Ubuntu 26 from 2026-10-19. A runner never changes under a job without a change in this repository. Moving to Ubuntu 26 (`ubuntu-26.04`) is a deliberate `[workflows]` PR, made only after a verification run of the full CI on that image (.NET, Node, the Postgres service container, Playwright e2e, k6, gitleaks and the db-tools build all green, with no coverage change) recorded in the PR. Re-evaluate by 2027-04-01, or sooner if GitHub announces a retirement date for `ubuntu-24.04`. `gate-tests` fails on any job that is not on `ubuntu-24.04`.

**Concurrency (#207):** each `push` run has its own group (`ci-push-<sha>`) and is never cancelled, so every commit that lands on `main` gets a full CI run even when merges land back to back. `pull_request` runs share one group per PR number (`ci-pr-<number>`, never the ref) with `cancel-in-progress`, so a new push to a PR cancels the superseded run. A merged PR's `edited` event resolves to `refs/heads/main`; keyed by PR number, it can't touch a `main` run. A shared group per ref is not safe on `main`: GitHub keeps only one pending run per group, and a newer pending run cancels the older one before any job starts.

Nothing deploys from PR branches. `deploy.yml` additionally refuses a `workflow_run` unless the triggering CI run was a **`push` to `main` on this repository**, and refuses `workflow_dispatch` unless the ref is exactly `refs/heads/main` (case-sensitive bash; GitHub `==` is not). A PR whose head branch is named `main` is not a deploy. The SHA being deployed **MUST** equal the current tip of `main`, so re-running an old CI or deploy run cannot roll production back.

### 14.2 CD: `.github/workflows/deploy.yml`, on push to `main` (after CI passes)

Triggered by `workflow_run` of CI on `main` with `conclusion == success`, or by `workflow_dispatch`. The `workflow_run` `branches: [main]` filter matches the triggering run's **head branch**, so jobs that use the `production` environment (and `DATABASE_URL`) also require:

- **`workflow_run`:** `event == push` **and** `head_branch == main` **and** `head_repository.full_name == github.repository` **and** `conclusion == success`
- **`workflow_dispatch`:** ref is exactly `refs/heads/main` (case-sensitive bash in the no-secrets `gate` job; GitHub's expression `==` is case-insensitive)
- **SHA:** `workflow_run.head_sha` or `github.sha` equals the current tip of `main` (re-runs of old successful CI/deploy runs are refused)

`DATABASE_URL` is injected only on the migrate and seed steps. A failing migrate/seed command fails the step (`defaults.run.shell: bash` enables `pipefail`, so `cmd | tee` does not swallow the command's exit code). Deploy's `release` job and db-ops' `run` job, the two jobs that reach Neon, share `concurrency: group: production` with `cancel-in-progress: false`. An **in-progress** job is never cancelled. GitHub keeps a single pending job in the group, so a **newer pending one cancels the older pending one**. The group therefore sits on those two jobs only, never on a workflow and never on Deploy's `gate` or `preflight` (#207, #242), and each run enters it **once**, after its own checks:
- **Deploy vs Deploy:** a Deploy for a SHA that is no longer the tip ends at the gate and never enters the group. Only a tip's `release` can be pending. A newer tip's `release` can replace it, and then the newer tip deploys. preflight (which only checks that three values are set) is outside the group, so a run's own `release` can't replace the tip's pending preflight.
- **db-ops vs Deploy:** db-ops' `gate`, outside the group, refuses with an error, and nothing runs, while any Deploy run isn't `completed` (queued, waiting, pending or in progress). It reads the run list with `actions: read` and fails closed if the list can't be read. A db-ops dispatch therefore never becomes the pending job that replaces the tip's `release`. The residual is a narrow race: a Deploy that starts in the seconds between db-ops' check and its `run` job queueing. A Deploy that queues while db-ops is pending replaces db-ops, never the reverse, so the tip still deploys. Dispatch db-ops again afterwards.
- GitHub's `concurrency.queue: max` (a FIFO of up to 100 pending) would remove even that window, but the pinned actionlint 1.7.12 rejects the key. Adopting it is a later `[workflows]` change once actionlint supports it.

A SHA that is no longer the tip of `main` is skipped, not failed. The run still concludes success, so the gate writes a `::warning::` and a **"Deploy skipped: <sha> is not the tip of main"** job summary saying nothing was migrated, seeded or released. `release` re-checks the tip as its first step: if main moved while it waited in the group, it writes the same `::warning::` and summary, and every later step is gated on that check. `concurrency-wiring.test.mjs` asserts all of this, including the exact CI trigger set and the absence of job-level groups in CI (#243).

| Job | Steps |
|---|---|
| **1. db-tools** | `.github/actions/build-db-tools`: NuGet restore for `linux-x64`, then the **EF Core migrations bundle** (`dotnet ef migrations bundle --self-contained -r linux-x64`) and the **seeder** (`dotnet publish src/Desk.Seeder -c Release -r linux-x64 --self-contained`). `dotnet tool restore` is not a package restore. A password-less design-time `DATABASE_URL` is set only while bundling; production `DATABASE_URL` stays on the migrate/seed steps. |
| **2. migrate** | Run the bundle against `NEON_DATABASE_URL`. A no-op when current. A failure **stops the deploy**: the running app keeps serving the old schema. |
| **3. seed** | Run `Desk.Seeder --if-changed --scale $SEED_SCALE --cap-mb $SEED_CAP_MB`. It compares the seed **version** (a constant in the seeder, bumped whenever the generator or schema changes) and the scale with `app.seed_metadata`, and does nothing when they match. When they differ, it truncates and reloads all seeded tables in one transaction; the metadata row is written in the same transaction after a pre-commit size guard. The step prints the DB size and fails over budget (§5.4). **Before it truncates, a reseed is refused (exit 2, nothing changed) when `SEED_PEAK_EST_MB` exceeds `--cap-mb`.** The cap is the `production` variable `SEED_CAP_MB` (default 512; the step fails before the seeder if it isn't a whole number 1–999999). A skip never checks the cap. A refusal comes after migrate, so deploy and smoke don't run. **Operator path:** before a `SeedVersion` or `SEED_SCALE` bump, estimate the peak (§10 runbook) and confirm the Neon cap; if the estimate is over the cap, set `SEED_CAP_MB` (or raise `SeedOptions.DefaultCapMegabytes`) first. If a deploy was refused, set or raise it (or revert the bump), then Tech Coordinator re-runs Deploy via `workflow_dispatch`. |
| **4. deploy** | `curl -fsS -X POST "$RENDER_DEPLOY_HOOK_URL"` triggers Render to build the Dockerfile at this commit. |
| **5. smoke** | Poll `$APP_URL/health` (up to 15 min, every 15 s; the free tier builds slowly and cold-starts) until `version` equals `github.sha`. Then `GET /` returns 200 HTML, and `GET /api/me` returns 401 (auth enforced). The workflow summary shows URL, version, migration list, seed action (skipped / reseeded) and DB size. |

### 14.3 Manual database operations: `.github/workflows/db-ops.yml` (`workflow_dispatch` only, `production` environment)

Inputs:
- `operation`: `migrate` | `reseed` | `size-report`
- `scale`: default `1.0`
- `cap_mb`: reseed peak cap in MB, default `512`. The Guard step fails unless it's a whole number 1–999999, before any database step; it reaches the seeder only through `env` (`--cap-mb "$CAP_MB"`)
- `confirm`: must equal `RESEED-PRODUCTION` for `reseed`; otherwise the job fails before touching the database

Dispatch is refused unless the run is from exact `refs/heads/main` (case-sensitive) at the current tip of `main`. `DATABASE_URL` is injected only on the step that talks to Neon. It is also refused while a Deploy run is in flight (#242, §14.2), so a dispatch can't cancel a pending deploy; dispatch again when it finishes.

`reseed` drops and reloads the synthetic data (never the `auth` schema or user accounts), then updates `app.seed_metadata`. **A reseed is refused (exit 2) before TRUNCATE when `SEED_PEAK_EST_MB` exceeds `cap_mb`**, and nothing changes. At the 512 default a full book at scale 1.0 (~547 MB estimated) is always refused. Operator path: confirm the Neon cap, then dispatch with a `cap_mb` at or below it that covers the estimate (§10 runbook).

### 14.4 Migration rules (because migrations run *before* the new app version starts)

- **Every migration MUST be backward compatible with the currently running app** (expand → deploy → contract):
  - Add columns as nullable or with defaults.
  - Never rename or drop in the same release that stops using a column; drop in a later PR.
- **Migrations are generated, reviewed and committed** in the PR that needs them. CI fails on pending model changes.
- **Seed data is never written by migrations,** only by the seeder. The exception is the column catalog, which is reference data; the seeder owns it too.

### 14.5 Code review: `.github/workflows/claude-review.yml`, on every PR push

- Claude reviews the diff against AGENTS.md and this spec, and posts inline **[blocking]** / **[suggestion]** comments.
- It ends with a summary comment whose first line is `<!-- claude-review sha=<head sha> blocking=<n> -->`. That marker is the model's own count. Any workflow running as `github-actions[bot]` can post it.
- Before the action runs, the job removes planted `.review-base` / `.review-pr` / `.review-context` dirs, removes symlinks outside `.git`/`.review-base`, overlays the base `AGENTS.md` and `README.md` on the working tree (so `CLAUDE.md`'s `@AGENTS.md` import cannot load the PR head), deletes nested `CLAUDE.md` / `AGENTS.md` files and nested `.claude/` dirs, and copies base `.claude` / top-level `CLAUDE.md`. Checkout credentials are not persisted; `Read`/`Grep`/`Glob` of `.git/**` (and `.review-base/.git/**`, `.review-pr/.git/**`) are denied (the action still writes its own job token into `.git/config`). The job is **advisory and must not be a required check**. It skips with a notice when the key is missing. `cursor[bot]` (agent pushes) is allowed via `allowed_bots`; forks, Dependabot and other bots skip.
- **Leo's decision (2026-10-07):** `ANTHROPIC_API_KEY` lives in a dedicated GitHub environment **`claude-review`** (spend-capped key; no branch restriction; no required reviewers). Never the `production` environment, never a repository secret. Same-repo PRs use this key; forks and Dependabot skip. It is unknown whether the Claude GitHub App is installed; the workflow does not need it (it passes `github_token`).

**The Claude review is advisory.** Its `blocking=<n>` is the model's own count, and any workflow running as `github-actions[bot]` can post the marker, so it never decides a merge.

The review gate (Tech Coordinator plus Code Reviewer; Claude's review is advisory only):
1. Every suite (API xUnit, web Vitest, compose smoke) passes in CI on the PR head, with nothing skipped, disabled or weakened.
2. coverlet and Vitest coverage are collected and published in CI, with the numbers in the PR summary; ≥80% on new or changed code; main never drops. Missing coverage means REQUEST CHANGES.
3. Any workflow, action, Dockerfile, render.yaml, `perf/coverage-*`, `tests/testconfig.json`, or `.gitleaks.toml` change gets governance review: SHA-pinned actions, least-privilege permissions, secrets only in the `production` environment (sole exception: the capped Claude key in `claude-review`), no unsafe `pull_request_target`, gitleaks stays on, nothing removed or loosened.

Tech Coordinator merges and starts the next phase.

Per-area CI ([ADR-0023](docs/adr/0023-per-area-ci-jobs.md)): a heavy job skipped because the base-sourced `changes` classifier reported its flag as exactly `false` was not affected by the diff, and is not "skipped" under clause 1. Any other skip (a failed or cancelled dependency, a missing classifier output, a disabled step) is. Code Reviewer checks the `changes` job summary. Clause 1 still applies in full to every job that runs.

**Known limit:** every bot acts as `llevintza`, so GitHub can't require an approving review and CODEOWNERS is advisory only. The `[workflows]` title prefix is also advisory only: no protection enforces it. The control is process: only Tech Coordinator (or Leo) merges. Same-repo PRs can edit `claude-review.yml` and use the `claude-review` key; accepted because the review is advisory and the key is dedicated and spend-capped. Forks and Dependabot skip. `cursor[bot]` (agent pushes) is allowed via `allowed_bots`; other bots skip.

### 14.6 Front end on GitHub Pages: `.github/workflows/pages.yml`, after CI passes on `main` (or `workflow_dispatch`)

The same SPA that Render serves from `wwwroot` is also published to `https://llevintza.github.io/credit-desk-analytics/` and calls the Render API cross-origin with bearer tokens (ADR-0024). It is gated on CI like the Render deploy (§14.2): `workflow_run` of CI with `conclusion == success`, for a `push` to `main` in this repository. A `gate` job (no environment, no secrets) then checks that the commit is still the tip of `main`, and `build` checks out exactly that commit. A green run for an older commit is skipped with a summary, because the tip's own run publishes. A dispatch is allowed from `refs/heads/main` only. It republishes after every green `main` push, not only `web/` changes; that is cheap and idempotent. It doesn't wait for the Render deploy: the static shell is independent of the API version, and the API keeps accepting the previous shell's calls.

| Job | Steps |
|---|---|
| **gate** (`contents: read`) | Event, branch, repository and exact-tip checks, as in `deploy.yml`. |
| **build** (`contents: read`, no environment, no secrets) | Checks out the gated commit. Checks the repository variable `APP_URL`: an `https://` origin with no path, or the run fails. Then `npm ci`, and `ng build --base-href /credit-desk-analytics/ --define DESK_API_BASE_URL="<APP_URL>"`. Adds a `<meta http-equiv="Content-Security-Policy">`, because Pages can't send headers: the app's CSP with `APP_URL` in `connect-src`. Copies `index.html` to `404.html`, so deep links open the router. Then `actions/upload-pages-artifact`. |
| **deploy** (`pages: write`, `id-token: write`, environment `github-pages`) | `actions/deploy-pages`. Nothing else runs with these permissions. |

- **Concurrency:** `group: pages`, `cancel-in-progress: false`. A deployment is never cut off, and the newest pending run wins.
- **Set by hand** (agents can't change settings): Settings → Pages → Source = **GitHub Actions**; a **repository** variable `APP_URL` (not a secret). The `production` environment's `APP_URL` can't be read by a job outside that environment, and this workflow stays outside it. On Render, `CORS_ALLOWED_ORIGINS` must include `https://llevintza.github.io` (README §7.1, `render.yaml`).
- **One origin for the whole account** (accepted by Leo, 2026-10-11, ADR-0024): every Pages site under `https://llevintza.github.io` (today also `/energlens`) shares this app's origin. That means each one passes the API's CORS check and can read the tab's `sessionStorage`, refresh token included. Rule: **no Pages site on the account loads untrusted third-party scripts.**
- **Limits on Pages:** no response headers, so no HSTS from us, no `frame-ancestors` (a meta CSP ignores it) and no `Permissions-Policy`. A deep link's document is served with HTTP 404 (the `404.html` fallback), and the app then routes normally.

---

## 15. Delivery plan: one PR per phase

**The commit and PR history is part of the deliverable.** It shows the reasoning and the measured evaluation behind each technology choice, especially the front end, EF/DbContext, Dapper, DTO shaping, SQL and large-grid loading.

| PR | Branch | Scope | ADRs (in `docs/adr/`) | Definition of done |
|---|---|---|---|---|
| 1 | `docs/spec` | This README, AGENTS.md, ADR + PR templates | n/a | Reviewed and merged |
| 2 | `phase-0/scaffold` | Solution + projects, Angular workspace, compose, `.env.example`, **Dockerfile, render.yaml, CI + deploy + db-ops workflows**, initial migration (`app.seed_metadata`), seeder `--if-changed` skeleton | **0001** stack; **0002** hosting (single Render service + Neon vs static site + API vs GitHub Pages); **0016** CD: Actions-driven migrate → seed → deploy hook → smoke | `docker compose up` serves a placeholder page and `/health`; CI green; **after the §13.3 setup, merging deploys a live placeholder whose `/health` reports the merged SHA** |
| 3 | `phase-1/data` | Schemas, EF migrations, column catalog, deterministic seeder, size check | **0003** wide snapshot + narrow history; **0004** COPY vs EF `AddRange` (**measured**) | Seed < 90 s; DB < 350 MB; edge cases present (tests) |
| 4 | `phase-2/auth-and-limits` | Identity, login, UserAdmin CLI, rate limiting, maintenance mode, audit, security headers | **0005** same-origin cookie vs JWT | All auth tests in §11 pass |
| 5 | `phase-3/positions-api` | Grid query builder, P1 endpoints, cache/ETag, export | **0006** Dapper vs EF for the dynamic grid (**measured**: ms, allocations); **0007** row JSON vs columnar vs MessagePack (**measured**: bytes, parse ms); **0008** offset vs keyset paging | P1 API tests pass; payload budget met |
| 6 | `phase-4/shell-and-positions-ui` | App shell, theme, login, P1 grid, presets, status bar, keyboard | **0009** Infinite Row Model + displayed-columns requests vs client-side model (**measured**: first paint, memory); **0010** signals + OnPush + zoneless | P1 e2e + screenshots; first-paint budget |
| 7 | `phase-5/fund-performance` | P2 API + UI | **0011** long format + edge pivot vs SQL pivot | P2 tests + screenshots |
| 8 | `phase-6/insights-board` | P3 per-source endpoints, parallel per-task contexts, progressive UI | **0012** per-source endpoints + per-task DbContext vs 20 calls vs one call (**measured**) | Progressive-render and isolation e2e |
| 9 | `phase-7/deal-explorer` | P4 | **0013** LATERAL vs ROW_NUMBER (`EXPLAIN ANALYZE`) | Zero-bond and grain tests |
| 10 | `phase-8/performance-lab` | P5 | n/a | Real measurements shown, copyable |
| 11 | `phase-9/hardening` | Production hardening: security headers review, rate-limit tuning, cache sizing, cold-start UX, budgets measured on the live site | **0014** free-tier guardrails | Budgets checked on the live site; reviewer accounts issued |
| 12 | `phase-10/intraday-overlay` (stretch) | P6 | **0015** SSE vs polling vs WebSockets | Stale-event test |

**Every PR:**
- small, single-purpose commits (Conventional Commits)
- tests
- an ADR where a choice was made
- before/after numbers for measured paths
- Playwright screenshots for UI changes
- an updated [§17 Status](#17-status) row

The implementing agent **stops after opening each PR** and waits for review.

The review gate (Tech Coordinator plus Code Reviewer; Claude's review is advisory only):
1. Every suite (API xUnit, web Vitest, compose smoke) passes in CI on the PR head, with nothing skipped, disabled or weakened.
2. coverlet and Vitest coverage are collected and published in CI, with the numbers in the PR summary; ≥80% on new or changed code; main never drops. Missing coverage means REQUEST CHANGES.
3. Any workflow, action, Dockerfile, render.yaml, `perf/coverage-*`, `tests/testconfig.json`, or `.gitleaks.toml` change gets governance review: SHA-pinned actions, least-privilege permissions, secrets only in the `production` environment (sole exception: the capped Claude key in `claude-review`), no unsafe `pull_request_target`, gitleaks stays on, nothing removed or loosened.

Tech Coordinator merges and starts the next phase. Don't start the next phase yourself.

Per-area CI ([ADR-0023](docs/adr/0023-per-area-ci-jobs.md)): a heavy job skipped because the base-sourced `changes` classifier reported its flag as exactly `false` was not affected by the diff, and is not "skipped" under clause 1. Any other skip (a failed or cancelled dependency, a missing classifier output, a disabled step) is. Code Reviewer checks the `changes` job summary. Clause 1 still applies in full to every job that runs.

---

## 16. Out of scope / later

- SSO (Microsoft Entra ID) instead of local accounts.
- AG Grid **Enterprise** (Server-Side Row Model, pivot mode, Excel export, range selection), behind a license-key flag. The Community implementation stays the default.
- SQL Server provider parity. Dapper SQL is kept close to ANSI, and dialect differences are noted in ADRs (`LATERAL` ↔ `OUTER APPLY`, `LIMIT` ↔ `TOP`/`FETCH`).
- Real streaming market data (Kafka). P6 simulates the pattern with SSE.
- Write-back workflows (trade entry, mark challenges).

---

## 17. Status

| Phase | PR | State |
|---|---|---|
| Spec | #1 | Merged |
| 0 Scaffold | #2 | Merged; follow-up #242/#243: only Deploy `release` and db-ops `run` hold the production group, db-ops refuses during a Deploy, wiring-test gaps closed (`[workflows]`, in review); follow-up #4: deploy-path safety (pipefail, main-only release, step-scoped DATABASE_URL); follow-up #91: restore linux-x64 + design-time DATABASE_URL before EF bundle/seeder publish; follow-up #5: coverage gates + CI hardening; follow-up #104: coverage gate reads from base on push; follow-up #99: gitleaks v8.30.1 image + CI pins; follow-up #169: CI runs only the jobs a change touches (merged as #173, `e5e26dd`); follow-up #207: every main push gets its own CI run, only the tip deploys (merged as #234, `66d4c22`); follow-up #321: every workflow job pinned to `ubuntu-24.04` before `ubuntu-latest` moves to Ubuntu 26 (merged as #400, `46f69be`); follow-up #270/#398: budgets runs k6 2.3.0 by digest, both pins in lockstep (`[workflows]`, in review) |
| API docs (Swagger UI) | #93 | Merged; follow-up #95: relative OpenAPI servers, fail-safe `SWAGGER_ENABLED`, `/swagger` 404 when off |
| GitHub Pages deploy (`[workflows]`) | #350 | Merged (`599d5d1`): `pages.yml` builds the SPA for `/credit-desk-analytics/` against `vars.APP_URL` and deploys to Pages (ADR-0024) |
| Claude PR review | #3 | Merged; follow-up #7: advisory-only review + claude-review.yml hardening |
| CI image pin agreement (`[workflows]`) | #255 | In review: `gate-tests` fails when a workflow image isn't digest-pinned, has no tracked Dockerfile (or compose image), or disagrees with it in tag or digest |
| 1 Data | #6 | Merged; follow-up #224: deploy and db-ops reseed pass the seeder's `--cap-mb` (`SEED_CAP_MB` / `cap_mb`, default 512, validated) (`[workflows]`, in review) |
| 2 Auth and limits | #105 | Merged; follow-up #106: Render forwarded headers; follow-up #119: shell label, deterministic coverage; follow-up #160: client IP behind Cloudflare (#116); follow-up #175: client-address diagnostics (#165 step a, merged as `57c7cc9`); follow-up #367: per-client login key behind Render's private hops (#165 step b, merged as `a856d45`); follow-up #114: audit retention (90-day default, `AUDIT_RETENTION_DAYS`, ADR-0022), merged as #172 (`825315c`); follow-up #193: idle-period audit purge on a timer (`AUDIT_PURGE_CHECK_MINUTES`, ADR-0022), merged as #292 (`7b8557c`); follow-up #118: one password hash per failed login, `__Host-` antiforgery cookie behind the proxy, demo-account fixes (merged as #218, `7c43da4`); follow-up #230: 401 response-time floor (merged as #279, `09a5b01`); follow-up #306: the floor measured on the compose stack (merged as #340, `c01c678`); follow-up #361: rate-limit partitions and the export gate keyed on the account id (#185, merged as `69450fb`); follow-up #396: a session without an account id is refused with 401, never keyed on the client address (merged as `dc2762a`); follow-up #362: sign-in refused for an account with no role (#275, merged as `cde36d4`); follow-up #366: single-use refresh tokens, a replayed one ends the session (#353, merged as `5458c4d`) |
| Portfolio entitlements (per-user grants) | feat/122-portfolio-grants | In review: `app.portfolio_grant`, `desk:portfolio` claims re-read at every refresh, `GrantedPortfolios` behind `ENTITLEMENTS_MODE=all\|grants` (default `all`), demo-account portfolios, UserAdmin `grant`/`revoke`, scoped/exempt guard on every `/api` path (#122, ADR-0021) |
| GitHub Pages front end + bearer auth | #349 | Merged (`f8db7c2`): Identity bearer tokens, refresh, CORS, build-time API origin (ADR-0024); the Pages deploy workflow is #350 |
| 3 Positions API | #121 | Merged; follow-up #125: CI budgets job; follow-up #136: `payload-size.mjs` fails closed on a short, empty or mis-shaped block (in review) |
| 4 Shell + Positions UI | #140 | Merged; follow-up #141: CI e2e job; follow-up #252: the bundle budget measures Brotli as the API streams it (merged as #347, `5a2c3c8`) |
| 5 Fund Performance | #144 | Merged |
| 6 Insights Board | #339 | Merged; follow-up #227: the coverage summary shows covered/total counts and slack (`[workflows]`, in review); follow-up #357: insights source reads get their own per-user limiter partition (5 in flight, queue 15) so the P3 e2e is green under #174's cap (merged as #382, `929dcea`; supersedes #364) |
| 7 Deal Explorer | #363 | Merged (`09b8344`): `GET /api/deals` (LATERAL representative bond, zero-bond deals kept, per-deal totals, scoped holdings) and `GET /api/deals/{id}`; master/detail page (ADR-0013) |
| 8 Performance Lab | n/a | Not started |
| 9 Hardening | n/a | Not started |
| 10 Intraday overlay (stretch) | n/a | Not started |
