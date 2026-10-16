-- #459 / ADR-0006 addendum: the All preset's whole-book summary (COUNT + every aggregated catalog column over every row
-- of the latest as-of date) with one weight for every average (before: abs(market_value) in weight_f8) and with the
-- prices' second weight (after: abs(current_face) in face_weight_f8, migration SnapshotFaceWeightColumn), each with the
-- planner's own (parallel) plan.
-- LOCAL DATABASE ONLY (never Render, Neon or production): the guard below refuses a non-local server. Seed at scale 1.0
-- and apply the migrations first. The script runs no DDL and writes nothing persistent.
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-summary-face-weight.sql
-- The SQL is the summary statement GridSqlBuilder.Build emits for the built-in All preset, with literals in place of
-- parameters and the whole book's filter from perf/positions.js (spread_bp below every spread). "After" weighs the
-- Price-kind columns by face_weight_f8, as the catalog does from #459 (whatever app.column_catalog says, so the script
-- also runs against a catalog seeded before #459). The variants run interleaved, round by round, so load on a shared
-- machine hits each of them alike.
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
    RAISE EXCEPTION 'perf/explain-summary-face-weight.sql: local database only (database %, server %)',
      current_database(), coalesce(host(inet_server_addr()), 'unix socket');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'core.position_snapshot'::regclass AND attname = 'face_weight_f8' AND NOT attisdropped) THEN
    RAISE EXCEPTION 'perf/explain-summary-face-weight.sql: core.position_snapshot has no face_weight_f8; apply the migrations first';
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
SELECT count(*) FILTER (WHERE aggregation = 'Sum') AS sums, count(*) FILTER (WHERE aggregation LIKE 'Weighted%' AND kind <> 'Price') AS mv_weighted,
       count(*) FILTER (WHERE aggregation LIKE 'Weighted%' AND kind = 'Price') AS prices
FROM app.column_catalog WHERE aggregation <> 'None';
SELECT pg_size_pretty(pg_total_relation_size('core.position_snapshot')) AS snapshot_size;

CREATE TEMP TABLE results (variant text, ms numeric, workers int);

-- The summary text: 'mv' (before, one weight) or 'face' (after, prices by face).
CREATE FUNCTION pg_temp.summary(asof date, weights text) RETURNS text LANGUAGE sql AS $fn$
  SELECT 'SELECT COUNT(*)::int AS row_count, '
    || string_agg(CASE WHEN aggregation = 'Sum' THEN format('SUM(%I)', name)
                       ELSE format('SUM(%1$I * %2$s) / NULLIF(SUM(%2$s) FILTER (WHERE %1$I IS NOT NULL), 0)', name,
                                   CASE WHEN weights = 'face' AND kind = 'Price' THEN 'face_weight_f8' ELSE 'weight_f8' END) END
                  || format(' AS %I', name), ', ' ORDER BY ordinal)
    || ' FROM core.position_snapshot'
    || format(' WHERE as_of_date = %L AND portfolio_id = ANY(%L::int[]) AND spread_bp > -1e13', asof,
              (SELECT array_agg(portfolio_id) FROM core.portfolio))
  FROM app.column_catalog
  WHERE aggregation <> 'None'
$fn$;

CREATE FUNCTION pg_temp.run_all(asof date, runs int) RETURNS SETOF text LANGUAGE plpgsql AS $fn$
DECLARE
  mv_q constant text := pg_temp.summary(asof, 'mv');
  face_q constant text := pg_temp.summary(asof, 'face');
  j jsonb; line text; v text;
BEGIN
  FOR line IN EXECUTE 'EXPLAIN (COSTS OFF) ' || mv_q LOOP
    IF line !~ '^\s*(Output|Filter):' THEN RETURN NEXT 'mv:   ' || line; END IF;
  END LOOP;
  FOR line IN EXECUTE 'EXPLAIN (COSTS OFF) ' || face_q LOOP
    IF line !~ '^\s*(Output|Filter):' THEN RETURN NEXT 'face: ' || line; END IF;
  END LOOP;
  FOR i IN 0 .. runs LOOP  -- round 0 warms the cache and is discarded
    FOREACH v IN ARRAY ARRAY['|MV| weight only (before)', 'prices by |face| (after)'] LOOP
      EXECUTE 'EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) ' || CASE WHEN v LIKE '%before%' THEN mv_q ELSE face_q END INTO j;
      IF i > 0 THEN
        INSERT INTO results VALUES (v, (j -> 0 ->> 'Execution Time')::numeric,
          coalesce((jsonb_path_query_first(j, '$.**."Workers Launched"'))::int, 0));
      END IF;
    END LOOP;
  END LOOP;
END
$fn$;

SELECT * FROM pg_temp.run_all(:'asof'::date, 30);

\echo
\echo 'Summary: execution time over 30 interleaved rounds (EXPLAIN ANALYZE, TIMING OFF), after one warm-up round'
SELECT variant, count(*) AS runs, max(workers) AS workers, round(min(ms), 1) AS min_ms,
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY ms)::numeric, 1) AS p50_ms,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY ms)::numeric, 1) AS p95_ms, round(max(ms), 1) AS max_ms
FROM results GROUP BY variant ORDER BY min(ms) DESC;
