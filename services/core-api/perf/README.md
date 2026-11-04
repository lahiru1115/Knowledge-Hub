# Latency check

`load.py` measures NFR-PERF-01 (CRUD p95 under 300 ms) and NFR-PERF-02 (keyword search p95 under 500 ms) on a
library of 10,000 resources (ASM-04). It needs a running Core API and PostgreSQL, and `psql` on the PATH.

```bash
uv run --with httpx perf/load.py --api http://localhost:8080 \
  --db "postgresql://core_migrator:core_migrator_dev@127.0.0.1:5432/knowledge_hub"
```

Start the API with generous rate limits first (`Auth__RateLimits__GeneralPerUserPerMinute=1000000` and the other
`Auth__RateLimits__*` settings), or most calls will be answered with 429. It prints p50, p95 and p99 per scenario and
exits with 1 when a target is missed.

What the numbers mean: one machine runs the API, the database and the load, with no network between them, so they show
that queries and indexes are sound at this size, not what a deployed server will do. Run it against the demo
deployment before relying on them. Semantic search and chat (NFR-PERF-03, 05 to 07) need the AI service and its models
and are not covered here.
