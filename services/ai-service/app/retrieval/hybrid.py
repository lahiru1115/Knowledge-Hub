"""Hybrid retrieval: a vector leg and a keyword leg over chunks, merged by reciprocal rank fusion (ADR-007)."""

import re
import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

from sqlalchemy import Row, text
from sqlalchemy.ext.asyncio import AsyncConnection, AsyncEngine

from app.indexing import vector_literal
from app.providers.base import Embedder

LEG_SIZE = 50
RRF_K = 60
SNIPPET_CHARS = 240
_WORD = re.compile(r"\w+", re.UNICODE)


@dataclass(frozen=True)
class ChunkHit:
    """One chunk found by search, with why it was found: a keyword match, and/or how close its meaning is."""

    resource_id: uuid.UUID
    chunk_id: uuid.UUID
    content: str
    score: float
    keyword: bool
    similarity: float | None


@dataclass(frozen=True)
class Hit:
    resource_id: uuid.UUID
    chunk_id: uuid.UUID
    content: str
    score: float


def or_query(query: str) -> str | None:
    """Words joined with OR for to_tsquery. A question rarely has every word in one chunk, so AND would find nothing."""
    words = list(dict.fromkeys(w.lower() for w in _WORD.findall(query) if len(w) > 1))
    return " | ".join(words[:30]) if words else None


def snippet(content: str, query: str = "", title_prefix: bool = True) -> str:
    """The part of the chunk worth showing: it starts at the sentence holding the first query word, if there is one.

    The title line every chunk starts with is dropped. With no word in common (a match by meaning) it shows the start.
    """
    body = content.split("\n\n", 1)[1] if title_prefix and "\n\n" in content else content
    body = " ".join(body.split())
    start = 0
    words = {w.lower() for w in _WORD.findall(query) if len(w) > 2}
    if words:
        match = re.search(r"\b(?:" + "|".join(re.escape(w) for w in words) + r")\b", body, re.IGNORECASE)
        if match:
            sentence_start = max(
                body.rfind(". ", 0, match.start()),
                body.rfind("! ", 0, match.start()),
                body.rfind("? ", 0, match.start()),
            )
            start = sentence_start + 2 if sentence_start >= 0 else 0
    window = body[start : start + SNIPPET_CHARS + 1]
    if len(window) > SNIPPET_CHARS:
        window = window[:SNIPPET_CHARS].rsplit(" ", 1)[0].rstrip(",;:- ") + "…"
    return window


def fuse(vector_ids: list[uuid.UUID], keyword_ids: list[uuid.UUID]) -> dict[uuid.UUID, float]:
    """Reciprocal rank fusion: score = sum of 1 / (60 + rank) over the lists a chunk appears in."""
    scores: dict[uuid.UUID, float] = {}
    for ids in (vector_ids, keyword_ids):
        for rank, cid in enumerate(ids, start=1):
            scores[cid] = scores.get(cid, 0.0) + 1.0 / (RRF_K + rank)
    return scores


def scope_sql(with_collection: bool) -> str:
    return "user_id = :u AND embedding_model = :m" + (" AND collection_id = :c" if with_collection else "")


def vector_leg_sql(dims: int, with_collection: bool = False) -> str:
    """The nearest-chunks query. Its cast and model filter match the partial HNSW index, so the index is usable."""
    return (
        f"SELECT id, resource_id, content, "  # noqa: S608
        f"1 - (embedding::public.vector({int(dims)}) <=> CAST(:q AS public.vector({int(dims)}))) AS sim "
        f"FROM ai.chunks WHERE {scope_sql(with_collection)} "
        f"ORDER BY embedding::public.vector({int(dims)}) <=> CAST(:q AS public.vector({int(dims)})) LIMIT :n"
    )


async def _allow_iterative_scan(conn: AsyncConnection) -> None:
    """With pgvector 0.8+ the filter (user, model) can no longer starve the HNSW result list. Older versions lack it."""
    try:
        async with conn.begin_nested():
            await conn.execute(text("SET LOCAL hnsw.iterative_scan = relaxed_order"))
    except Exception:  # noqa: S110
        pass


