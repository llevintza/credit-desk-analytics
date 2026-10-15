-- ADR-0013 / README §6 P4: "any one bond per deal", LATERAL ... LIMIT 1 vs ROW_NUMBER() = 1 + LEFT JOIN (and DISTINCT ON
-- for reference), with EXPLAIN (ANALYZE, BUFFERS) and a timing loop.
-- LOCAL DATABASE ONLY (never Render, Neon or production): the guard below refuses a non-local server. Seed at scale 1.0
-- first (seed 42, --as-of 2026-10-06 for comparable numbers). The script runs no DDL outside pg_temp and writes nothing
-- persistent.
--   docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-deal-representative.sql
-- Each shape runs in two variants:
--   index:    the query as written; the planner may use ix_bond_deal_seniority (deal_id, seniority_rank, bond_id).
--   order not indexed: the same query ordered by seniority_rank + 0, which the index can't serve in order: the plan the
--             shape would get with an index on deal_id alone, without dropping anything.
-- "rep only" is the README's snippet (deal + its representative bond); "rep only, one sector" filters the deals first
-- (CRT, about a fifth of them: the case of a filtered or paged list); "list" is DealRepository.ListSql's shape (plus
-- per-deal bond totals and the caller's holdings, all 12 portfolios). Each query runs twice to warm the cache, then the
-- plan is printed, then it runs RUNS more times under EXPLAIN ANALYZE (timing only) for p50 / p95 execution time.
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
    RAISE EXCEPTION 'perf/explain-deal-representative.sql: local database only (database %, server %)',
      current_database(), coalesce(host(inet_server_addr()), 'unix socket');
  END IF;
END
$guard$;
SET statement_timeout = '5min';

SELECT max(as_of_date)::text AS asof FROM core.position_snapshot \gset
SELECT count(*) >= 1000 AS scale_ok, count(*) AS deals FROM core.deal \gset
\if :scale_ok
\else
  \echo 'Seed at scale 1.0 first: only' :deals 'deals'
  \quit
\endif
SELECT version();
SELECT (SELECT count(*) FROM core.deal) AS deals, (SELECT count(*) FROM core.bond) AS bonds,
       (SELECT count(*) FROM core.deal d WHERE NOT EXISTS (SELECT 1 FROM core.bond b WHERE b.deal_id = d.deal_id)) AS zero_bond_deals,
       :'asof' AS as_of;

CREATE TEMP TABLE results (n serial, shape text, approach text, variant text, rows_out int, null_bond_rows int,
  plan text, buffers bigint, p50_ms numeric, p95_ms numeric, max_ms numeric);

CREATE FUNCTION pg_temp.measure(shape text, approach text, variant text, q text, runs int) RETURNS SETOF text
LANGUAGE plpgsql AS $fn$
DECLARE
  line text; plan text := ''; buf bigint; times numeric[] := '{}'; j jsonb; m text[]; i int; out_rows int; nulls int;
BEGIN
  EXECUTE format('SELECT count(*), count(*) FILTER (WHERE rep_bond_id IS NULL) FROM (%s) x', q) INTO out_rows, nulls;
  EXECUTE 'EXPLAIN (ANALYZE) ' || q; EXECUTE 'EXPLAIN (ANALYZE) ' || q;  -- warm-up, discarded
  RETURN NEXT format('=== %s | %s | %s', shape, approach, variant);
  FOR line IN EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q LOOP
    RETURN NEXT line;
    IF buf IS NULL AND line ~ '^\s*Buffers: ' THEN
      buf := coalesce((regexp_match(line, 'shared hit=(\d+)'))[1]::bigint, 0)
           + coalesce((regexp_match(line, 'shared( hit=\d+)? read=(\d+)'))[2]::bigint, 0);
    END IF;
    m := regexp_match(line, '^\s*(?:->\s+)?(Nested Loop Left Join|Hash Right Join|Hash Left Join|Merge Left Join|Merge Right Join|WindowAgg|Run Condition|Unique|Limit|Incremental Sort|Sort|Index Only Scan|Index Scan|Seq Scan|Subquery Scan|HashAggregate|GroupAggregate)( using \w+)?( on \w+)?');
    IF m IS NOT NULL AND line ~ '\(actual' THEN
      plan := plan || CASE WHEN plan = '' THEN '' ELSE ' > ' END || m[1] || coalesce(replace(m[2], ' using ', ' '), '') || coalesce(replace(m[3], ' on ', ' '), '');
    END IF;
  END LOOP;
  FOR i IN 1 .. runs LOOP
    EXECUTE 'EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) ' || q INTO j;
    times := times || (j -> 0 ->> 'Execution Time')::numeric;
  END LOOP;
  INSERT INTO results (shape, approach, variant, rows_out, null_bond_rows, plan, buffers, p50_ms, p95_ms, max_ms)
  SELECT shape, approach, variant, out_rows, nulls, plan, buf,
         percentile_cont(0.5) WITHIN GROUP (ORDER BY t), percentile_cont(0.95) WITHIN GROUP (ORDER BY t), max(t)
  FROM unnest(times) t;
