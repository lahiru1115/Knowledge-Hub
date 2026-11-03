#!/usr/bin/env python3
"""Latency check against a running Core API on a library of the size the targets assume (ASM-04: 10,000 resources).

Measures NFR-PERF-01 (CRUD p95 < 300 ms) and NFR-PERF-02 (keyword search p95 < 500 ms). It registers a user, fills
that user's library straight in the database, then calls the API from several concurrent clients.

    uv run --with httpx perf/load.py --api http://localhost:8080 \
        --db "postgresql://..."   # a role that can insert into the core schema (the migrator role)

Needs `psql` on the PATH for the seeding. Prints a table and exits 1 when a target is missed.
"""

import argparse
import asyncio
import statistics
import subprocess
import sys
import time
import uuid

import httpx

WORDS = ["postgres", "connection", "pooling", "indexing", "vector", "search", "docker", "kubernetes", "rust", "python",
         "react", "typescript", "testing", "security", "caching", "queues", "design", "async", "memory", "latency"]

TARGETS_MS = {"CRUD": 300, "search": 500}


def seed(db: str, user: str, count: int) -> None:
    """One INSERT ... SELECT: titles and descriptions drawn from a small vocabulary, a spread of dates and types."""
    words = "ARRAY[" + ",".join(f"'{w}'" for w in WORDS) + "]"
    sql = f"""
    INSERT INTO core.resources (id, user_id, url, url_normalized, title, description, notes, type, summary_source,
        is_favourite, is_archived, metadata_status, created_at, updated_at)
    SELECT gen_random_uuid(), '{user}', 'https://example.com/p/' || i, 'https://example.com/p/' || i,
        ({words})[1 + i % 20] || ' ' || ({words})[1 + (i * 7) % 20] || ' guide ' || i,
        'A practical note about ' || ({words})[1 + (i * 3) % 20] || ' and ' || ({words})[1 + (i * 11) % 20] || ' in production systems.',
        CASE WHEN i % 5 = 0 THEN 'Some notes on ' || ({words})[1 + i % 20] END,
        (ARRAY['article','video','pdf','website','course','other'])[1 + i % 6], 'none',
        i % 17 = 0, i % 29 = 0, 'ready',
        now() - (i || ' minutes')::interval, now() - (i || ' minutes')::interval
    FROM generate_series(1, {count}) AS i;
    INSERT INTO core.tags (id, user_id, name)
    SELECT gen_random_uuid(), '{user}', 'tag-' || n FROM generate_series(1, 40) AS n;
    INSERT INTO core.resource_tags (resource_id, tag_id, source)
    SELECT r.id, t.id, 'user' FROM (SELECT id, row_number() OVER () AS n FROM core.resources WHERE user_id = '{user}') r
    JOIN (SELECT id, row_number() OVER () AS n FROM core.tags WHERE user_id = '{user}') t ON t.n = 1 + r.n % 40
    UNION
    SELECT r.id, t.id, 'user' FROM (SELECT id, row_number() OVER () AS n FROM core.resources WHERE user_id = '{user}') r
    JOIN (SELECT id, row_number() OVER () AS n FROM core.tags WHERE user_id = '{user}') t ON t.n = 1 + (r.n * 3) % 40;
    ANALYZE core.resources; ANALYZE core.resource_tags;
    """
    done = subprocess.run(["psql", db, "-v", "ON_ERROR_STOP=1", "-q", "-c", sql], capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit("Seeding failed:\n" + done.stderr)


def set_cookies(response: httpx.Response) -> dict[str, str]:
    """Cookie values from Set-Cookie. httpx would not send Secure cookies back over plain http, so they are carried by hand."""
    found = {}
    for header in response.headers.get_list("set-cookie"):
        name, _, rest = header.partition("=")
        found[name] = rest.split(";", 1)[0]
    return found


async def session(base: str) -> tuple[httpx.AsyncClient, str]:
    c = httpx.AsyncClient(base_url=base, timeout=30)
    jar = set_cookies(await c.get("/api/v1/auth/csrf"))
    c.headers.update({"X-CSRF-Token": jar["kh_csrf"], "Cookie": f"kh_csrf={jar['kh_csrf']}"})
    r = await c.post("/api/v1/auth/register", json={"email": f"perf-{uuid.uuid4().hex}@example.com",
                     "password": "a long unusual passphrase", "display_name": "Perf"})
    r.raise_for_status()
    jar.update(set_cookies(r))
    c.headers.update({"X-CSRF-Token": jar["kh_csrf"], "Cookie": f"kh_at={jar['kh_at']}; kh_csrf={jar['kh_csrf']}"})
    return c, r.json()["id"]


async def measure(name: str, group: str, make_request, n: int, concurrency: int) -> tuple[str, str, list[float], int]:
    times: list[float] = []
    errors = 0
    queue: asyncio.Queue[int] = asyncio.Queue()
    for i in range(n):
        queue.put_nowait(i)

    async def worker() -> None:
        nonlocal errors
        while True:
            try:
                i = queue.get_nowait()
            except asyncio.QueueEmpty:
                return
            t0 = time.perf_counter()
            try:
                ok = await make_request(i)
            except httpx.HTTPError:
                ok = False
            times.append((time.perf_counter() - t0) * 1000)
            errors += 0 if ok else 1

    await asyncio.gather(*(worker() for _ in range(concurrency)))
    return name, group, times, errors


def p(values: list[float], q: float) -> float:
    return statistics.quantiles(values, n=100, method="inclusive")[int(q) - 1] if len(values) > 1 else values[0]


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default="http://localhost:8080")
    ap.add_argument("--db", required=True, help="connection URL of a role that can insert into the core schema")
    ap.add_argument("--resources", type=int, default=10_000)
    ap.add_argument("--requests", type=int, default=300, help="per scenario")
    ap.add_argument("--concurrency", type=int, default=8)
    a = ap.parse_args()

    client, user = await session(a.api)
    seed(a.db, user, a.resources)
    first = (await client.get("/api/v1/resources?page_size=5")).json()
    ids = [r["id"] for r in first["items"]]
    print(f"Library seeded: {first['total']} resources in the default list (archived ones are left out of it)\n")

    v = len(WORDS)
    scenarios = [
        ("list (newest, 20)", "CRUD", lambda i: client.get("/api/v1/resources?page_size=20&page=" + str(1 + i % 50))),
        ("list filtered by type and tag", "CRUD", lambda i: client.get(f"/api/v1/resources?type=article&tag=tag-{1 + i % 40}")),
        ("get one", "CRUD", lambda i: client.get("/api/v1/resources/" + ids[i % len(ids)])),
        ("create", "CRUD", lambda i: client.post("/api/v1/resources", json={"url": f"https://example.com/load/{uuid.uuid4().hex}", "title": "Load test"})),
        ("update", "CRUD", lambda i: client.patch("/api/v1/resources/" + ids[i % len(ids)], json={"is_favourite": i % 2 == 0})),
        ("collections", "CRUD", lambda i: client.get("/api/v1/collections")),
        ("dashboard", "CRUD", lambda i: client.get("/api/v1/dashboard?time_zone=UTC")),
        ("keyword search", "search", lambda i: client.get(f"/api/v1/resources?q={WORDS[i % v]}+{WORDS[(i * 7) % v]}")),
        ("search with filters", "search", lambda i: client.get(f"/api/v1/resources?q={WORDS[i % v]}&type=article&sort=relevance&page_size=50")),
    ]

    def wrap(call):
        async def run(i: int) -> bool:
            return (await call(i)).status_code < 400
        return run

    await asyncio.gather(*(client.get("/api/v1/resources?q=warm") for _ in range(3)))   # warm the plans
    rows = []
    for name, group, call in scenarios:
        rows.append(await measure(name, group, wrap(call), a.requests, a.concurrency))

    failed = False
    print(f"{'scenario':32} {'n':>5} {'err':>4} {'p50':>7} {'p95':>7} {'p99':>7}   target")
    for name, group, times, errors in rows:
        p95 = p(times, 95)
        bad = errors > 0 or p95 >= TARGETS_MS[group]
        failed |= bad
        print(f"{name:32} {len(times):>5} {errors:>4} {p(times, 50):>6.0f}ms {p95:>6.0f}ms {p(times, 99):>6.0f}ms   "
              f"p95 < {TARGETS_MS[group]} ms {'FAIL' if bad else 'ok'}")
    await client.aclose()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