async def hybrid_chunks(
    engine: AsyncEngine,
    embedder: Embedder,
    user_id: uuid.UUID,
    query: str,
    *,
    collection_id: uuid.UUID | None = None,
) -> list[ChunkHit]:
    """The best chunks, best first (several may come from one resource). Filtered by the caller and the model."""
    query_vector = vector_literal(await embedder.embed_query(query))
    dims = int(embedder.dimensions)  # an int from our own code, interpolated because a cast cannot take a parameter
    scope = scope_sql(collection_id is not None)
    params: dict[str, object] = {"u": user_id, "m": embedder.model_id, "n": LEG_SIZE}
    if collection_id:
        params["c"] = collection_id

    async with engine.begin() as conn:
        await _allow_iterative_scan(conn)
        vector_rows = (
            await conn.execute(text(vector_leg_sql(dims, collection_id is not None)), {**params, "q": query_vector})
        ).all()
        keyword_rows: Sequence[Row[Any]] = []
        if (tsq := or_query(query)) is not None:
            keyword_rows = (
                await conn.execute(
                    text(
                        f"SELECT id, resource_id, content FROM ai.chunks WHERE {scope} "  # noqa: S608
                        "AND tsv @@ to_tsquery('english', :tsq) "
                        "ORDER BY ts_rank_cd(tsv, to_tsquery('english', :tsq)) DESC LIMIT :n"
                    ),
                    {**params, "tsq": tsq},
                )
            ).all()

    by_chunk = {r.id: r for r in [*keyword_rows, *vector_rows]}
    similarity = {r.id: float(r.sim) for r in vector_rows}
    keyword_ids = {r.id for r in keyword_rows}
    scores = fuse([r.id for r in vector_rows], [r.id for r in keyword_rows])
    return [
        ChunkHit(by_chunk[cid].resource_id, cid, by_chunk[cid].content, score, cid in keyword_ids, similarity.get(cid))
        for cid, score in sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
    ]


async def hybrid_search(
    engine: AsyncEngine,
    embedder: Embedder,
    user_id: uuid.UUID,
    query: str,
    *,
    limit: int = 20,
    collection_id: uuid.UUID | None = None,
) -> list[Hit]:
    """At most one hit per resource, best first, the best chunk being its snippet."""
    best: dict[uuid.UUID, Hit] = {}
    for c in await hybrid_chunks(engine, embedder, user_id, query, collection_id=collection_id):
        if c.resource_id not in best:  # chunks arrive best first, so the first one per resource is its snippet
            best[c.resource_id] = Hit(c.resource_id, c.chunk_id, c.content, c.score)
    return list(best.values())[:limit]


async def related(
    engine: AsyncEngine, embedder: Embedder, user_id: uuid.UUID, resource_id: uuid.UUID, *, limit: int = 5
) -> list[tuple[uuid.UUID, float]]:
    """Other resources nearest to the mean of this one's chunk embeddings, as (resource id, cosine similarity)."""
    dims = int(embedder.dimensions)
    async with engine.begin() as conn:
        await _allow_iterative_scan(conn)
        mean = (
            await conn.execute(
                text(
                    f"SELECT avg(embedding::public.vector({dims}))::text FROM ai.chunks "  # noqa: S608
                    "WHERE user_id = :u AND resource_id = :r AND embedding_model = :m"
                ),
                {"u": user_id, "r": resource_id, "m": embedder.model_id},
            )
        ).scalar_one_or_none()
        if mean is None:
            return []
        rows = (
            await conn.execute(
                text(
                    f"SELECT resource_id, 1 - (embedding::public.vector({dims}) "  # noqa: S608
                    f"<=> CAST(:q AS public.vector({dims}))) AS sim "
                    "FROM ai.chunks WHERE user_id = :u AND embedding_model = :m AND resource_id <> :r "
                    f"ORDER BY embedding::public.vector({dims}) <=> CAST(:q AS public.vector({dims})) LIMIT :n"
                ),
                {"q": mean, "u": user_id, "m": embedder.model_id, "r": resource_id, "n": LEG_SIZE},
            )
        ).all()
    best: dict[uuid.UUID, float] = {}
    for rid, sim in rows:
        best.setdefault(rid, float(sim))  # nearest chunk first
    return list(best.items())[:limit]
