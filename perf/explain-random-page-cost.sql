-- #326 / ADR-0008: deep-OFFSET grid plans under random_page_cost 4 (the PostgreSQL default) and 1.1 (Helms's
-- decision for the app role, 2026-10-16), for two indexed sort columns and two with no sort index.
-- Every case is the page query GridSqlBuilder.Build emits (Risk preset columns, 200 rows, latest as-of date, all books,
-- position_id tie-breaker in the first key's direction), with literals in place of parameters. Each plan runs once to
-- warm the cache, then 5 times; the first timed run is printed in full, and the summary has its plan and buffers and
-- the median execution time of the 5. Both settings are set per case (SET LOCAL), so the result doesn't depend
-- on the role's own value; the header shows that value and where it comes from (pg_settings.source).
-- The script is read-only: one DO block, no DDL, no temp objects, output as NOTICE lines.
--
-- Local (agents and humans; the guard refuses a non-local server). Seed at scale 1.0 first (seed 42,
-- --as-of 2026-10-06 for comparable numbers):
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-random-page-cost.sql
-- Neon (Leo only, README §13.1 runbook; agents never connect to Neon). The session is read-only:
--   psql "<Neon direct connection string>" -q -v target=neon -f perf/explain-random-page-cost.sql > before.txt 2>&1
\pset pager off
\set ON_ERROR_STOP on
\set VERBOSITY terse
\if :{?target}
  SET default_transaction_read_only = on;
  BEGIN READ ONLY;
\else
  DO $guard$
  BEGIN
    IF current_database() NOT LIKE 'creditdesk%'
       OR NOT (inet_server_addr() IS NULL
               OR inet_server_addr() <<= inet '127.0.0.0/8'
               OR inet_server_addr() = inet '::1'
               OR inet_server_addr() <<= inet '172.16.0.0/12')
    THEN
      RAISE EXCEPTION 'perf/explain-random-page-cost.sql: local database only (database %, server %)',
        current_database(), coalesce(host(inet_server_addr()), 'unix socket');
    END IF;
  END
  $guard$;
  BEGIN;
\endif
SET LOCAL statement_timeout = '5min';
SELECT version();
SELECT current_user AS role, current_database() AS database, name, setting, source
FROM pg_settings WHERE name IN ('random_page_cost', 'seq_page_cost', 'effective_cache_size', 'work_mem', 'shared_buffers')
ORDER BY name;
SELECT coalesce(d.datname, '(all databases)') AS database, s.setconfig AS role_settings
FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole LEFT JOIN pg_database d ON d.oid = s.setdatabase
WHERE r.rolname = current_user;

DO $matrix$
DECLARE
  risk_cols constant text := '"position_id", "deal_name", "class", "cusip", "sector", "rating_composite", "current_face", "market_value", "unrealized_pnl", "pct_of_portfolio_mv", "price", "price_chg_1d", "yield", "spread_bp", "oas_bp", "dm_bp", "z_spread_bp", "spread_chg_1d_bp", "vendor_dispersion_bp", "mod_duration", "eff_duration", "convexity", "dv01", "krd_2y", "krd_5y", "krd_10y", "krd_20y", "krd_30y", "spread_duration", "cs01", "jtd", "expected_loss_pct", "credit_enhancement_pct", "wal", "coupon_current", "pnl_carry", "pnl_rates", "pnl_spread", "pnl_total_mtd", "worst_case_price", "stress_loss_mv", "watchlist_flag"';
  -- column, direction, whether a (as_of_date, X, position_id) sort index exists (SnapshotSortIndexes)
  sorts constant text[][] := ARRAY[
    ['market_value', 'DESC', 'indexed'], ['spread_bp', 'ASC', 'indexed'],
    ['yield', 'DESC', 'unindexed'], ['oas_bp', 'ASC', 'unindexed']];
  costs constant text[] := ARRAY['4', '1.1'];
  offsets constant int[] := ARRAY[0, 2000, 10000, -1];  -- -1 = last block
  asof date; total int; all_books int[]; old_cost text;
  o int; s int; k int; off int; q text; line text;
  r int; plan text; buf bigint; tmp bigint; ms numeric; runs numeric[]; m text[];
  summary text[] := ARRAY[]::text[];
BEGIN
  SELECT max(as_of_date) INTO asof FROM core.position_snapshot;
  SELECT count(*) INTO total FROM core.position_snapshot WHERE as_of_date = asof;
  SELECT array_agg(portfolio_id ORDER BY portfolio_id) INTO all_books FROM core.portfolio;
  IF total < 18000 THEN
    RAISE EXCEPTION 'Seed at scale 1.0 first: only % rows on %', total, asof;
  END IF;
  RAISE NOTICE 'as of %: % rows, % books', asof, total, array_length(all_books, 1);
  old_cost := current_setting('random_page_cost');
  FOR o IN 1 .. array_length(offsets, 1) LOOP
    off := CASE WHEN offsets[o] = -1 THEN total - 200 ELSE offsets[o] END;
    FOR s IN 1 .. array_length(sorts, 1) LOOP
      q := format('SELECT %s FROM core.position_snapshot WHERE as_of_date = %L AND portfolio_id = ANY(%L::int[]) ORDER BY %I %s, "position_id" %s OFFSET %s ROWS FETCH NEXT 200 ROWS ONLY',
                  risk_cols, asof, all_books, sorts[s][1], sorts[s][2], sorts[s][2], off);
      FOR k IN 1 .. array_length(costs, 1) LOOP
        PERFORM set_config('random_page_cost', costs[k], true);
        RAISE NOTICE '=== OFFSET % | % % (%) | random_page_cost %', off, sorts[s][1], sorts[s][2], sorts[s][3], costs[k];
        EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q;  -- warm-up run, discarded
        plan := ''; buf := NULL; tmp := 0; runs := ARRAY[]::numeric[];
        FOR r IN 1 .. 5 LOOP
          FOR line IN EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q LOOP
            m := regexp_match(line, '^Execution Time: ([\d.]+) ms');
            IF m IS NOT NULL THEN runs := runs || m[1]::numeric; END IF;
            CONTINUE WHEN r > 1;
            RAISE NOTICE '%', line;
            IF buf IS NULL AND line ~ '^\s*Buffers: ' THEN
              buf := coalesce((regexp_match(line, 'shared hit=(\d+)'))[1]::bigint, 0)
                   + coalesce((regexp_match(line, 'shared( hit=\d+)? read=(\d+)'))[2]::bigint, 0);
              tmp := coalesce((regexp_match(line, 'temp( read=\d+)? written=(\d+)'))[2]::bigint, 0);
            END IF;
            m := regexp_match(line, '((?:Parallel )?(?:Index Only Scan|Index Scan Backward|Index Scan|Bitmap Heap Scan|Seq Scan)(?: using \w+)?)');
            IF m IS NOT NULL THEN plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || replace(m[1], 'ix_snapshot_', ''); END IF;
            m := regexp_match(line, '^\s*Sort Method: (top-N heapsort|quicksort|external merge)');
            IF m IS NOT NULL THEN plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || m[1]; END IF;
          END LOOP;
        END LOOP;
        SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY x) INTO ms FROM unnest(runs) AS x;
        summary := summary || format('%-6s | %-17s | %-9s | %-4s | %-58s | %7s | %5s | %9s',
                                     off, sorts[s][1] || ' ' || sorts[s][2], sorts[s][3], costs[k], plan, buf, tmp, ms);
      END LOOP;
    END LOOP;
  END LOOP;
  PERFORM set_config('random_page_cost', old_cost, true);
  RAISE NOTICE '=== Summary (buffers = shared hit + read on the top node; temp = temp blocks written)';
  RAISE NOTICE '%', format('%-6s | %-17s | %-9s | %-4s | %-58s | %7s | %5s | %9s',
                           'offset', 'sort', 'index', 'rpc', 'plan', 'buffers', 'temp', 'median ms');
  FOREACH line IN ARRAY summary LOOP
    RAISE NOTICE '%', line;
  END LOOP;
END
$matrix$;
ROLLBACK;
