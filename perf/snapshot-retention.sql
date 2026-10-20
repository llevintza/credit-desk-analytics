-- ADR-0029 (#544): storage cost of rolling the synthetic book forward one as-of date per business day, under the two
-- retention mechanisms (D13). Runs against a throwaway local Postgres that perf/snapshot-retention.sh migrated and
-- seeded (trust auth, nothing persisted); never Neon or production.
--
-- Each "day" step copies the newest as-of date of core.position_snapshot, pricing.vendor_mark and
-- pricing.internal_mark to the next business day (the shape the #548 roll-forward writes), then drops the oldest date
-- so the D9 retention holds (snapshot 2 as-of dates, marks 22 business days):
--   mode=delete    : batched DELETE (5,000 rows per transaction) + plain VACUUM (ANALYZE). Never VACUUM FULL.
--   mode=partition : core.position_snapshot is range-partitioned by as_of_date (step "partition" converts it once);
--                    the oldest snapshot date is a DROP TABLE of its partition. Marks still use the batched DELETE.
--
-- Steps (-v step=…): partition | day | size. Variables: -v mode=delete|partition, -v day=N (label only).
\set ON_ERROR_STOP on
\if :{?mode}
\else
  \set mode delete
\endif
\if :{?step}
\else
  \set step size
\endif
\if :{?day}
\else
  \set day 0
\endif
SELECT :'step' = 'partition' AS do_partition, :'step' = 'day' AS do_day, :'step' = 'size' AS do_size,
       :'mode' = 'partition' AS is_partition \gset

-- Copies the newest as-of date of a table to the next business day, every stored column but the generated ones.
CREATE OR REPLACE PROCEDURE pg_temp.roll_copy(tbl regclass, next_date date)
LANGUAGE plpgsql AS $$
DECLARE
    cols text;
    exprs text;
BEGIN
    SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum),
           string_agg(CASE WHEN attname = 'as_of_date' THEN quote_literal(next_date) || '::date' ELSE quote_ident(attname) END,
                      ', ' ORDER BY attnum)
      INTO cols, exprs
      FROM pg_attribute
     WHERE attrelid = tbl AND attnum > 0 AND NOT attisdropped AND attgenerated = '';
    EXECUTE format('INSERT INTO %s (%s) SELECT %s FROM %s WHERE as_of_date = (SELECT max(as_of_date) FROM %s)',
                   tbl, cols, exprs, tbl, tbl);
END $$;

-- Deletes one as-of date in 5,000-row batches, one transaction each (what the #549 retention step does).
CREATE OR REPLACE PROCEDURE pg_temp.purge_date(tbl regclass, old_date date)
LANGUAGE plpgsql AS $$
DECLARE
    n bigint;
BEGIN
    LOOP
        EXECUTE format('DELETE FROM %s WHERE ctid IN (SELECT ctid FROM %s WHERE as_of_date = $1 LIMIT 5000)', tbl, tbl)
          USING old_date;
        GET DIAGNOSTICS n = ROW_COUNT;
        COMMIT;
        EXIT WHEN n = 0;
    END LOOP;
END $$;

\if :do_partition
\echo '--- convert core.position_snapshot to a range-partitioned table (one partition per as-of date)'
BEGIN;
ALTER TABLE core.position_snapshot RENAME TO position_snapshot_old;
CREATE TABLE core.position_snapshot (LIKE core.position_snapshot_old INCLUDING ALL) PARTITION BY RANGE (as_of_date);
SELECT format('CREATE TABLE core.position_snapshot_%s PARTITION OF core.position_snapshot FOR VALUES FROM (%L) TO (%L)',
              to_char(d, 'YYYYMMDD'), d, d + 1)
  FROM (SELECT DISTINCT as_of_date AS d FROM core.position_snapshot_old) AS dates ORDER BY d \gexec
SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum) AS cols
  FROM pg_attribute
 WHERE attrelid = 'core.position_snapshot_old'::regclass AND attnum > 0 AND NOT attisdropped AND attgenerated = '' \gset
INSERT INTO core.position_snapshot (:cols) SELECT :cols FROM core.position_snapshot_old;
DROP TABLE core.position_snapshot_old;
COMMIT;
VACUUM (ANALYZE) core.position_snapshot;
\endif

\if :do_day
SELECT max(as_of_date) AS newest, min(as_of_date) AS oldest,
       (max(as_of_date) + CASE extract(isodow FROM max(as_of_date)) WHEN 5 THEN 3 WHEN 6 THEN 2 ELSE 1 END)::date AS next_date
  FROM core.position_snapshot \gset
SELECT min(as_of_date) AS marks_oldest FROM pricing.vendor_mark \gset
SELECT pg_current_wal_lsn() AS lsn0, clock_timestamp() AS t0 \gset

\if :is_partition
SELECT format('CREATE TABLE core.position_snapshot_%s PARTITION OF core.position_snapshot FOR VALUES FROM (%L) TO (%L)',
              to_char(:'next_date'::date, 'YYYYMMDD'), :'next_date'::date, :'next_date'::date + 1) \gexec
\endif
CALL pg_temp.roll_copy('core.position_snapshot', :'next_date');
CALL pg_temp.roll_copy('pricing.vendor_mark', :'next_date');
CALL pg_temp.roll_copy('pricing.internal_mark', :'next_date');

\if :is_partition
SELECT format('DROP TABLE core.position_snapshot_%s', to_char(:'oldest'::date, 'YYYYMMDD')) \gexec
\else
CALL pg_temp.purge_date('core.position_snapshot', :'oldest');
\endif
CALL pg_temp.purge_date('pricing.vendor_mark', :'marks_oldest');
CALL pg_temp.purge_date('pricing.internal_mark', :'marks_oldest');
VACUUM (ANALYZE) core.position_snapshot, pricing.vendor_mark, pricing.internal_mark;

SELECT :'day' AS day, :'next_date' AS new_as_of,
       round(extract(epoch FROM clock_timestamp() - :'t0'::timestamptz)::numeric, 1) AS step_s,
       round(pg_wal_lsn_diff(pg_current_wal_lsn(), :'lsn0') / 1048576.0, 1) AS wal_mb,
       -- Plain tables and partitions only: a partitioned parent has no storage of its own.
       round(sum(pg_total_relation_size(c.oid)) FILTER (WHERE n.nspname = 'core') / 1048576.0, 1) AS snapshot_mb,
       round(sum(pg_total_relation_size(c.oid)) FILTER (WHERE n.nspname = 'pricing') / 1048576.0, 1) AS marks_mb,
       round(pg_database_size(current_database()) / 1048576.0, 1) AS db_mb
  FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace
 WHERE c.relkind = 'r'
   AND ((n.nspname = 'core' AND c.relname LIKE 'position\_snapshot%')
        OR (n.nspname = 'pricing' AND c.relname IN ('vendor_mark', 'internal_mark')));
\endif

\if :do_size
SELECT (SELECT count(DISTINCT as_of_date) FROM core.position_snapshot) AS snapshot_dates,
       (SELECT count(*) FROM core.position_snapshot) AS snapshot_rows,
       (SELECT count(DISTINCT as_of_date) FROM pricing.vendor_mark) AS mark_dates,
       round(pg_database_size(current_database()) / 1048576.0, 1) AS db_mb;
\endif