END
$fn$;

CREATE FUNCTION pg_temp.run_all(asof date, runs int) RETURNS SETOF text LANGUAGE plpgsql AS $fn$
DECLARE
  rep_cols constant text := 'd.deal_id, d.name, d.sector, b.bond_id AS rep_bond_id, b.class, b.current_balance';
  list_cols constant text := 'd.deal_id, d.name, d.sector, d.sub_sector, d.vintage, d.status, d.original_balance, '
    || 'coalesce(t.bonds, 0) AS bond_count, t.current_balance AS deal_balance, b.bond_id AS rep_bond_id, b.class, b.cusip, '
    || 'b.current_balance, b.coupon_type, b.coupon_or_margin, b.rating_sp, h.positions, h.face, h.mv';
  list_ctes text;
  list_joins constant text := ' LEFT JOIN tranches t ON t.deal_id = d.deal_id LEFT JOIN held h ON h.deal_id = d.deal_id';
  ord text; q text; v int; variant text; shape text; cols text; ctes text; joins text; filter text; line text;
BEGIN
  list_ctes := format('WITH tranches AS (SELECT deal_id, count(*)::int AS bonds, sum(current_balance) AS current_balance '
    || 'FROM core.bond GROUP BY deal_id), held AS (SELECT deal_id, count(*)::int AS positions, sum(face) AS face, '
    || 'sum(market_value) AS mv FROM core.position_snapshot WHERE as_of_date = %L AND portfolio_id = ANY(%L::int[]) '
    || 'GROUP BY deal_id) ', asof, (SELECT array_agg(portfolio_id) FROM core.portfolio));
  FOR s IN 1 .. 3 LOOP
    shape := CASE s WHEN 1 THEN 'rep only' WHEN 2 THEN 'rep only, one sector' ELSE 'list' END;
    cols := CASE s WHEN 3 THEN list_cols ELSE rep_cols END;
    ctes := CASE s WHEN 3 THEN list_ctes ELSE '' END;
    joins := CASE s WHEN 3 THEN list_joins ELSE '' END || CASE s WHEN 2 THEN ' WHERE d.sector = ''CRT''' ELSE '' END;
    FOR v IN 1 .. 2 LOOP
      variant := CASE v WHEN 1 THEN 'index' ELSE 'order not indexed' END;
      ord := CASE v WHEN 1 THEN 'seniority_rank, bond_id' ELSE 'seniority_rank + 0, bond_id' END;

      q := ctes || format('SELECT %s FROM core.deal d LEFT JOIN LATERAL (SELECT bond_id, class, cusip, current_balance, '
        || 'coupon_type, coupon_or_margin, rating_sp FROM core.bond WHERE deal_id = d.deal_id ORDER BY %s LIMIT 1) b ON true%s '
        || 'ORDER BY d.deal_id', cols, ord, joins);
      FOR line IN SELECT * FROM pg_temp.measure(shape, 'LATERAL ... LIMIT 1', variant, q, runs) LOOP RETURN NEXT line; END LOOP;

      q := ctes || format('SELECT %s FROM core.deal d LEFT JOIN (SELECT bond_id, deal_id, class, cusip, current_balance, '
        || 'coupon_type, coupon_or_margin, rating_sp, row_number() OVER (PARTITION BY deal_id ORDER BY %s) AS rn '
        || 'FROM core.bond) b ON b.deal_id = d.deal_id AND b.rn = 1%s ORDER BY d.deal_id', cols, ord, joins);
      FOR line IN SELECT * FROM pg_temp.measure(shape, 'ROW_NUMBER() = 1 + LEFT JOIN', variant, q, runs) LOOP RETURN NEXT line; END LOOP;

      q := ctes || format('SELECT %s FROM core.deal d LEFT JOIN (SELECT DISTINCT ON (deal_id) bond_id, deal_id, class, cusip, '
        || 'current_balance, coupon_type, coupon_or_margin, rating_sp FROM core.bond ORDER BY deal_id, %s) b '
        || 'ON b.deal_id = d.deal_id%s ORDER BY d.deal_id', cols, ord, joins);
      FOR line IN SELECT * FROM pg_temp.measure(shape, 'DISTINCT ON + LEFT JOIN', variant, q, runs) LOOP RETURN NEXT line; END LOOP;
    END LOOP;
  END LOOP;
END
$fn$;

\echo 'as of' :asof
SELECT * FROM pg_temp.run_all(:'asof'::date, 30);

\echo
\echo 'Summary: execution time over 30 warm runs (EXPLAIN ANALYZE, TIMING OFF), shared buffers from the printed plan'
SELECT shape, approach, variant, rows_out, null_bond_rows, buffers, round(p50_ms, 2) AS p50_ms, round(p95_ms, 2) AS p95_ms,
       round(max_ms, 2) AS max_ms, plan
FROM results ORDER BY n;
