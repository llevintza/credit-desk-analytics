-- #133 / ADR-0008: EXPLAIN (ANALYZE, BUFFERS) for the first block of the unfiltered Risk view, sorted by each of the
-- four indexed columns, ascending and descending, with and without the index (#48 AC1).
-- LOCAL COMPOSE STACK ONLY (never Render, Neon or production): "before" runs DROP INDEX inside a transaction that is
-- rolled back, and the guard below refuses any database but a local `creditdesk`. Seed at scale 1.0 first.
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-sort-indexes.sql
-- The query has the shape GridSqlBuilder.Build emits for the page (Risk preset columns, every portfolio, 200 rows), on
-- the latest as-of date (the API's default). Each plan runs once to warm the cache, then once for the record.
\pset pager off
\set ON_ERROR_STOP on
DO $guard$
BEGIN
  IF current_database() <> 'creditdesk'
     OR NOT (inet_server_addr() IS NULL
             OR inet_server_addr() <<= inet '127.0.0.0/8'
             OR inet_server_addr() = inet '::1'
             OR inet_server_addr() <<= inet '172.16.0.0/12')
  THEN
    RAISE EXCEPTION 'perf/explain-sort-indexes.sql runs DROP INDEX: local compose stack only (database %, server %)',
      current_database(), coalesce(host(inet_server_addr()), 'unix socket');
  END IF;
END
$guard$;
-- A DROP waiting behind a reader would queue every later read behind it: give up instead.
SET lock_timeout = '2s';
SET statement_timeout = '30s';
SET idle_in_transaction_session_timeout = '30s';

SELECT max(as_of_date)::text AS asof FROM core.position_snapshot \gset
SELECT array_agg(portfolio_id ORDER BY portfolio_id)::text AS portfolios FROM core.portfolio \gset
SELECT count(*) >= 18000 AS scale_ok, count(*) AS asof_rows FROM core.position_snapshot WHERE as_of_date = :'asof' \gset
\if :scale_ok
\else
  \echo 'Seed at scale 1.0 first: only' :asof_rows 'rows on' :asof
  \quit
\endif
\echo 'as of' :asof ',' :asof_rows 'rows, portfolios' :portfolios
\set risk_cols '"position_id", "deal_name", "class", "cusip", "sector", "rating_composite", "current_face", "market_value", "unrealized_pnl", "pct_of_portfolio_mv", "price", "price_chg_1d", "yield", "spread_bp", "oas_bp", "dm_bp", "z_spread_bp", "spread_chg_1d_bp", "vendor_dispersion_bp", "mod_duration", "eff_duration", "convexity", "dv01", "krd_2y", "krd_5y", "krd_10y", "krd_20y", "krd_30y", "spread_duration", "cs01", "jtd", "expected_loss_pct", "credit_enhancement_pct", "wal", "coupon_current", "pnl_carry", "pnl_rates", "pnl_spread", "pnl_total_mtd", "worst_case_price", "stress_loss_mv", "watchlist_flag"'

\set col market_value
\set dir DESC
\echo '=== market_value DESC: before (no ix_snapshot_sort_market_value)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_market_value;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== market_value DESC: after (ix_snapshot_sort_market_value)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col market_value
\set dir ASC
\echo '=== market_value ASC: before (no ix_snapshot_sort_market_value)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_market_value;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== market_value ASC: after (ix_snapshot_sort_market_value)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col spread_bp
\set dir DESC
\echo '=== spread_bp DESC: before (no ix_snapshot_sort_spread_bp)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_spread_bp;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== spread_bp DESC: after (ix_snapshot_sort_spread_bp)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col spread_bp
\set dir ASC
\echo '=== spread_bp ASC: before (no ix_snapshot_sort_spread_bp)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_spread_bp;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== spread_bp ASC: after (ix_snapshot_sort_spread_bp)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col dv01
\set dir DESC
\echo '=== dv01 DESC: before (no ix_snapshot_sort_dv01)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_dv01;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== dv01 DESC: after (ix_snapshot_sort_dv01)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col dv01
\set dir ASC
\echo '=== dv01 ASC: before (no ix_snapshot_sort_dv01)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_dv01;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== dv01 ASC: after (ix_snapshot_sort_dv01)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col deal_name
\set dir DESC
\echo '=== deal_name DESC: before (no ix_snapshot_sort_deal_name)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_deal_name;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== deal_name DESC: after (ix_snapshot_sort_deal_name)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;

\set col deal_name
\set dir ASC
\echo '=== deal_name ASC: before (no ix_snapshot_sort_deal_name)'
BEGIN;
DROP INDEX core.ix_snapshot_sort_deal_name;
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
ROLLBACK;
\echo '=== deal_name ASC: after (ix_snapshot_sort_deal_name)'
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
\o
EXPLAIN (ANALYZE, BUFFERS) SELECT :risk_cols FROM core.position_snapshot WHERE as_of_date = :'asof' AND portfolio_id = ANY(:'portfolios'::int[]) ORDER BY :"col" :dir, "position_id" :dir OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;
