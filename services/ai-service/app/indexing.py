"""Indexing: document -> chunks -> embeddings -> rows (Doc 02 §4.3)."""

import hashlib
import uuid
from dataclasses import dataclass
from typing import Literal

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine

from app.providers.base import Embedder
from app.retrieval.chunker import build_document, chunk_document

BATCH = 32


@dataclass(frozen=True)
class IndexInput:
    title: str | None
    description: str | None
    notes: str | None
    collection_id: uuid.UUID | None
    #: The page's extracted text. None means "keep the stored text".
    text: str | None


@dataclass(frozen=True)
class IndexResult:
    status: Literal["indexed", "unchanged", "empty"]
    chunks: int


def vector_literal(v: list[float]) -> str:
    return "[" + ",".join(f"{x:.7g}" for x in v) + "]"


class IndexService:
    def __init__(self, engine: AsyncEngine, embedder: Embedder, provider: str) -> None:
        self._engine = engine
        self._embedder = embedder
        self._provider = provider

    async def put(self, user_id: uuid.UUID, resource_id: uuid.UUID, data: IndexInput) -> IndexResult:
        async with self._engine.begin() as conn:
            if data.text is not None:
                await conn.execute(
                    text(
                        "INSERT INTO ai.documents (user_id, resource_id, text) VALUES (:u, :r, :t) "
                        "ON CONFLICT (user_id, resource_id) DO UPDATE SET text = EXCLUDED.text, updated_at = now()"
                    ),
                    {"u": user_id, "r": resource_id, "t": data.text},
                )
                body: str | None = data.text
            else:
                body = (
                    await conn.execute(
                        text("SELECT text FROM ai.documents WHERE user_id = :u AND resource_id = :r"),
                        {"u": user_id, "r": resource_id},
                    )
                ).scalar_one_or_none()
            document = build_document(data.title, data.description, data.notes, body)
            digest = hashlib.sha256(document.encode()).hexdigest()
            model = self._embedder.model_id

            existing = (
                await conn.execute(
                    text(
                        "SELECT count(*), count(DISTINCT content_hash), min(content_hash) FROM ai.chunks "
                        "WHERE user_id = :u AND resource_id = :r AND embedding_model = :m"
                    ),
                    {"u": user_id, "r": resource_id, "m": model},
                )
            ).one()
            if existing[0] > 0 and existing[1] == 1 and existing[2] == digest:
                # Same content, same model (NFR-REL-03): nothing to embed, but the collection may have changed.
                await conn.execute(
                    text("UPDATE ai.chunks SET collection_id = :c WHERE user_id = :u AND resource_id = :r"),
                    {"c": data.collection_id, "u": user_id, "r": resource_id},
                )
                return IndexResult("unchanged", existing[0])
            if not document:
                await conn.execute(
                    text("DELETE FROM ai.chunks WHERE user_id = :u AND resource_id = :r AND embedding_model = :m"),
                    {"u": user_id, "r": resource_id, "m": model},
                )
                return IndexResult("empty", 0)

        # The title is added to every chunk as a prefix, so it is left out of the text that is cut up.
        body_document = build_document(None, data.description, data.notes, body)
        chunks = chunk_document(body_document, data.title) if body_document else [(data.title or "").strip()]
        vectors: list[list[float]] = []
        for i in range(0, len(chunks), BATCH):
            vectors.extend(await self._embedder.embed_documents(chunks[i : i + BATCH]))

        # Replace the resource's chunks for this model in one transaction. Rows of other models stay, so search
        # keeps working while a profile switch re-indexes.
        async with self._engine.begin() as conn:
            await conn.execute(
                text("DELETE FROM ai.chunks WHERE user_id = :u AND resource_id = :r AND embedding_model = :m"),
                {"u": user_id, "r": resource_id, "m": model},
            )
            await conn.execute(
                text(
                    "INSERT INTO ai.chunks (id, user_id, resource_id, collection_id, chunk_index, content, "
                    "content_hash, embedding_model, embedding) "
                    "VALUES (:id, :u, :r, :c, :i, :content, :h, :m, CAST(:e AS public.vector))"
                ),
                [
                    {
                        "id": uuid.uuid4(),
                        "u": user_id,
                        "r": resource_id,
                        "c": data.collection_id,
                        "i": i,
                        "content": chunk,
                        "h": digest,
                        "m": model,
                        "e": vector_literal(vec),
                    }
                    for i, (chunk, vec) in enumerate(zip(chunks, vectors, strict=True))
                ],
            )
            await conn.execute(
                text(
                    "INSERT INTO ai.usage (id, user_id, operation, provider, model, tokens_in) "
                    "VALUES (:id, :u, 'embed', :p, :m, :t)"
                ),
                {
                    "id": uuid.uuid4(),
                    "u": user_id,
                    "p": self._provider,
                    "m": model,
                    "t": sum(len(c.split()) for c in chunks),
                },
            )
        return IndexResult("indexed", len(chunks))

    async def delete(self, user_id: uuid.UUID, resource_id: uuid.UUID, forget: bool = False) -> None:
        """Removes the resource's chunks. `forget` also drops the stored text (after the resource is purged)."""
        async with self._engine.begin() as conn:
            params = {"u": user_id, "r": resource_id}
            await conn.execute(text("DELETE FROM ai.chunks WHERE user_id = :u AND resource_id = :r"), params)
            if forget:
                await conn.execute(text("DELETE FROM ai.documents WHERE user_id = :u AND resource_id = :r"), params)

    async def purge_user(self, user_id: uuid.UUID) -> None:
        """Removes everything the AI service holds for a user (DR-06). Chat messages go with their sessions."""
        async with self._engine.begin() as conn:
            for table in ("chunks", "documents", "summaries", "chat_sessions", "usage"):
                await conn.execute(text(f"DELETE FROM ai.{table} WHERE user_id = :u"), {"u": user_id})  # noqa: S608
