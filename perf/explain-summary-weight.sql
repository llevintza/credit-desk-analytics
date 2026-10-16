-- #241 / ADR-0006 addendum: the All preset's whole-book summary (COUNT + 174 aggregates over every row of the latest
-- as-of date), with the weight computed in a CROSS JOIN LATERAL (... OFFSET 0) (before) or read from the stored
-- generated column weight_f8 (after, migration SnapshotWeightColumn), serial and with the planner's own parallel plan.
-- LOCAL DATABASE ONLY (never Render, Neon or production): the guard below refuses a non-local server. Seed at scale 1.0
-- and apply the migrations first. The script runs no DDL and writes nothing persistent.
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-summary-weight.sql
-- The SQL is the summary statement GridSqlBuilder.Build emits for the built-in All preset (every aggregated catalog
-- column), with literals in place of parameters and the whole book's filter from perf/positions.js (spread_bp below every
-- spread). The variants run interleaved, round by round, so load on a shared machine hits each of them alike.
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
    RAISE EXCEPTION 'perf/explain-summary-weight.sql: local database only (database %, server %)',
      current_database(), coalesce(host(inet_server_addr()), 'unix socket');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'core.position_snapshot'::regclass AND attname = 'weight_f8' AND NOT attisdropped) THEN
    RAISE EXCEPTION 'perf/explain-summary-weight.sql: core.position_snapshot has no weight_f8; apply the migrations first';
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
SHOW max_parallel_workers_per_gather;
SELECT count(*) FILTER (WHERE aggregation = 'Sum') AS sums, count(*) FILTER (WHERE aggregation LIKE 'Weighted%') AS weighted
FROM app.column_catalog WHERE aggregation <> 'None';

CREATE TEMP TABLE results (variant text, ms numeric, workers int);

-- The summary text for one weight source: 'lateral' (before) or 'stored' (after).
CREATE FUNCTION pg_temp.summary(asof date, weight text) RETURNS text LANGUAGE sql AS $fn$
  SELECT 'SELECT COUNT(*)::int AS row_count, '
    || string_agg(CASE WHEN aggregation = 'Sum' THEN format('SUM(%I)', name)
                       ELSE format('SUM(%1$I * %2$s) / NULLIF(SUM(%2$s) FILTER (WHERE %1$I IS NOT NULL), 0)', name, w) END
                  || format(' AS %I', name), ', ' ORDER BY ordinal)
    || ' FROM core.position_snapshot'
    || CASE weight WHEN 'lateral' THEN ' CROSS JOIN LATERAL (SELECT abs(market_value)::float8 AS weight_f8 OFFSET 0) w' ELSE '' END
    || format(' WHERE as_of_date = %L AND portfolio_id = ANY(%L::int[]) AND spread_bp > -1e13', asof,
              (SELECT array_agg(portfolio_id) FROM core.portfolio))
  FROM app.column_catalog, LATERAL (SELECT CASE weight WHEN 'lateral' THEN 'w.weight_f8' ELSE 'weight_f8' END AS w) x
  WHERE aggregation <> 'None'
$fn$;

CREATE FUNCTION pg_temp.run_all(asof date, runs int) RETURNS SETOF text LANGUAGE plpgsql AS $fn$
DECLARE
  lateral_q constant text := pg_temp.summary(asof, 'lateral');
  stored_q constant text := pg_temp.summary(asof, 'stored');
  gather constant text := current_setting('max_parallel_workers_per_gather');
  j jsonb; line text; v text; q text;
BEGIN
  FOR line IN EXECUTE 'EXPLAIN (COSTS OFF) ' || lateral_q LOOP
    IF line !~ '^\s*(Output|Filter):' THEN RETURN NEXT 'lateral: ' || line; END IF;
  END LOOP;
  FOR line IN EXECUTE 'EXPLAIN (COSTS OFF) ' || stored_q LOOP
    IF line !~ '^\s*(Output|Filter):' THEN RETURN NEXT 'stored:  ' || line; END IF;
  END LOOP;
  FOR i IN 0 .. runs LOOP  -- round 0 warms the cache and is discarded
    FOREACH v IN ARRAY ARRAY['lateral (before)', 'stored, serial', 'stored, parallel (after)'] LOOP
      q := CASE WHEN v LIKE 'lateral%' THEN lateral_q ELSE stored_q END;
      PERFORM set_config('max_parallel_workers_per_gather', CASE WHEN v = 'stored, serial' THEN '0' ELSE gather END, true);
      EXECUTE 'EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) ' || q INTO j;
      IF i > 0 THEN
        INSERT INTO results VALUES (v, (j -> 0 ->> 'Execution Time')::numeric,
          coalesce((jsonb_path_query_first(j, '$.**."Workers Launched"'))::int, 0));
      END IF;
    END LOOP;
  END LOOP;
  PERFORM set_config('max_parallel_workers_per_gather', gather, true);
END
$fn$;

SELECT * FROM pg_temp.run_all(:'asof'::date, 30);

\echo
\echo 'Summary: execution time over 30 interleaved rounds (EXPLAIN ANALYZE, TIMING OFF), after one warm-up round'
SELECT variant, count(*) AS runs, max(workers) AS workers, round(min(ms), 1) AS min_ms,
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY ms)::numeric, 1) AS p50_ms,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY ms)::numeric, 1) AS p95_ms, round(max(ms), 1) AS max_ms
FROM results GROUP BY variant ORDER BY min(ms) DESC;
