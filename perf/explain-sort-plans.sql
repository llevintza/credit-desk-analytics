-- #188 / ADR-0008: EXPLAIN (ANALYZE, BUFFERS) for the four sort indexes beyond the unfiltered first block that
-- perf/explain-sort-indexes.sql covers (#133): portfolio subsets, set and range filters, and deep OFFSET blocks.
-- LOCAL DATABASE ONLY (never Render, Neon or production): the guard below refuses a non-local server. Seed at scale 1.0
-- first (seed 42, --as-of 2026-10-06 for comparable numbers). The script runs no DDL and writes nothing persistent.
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-sort-plans.sql
-- Every case is the page query GridSqlBuilder.Build emits (Risk preset columns, 200 rows, latest as-of date, position_id
-- tie-breaker in the first key's direction), with literals in place of parameters. Each case runs twice per sort:
--   index:    the query as emitted; the planner may use the (as_of_date, X, position_id) sort index.
--   no index: the same query sorted by X + 0 (or X || '' for text), which no index can serve: the plan the case
--             would get without the sort index, without dropping it.
-- Each plan runs once to warm the cache, then once for the record. A summary table follows the plans. Rows removed by a
-- parallel node are per worker (EXPLAIN's per-loop average).
\pset pager off
\set ON_ERROR_STOP on
DO $guard$
BEGIN
  IF current_database() NOT LIKE 'creditdesk%'
     OR NOT (inet_server_addr() IS NULL
             OR inet_server_addr() <<= inet '127.0.0.0/8'
             OR inet_server_addr() = inet '::1'
             OR inet_server_addr() <<= inet '172.16.0.0/12')
  THEN
    RAISE EXCEPTION 'perf/explain-sort-plans.sql: local database only (database %, server %)',
      current_database(), coalesce(host(inet_server_addr()), 'unix socket');
  END IF;
END
$guard$;
SET statement_timeout = '5min';

SELECT max(as_of_date)::text AS asof FROM core.position_snapshot \gset
SELECT count(*) >= 18000 AS scale_ok, count(*) AS asof_rows FROM core.position_snapshot WHERE as_of_date = :'asof' \gset
\if :scale_ok
\else
  \echo 'Seed at scale 1.0 first: only' :asof_rows 'rows on' :asof
  \quit
\endif
\echo 'as of' :asof ':' :asof_rows 'rows'
SHOW work_mem;
SELECT portfolio_id, count(*) AS rows FROM core.position_snapshot WHERE as_of_date = :'asof' GROUP BY 1 ORDER BY 1;

CREATE TEMP TABLE plan_summary (
  n serial, shape text, sort text, variant text, rows_in_scope int, plan text,
  buffers bigint, temp_written bigint, rows_removed bigint, ms numeric);

CREATE FUNCTION pg_temp.explain_matrix(asof date) RETURNS SETOF text LANGUAGE plpgsql AS $fn$
DECLARE
  risk_cols constant text := '"position_id", "deal_name", "class", "cusip", "sector", "rating_composite", "current_face", "market_value", "unrealized_pnl", "pct_of_portfolio_mv", "price", "price_chg_1d", "yield", "spread_bp", "oas_bp", "dm_bp", "z_spread_bp", "spread_chg_1d_bp", "vendor_dispersion_bp", "mod_duration", "eff_duration", "convexity", "dv01", "krd_2y", "krd_5y", "krd_10y", "krd_20y", "krd_30y", "spread_duration", "cs01", "jtd", "expected_loss_pct", "credit_enhancement_pct", "wal", "coupon_current", "pnl_carry", "pnl_rates", "pnl_spread", "pnl_total_mtd", "worst_case_price", "stress_loss_mv", "watchlist_flag"';
  all_books int[];
  total int;
  -- shape label, portfolios (NULL = every portfolio), extra filter in GridSqlBuilder's form, offset (-1 = last block),
  -- and a planner setting for this case only (name=value, '' = server default)
  cases constant text[][] := ARRAY[
    ['all books, first block',                 NULL,     '',                                                         '0',     ''],
    ['book 12 (smallest)',                     '{12}',   '',                                                         '0',     ''],
    ['book 1 (largest)',                       '{1}',    '',                                                         '0',     ''],
    ['books [3,7] (README §6)',                '{3,7}',  '',                                                         '0',     ''],
    ['set rating_composite {BB,B,CCC}',        NULL,     ' AND "rating_composite" = ANY(''{BB,B,CCC}''::text[])',     '0',     ''],
    ['set sector {CRT}',                       NULL,     ' AND "sector" = ANY(''{CRT}''::text[])',                    '0',     ''],
    ['range dv01 < 1.77 (bottom quartile)',    NULL,     ' AND "dv01" < 1.77',                                       '0',     ''],
    ['range spread_bp inRange (500, 1000)',    NULL,     ' AND ("spread_bp" > 500 AND "spread_bp" < 1000)',          '0',     ''],
    ['book 12 + set rating {BB,B,CCC}',        '{12}',   ' AND "rating_composite" = ANY(''{BB,B,CCC}''::text[])',     '0',     ''],
    ['set rating_composite {CCC} (rarest)',    NULL,     ' AND "rating_composite" = ANY(''{CCC}''::text[])',          '0',     ''],
    ['all books, OFFSET 2,000',                NULL,     '',                                                         '2000',  ''],
    ['all books, OFFSET 10,000',               NULL,     '',                                                         '10000', ''],
    ['all books, last block',                  NULL,     '',                                                         '-1',    ''],
    -- the same deep blocks under the two planner settings the deep-OFFSET plans point at
    ['all books, OFFSET 10,000',               NULL,     '',                                                         '10000', 'work_mem=16MB'],
    ['all books, last block',                  NULL,     '',                                                         '-1',    'work_mem=16MB'],
    ['all books, OFFSET 10,000',               NULL,     '',                                                         '10000', 'random_page_cost=1.1'],
    ['all books, last block',                  NULL,     '',                                                         '-1',    'random_page_cost=1.1'],
    ['books [3,7] (README §6)',                '{3,7}',  '',                                                         '0',     'random_page_cost=1.1'],
    ['range dv01 < 1.77 (bottom quartile)',    NULL,     ' AND "dv01" < 1.77',                                       '0',     'random_page_cost=1.1']];
  sorts constant text[][] := ARRAY[
    ['market_value', 'DESC'], ['market_value', 'ASC'], ['spread_bp', 'DESC'], ['spread_bp', 'ASC'],
    ['dv01', 'DESC'], ['dv01', 'ASC'], ['deal_name', 'DESC'], ['deal_name', 'ASC']];
  c int; s int; v int;
  books int[]; off int; guc text; old_guc text;
  where_sql text; sort_expr text; q text; line text; in_scope int;
  label text; plan text; buf bigint; tmp bigint; removed bigint; ms numeric; m text[];
BEGIN
  SELECT array_agg(portfolio_id ORDER BY portfolio_id) INTO all_books FROM core.portfolio;
  SELECT count(*) INTO total FROM core.position_snapshot WHERE as_of_date = asof;
  FOR c IN 1 .. array_length(cases, 1) LOOP
    books := coalesce(cases[c][2]::int[], all_books);
    label := cases[c][1] || CASE WHEN cases[c][5] <> '' THEN ', ' || cases[c][5] ELSE '' END;
    where_sql := format('as_of_date = %L AND portfolio_id = ANY(%L::int[])', asof, books) || cases[c][3];
    EXECUTE 'SELECT count(*) FROM core.position_snapshot WHERE ' || where_sql INTO in_scope;
    off := CASE WHEN cases[c][4] = '-1' THEN total - 200 ELSE cases[c][4]::int END;
    guc := split_part(cases[c][5], '=', 1);
    IF guc <> '' THEN
      old_guc := current_setting(guc);
      PERFORM set_config(guc, split_part(cases[c][5], '=', 2), true);
    END IF;
    FOR s IN 1 .. array_length(sorts, 1) LOOP
      FOR v IN 1 .. 2 LOOP
        sort_expr := CASE
          WHEN v = 1 THEN quote_ident(sorts[s][1])
          WHEN sorts[s][1] = 'deal_name' THEN format('(%I || '''')', sorts[s][1])
          ELSE format('(%I + 0)', sorts[s][1]) END;
        q := format('SELECT %s FROM core.position_snapshot WHERE %s ORDER BY %s %s, "position_id" %s OFFSET %s ROWS FETCH NEXT 200 ROWS ONLY',
                    risk_cols, where_sql, sort_expr, sorts[s][2], sorts[s][2], off);
        RETURN NEXT format('=== %s | %s %s | %s | %s rows in scope, OFFSET %s', label, sorts[s][1], sorts[s][2],
                           CASE v WHEN 1 THEN 'index' ELSE 'no index' END, in_scope, off);
        RETURN NEXT q;
        EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q;  -- warm-up run, discarded
        plan := ''; buf := NULL; tmp := 0; removed := 0; ms := NULL;
        FOR line IN EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q LOOP
          RETURN NEXT line;
          IF buf IS NULL AND line ~ '^\s*Buffers: ' THEN
            buf := coalesce((regexp_match(line, 'shared hit=(\d+)'))[1]::bigint, 0)
                 + coalesce((regexp_match(line, 'shared( hit=\d+)? read=(\d+)'))[2]::bigint, 0);
            tmp := coalesce((regexp_match(line, 'temp( read=\d+)? written=(\d+)'))[2]::bigint, 0);
          END IF;
          m := regexp_match(line, '((?:Parallel )?(?:Index Only Scan|Index Scan Backward|Index Scan|Bitmap Heap Scan|Bitmap Index Scan|Seq Scan)(?: using \w+)?)');
          IF m IS NOT NULL THEN plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || replace(m[1], 'ix_snapshot_', ''); END IF;
          m := regexp_match(line, '^\s*(?:->\s+)?(Gather Merge|Incremental Sort)');
          IF m IS NOT NULL THEN plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || m[1]; END IF;
          m := regexp_match(line, '^\s*Sort Method: (top-N heapsort|quicksort|external merge)(?:\s+Disk: (\d+kB))?');
          IF m IS NOT NULL THEN
            plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || 'sort: ' || m[1] || coalesce(' (disk ' || m[2] || ')', '');
          END IF;
          m := regexp_match(line, 'Rows Removed by Filter: (\d+)');
          IF m IS NOT NULL THEN removed := removed + m[1]::bigint; END IF;
          m := regexp_match(line, '^Execution Time: ([\d.]+) ms');
          IF m IS NOT NULL THEN ms := m[1]::numeric; END IF;
        END LOOP;
        INSERT INTO plan_summary (shape, sort, variant, rows_in_scope, plan, buffers, temp_written, rows_removed, ms)
        VALUES (label, sorts[s][1] || ' ' || sorts[s][2], CASE v WHEN 1 THEN 'index' ELSE 'no index' END,
                in_scope, plan, buf, tmp, removed, ms);
        RETURN NEXT '';
      END LOOP;
    END LOOP;
    IF guc <> '' THEN PERFORM set_config(guc, old_guc, true); END IF;
  END LOOP;
END
$fn$;

\pset tuples_only on
\pset format unaligned
SELECT line FROM pg_temp.explain_matrix(:'asof'::date) AS line;
\pset tuples_only off
\pset format aligned
\echo '=== Summary (buffers = shared hit + read on the top node; temp = temp blocks written; removed = rows removed by Filter)'
SELECT shape, sort, variant, rows_in_scope AS scope, plan, buffers, temp_written AS temp, rows_removed AS removed, ms
FROM plan_summary ORDER BY n;
