-- ADR-0014 (#67): derive the README §7.2 limits from app.audit. Read-only; run it against a LOCAL stack after a demo
-- session (e2e/tests/demo-session.spec.ts), never against Neon or production:
--   docker exec -i <postgres container> psql -U desk -d creditdesk -v since="'2026-10-16T12:00:00Z'" -v until="'2026-10-16T13:00:00Z'" -v rate=1 -f - < perf/audit-limits.sql
-- since, until: the session's audit rows (at in [since, until)). rate: the token bucket's refill per second (RATE_LIMIT_PER_USER_PER_MIN / 60).
--
-- An audit row is written after the limiter (README §7.2), so it covers admitted requests only; its start
-- (at - ms) is when the request left the per-user queue and spent its token. A session with 429s under-counts its
-- demand here: read the refused calls from the spec's demo-session.json instead.
-- The pairwise windows are O(n²) per user: fine for a session's few hundred rows, not for a whole production table.

\set ON_ERROR_STOP on

CREATE TEMP TABLE r AS
SELECT user_name,
       endpoint,
       cache,
       at - ms * interval '1 millisecond' AS t0,
       at AS t1,
       CASE WHEN endpoint = 'GET /api/insights/{source}' THEN 'insights' ELSE 'main' END AS part,
       row_number() OVER (PARTITION BY user_name ORDER BY at - ms * interval '1 millisecond', id) AS n
FROM app.audit
WHERE kind = 'request' AND at >= :since::timestamptz AND at < :until::timestamptz AND endpoint <> 'POST /api/positions/export';

\echo '1. Pace per user: requests, span, busiest 10 s and 60 s windows'
SELECT r.user_name,
       count(*) AS requests,
       round(extract(epoch FROM max(r.t1) - min(r.t0))::numeric, 1) AS span_s,
       (SELECT max(c) FROM (SELECT count(*) AS c FROM r a JOIN r b ON b.user_name = a.user_name
                              AND b.t0 >= a.t0 AND b.t0 < a.t0 + interval '10 seconds'
                            WHERE a.user_name = r.user_name GROUP BY a.n) w) AS peak_10s,
       (SELECT max(c) FROM (SELECT count(*) AS c FROM r a JOIN r b ON b.user_name = a.user_name
                              AND b.t0 >= a.t0 AND b.t0 < a.t0 + interval '60 seconds'
                            WHERE a.user_name = r.user_name GROUP BY a.n) w) AS peak_60s
FROM r GROUP BY r.user_name ORDER BY requests DESC;

\echo '2. Token bucket: the smallest burst that admits every request at :rate tokens/s'
-- Requests i..j spend j-i+1 tokens while the bucket refills (t_j - t_i) * rate: the burst must cover the worst gap.
SELECT a.user_name,
       ceil(max((b.n - a.n + 1) - extract(epoch FROM b.t0 - a.t0) * :rate))::int AS burst_needed
FROM r a JOIN r b ON b.user_name = a.user_name AND b.n >= a.n
GROUP BY a.user_name ORDER BY burst_needed DESC;

\echo '3. In flight per user and partition (main: 1 permit + queue; insights: 5 permits + queue)'
SELECT a.user_name, a.part, max(c.inflight) AS peak_in_flight
FROM r a
CROSS JOIN LATERAL (SELECT count(*) AS inflight FROM r b
                    WHERE b.user_name = a.user_name AND b.part = a.part AND b.t0 <= a.t0 AND b.t1 > a.t0) c
GROUP BY a.user_name, a.part ORDER BY a.user_name, a.part;

\echo '4. Database permits: MISS requests in flight at once, all users (RATE_LIMIT_GLOBAL_CONCURRENCY)'
SELECT coalesce(max(c.inflight), 0) AS peak_db_in_flight
FROM r a
CROSS JOIN LATERAL (SELECT count(*) AS inflight FROM r b WHERE b.cache = 'MISS' AND b.t0 <= a.t0 AND b.t1 > a.t0) c
WHERE a.cache = 'MISS';

\echo '5. Cache by endpoint (README §7.2: only first views read the database)'
SELECT endpoint,
       count(*) FILTER (WHERE cache = 'MISS') AS miss,
       count(*) FILTER (WHERE cache = 'HIT') AS hit,
       count(*) FILTER (WHERE cache IS NULL) AS uncached,
       round(avg(extract(epoch FROM t1 - t0) * 1000) FILTER (WHERE cache = 'MISS')) AS miss_ms_avg
FROM r GROUP BY endpoint ORDER BY endpoint;
