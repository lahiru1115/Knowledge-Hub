"""Re-embedding after the embedding model changes (FR-AI-01, Doc 02 §7.3).

Chunks are stored with the model that embedded them, and search only reads the current model's. After a switch,
resources that only have chunks from another model are re-embedded here, a few at a time in the background. Their
chunk text is reused as it is (it already carries the title), so nothing has to be sent again from the Core API.
Once a resource has chunks from the current model, its old ones are removed.
"""

import asyncio
import logging
import uuid
from typing import Any

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine

from app.indexing import vector_literal
from app.pricing import CostCalculator
from app.providers.base import Embedder
from app.quota import estimate_tokens

log = logging.getLogger(__name__)
BATCH = 32


class ReindexService:
    def __init__(
        self, engine: AsyncEngine, embedder: Embedder, provider: str, costs: CostCalculator | None = None
    ) -> None:
        self._engine = engine
        self._embedder = embedder
        self._provider = provider
        self._costs = costs or CostCalculator()

    async def pending(self) -> int:
        """Resources that still have only another model's chunks."""
        async with self._engine.connect() as conn:
            return int(
                (
                    await conn.execute(
                        text(
                            "SELECT count(*) FROM (SELECT DISTINCT user_id, resource_id FROM ai.chunks c "
                            "WHERE embedding_model <> :m AND NOT EXISTS (SELECT 1 FROM ai.chunks n "
                            "WHERE n.user_id = c.user_id AND n.resource_id = c.resource_id "
                            "AND n.embedding_model = :m)) t"
                        ),
                        {"m": self._embedder.model_id},
                    )
                ).scalar_one()
            )

    async def run_once(self, batch: int = 20) -> int:
        """Re-embeds up to `batch` resources. Returns how many it finished; stops early if the model is unavailable."""
        async with self._engine.connect() as conn:
            todo = (
                await conn.execute(
                    text(
                        "SELECT DISTINCT user_id, resource_id FROM ai.chunks c WHERE embedding_model <> :m "
                        "AND NOT EXISTS (SELECT 1 FROM ai.chunks n WHERE n.user_id = c.user_id "
                        "AND n.resource_id = c.resource_id AND n.embedding_model = :m) LIMIT :n"
                    ),
                    {"m": self._embedder.model_id, "n": batch},
                )
            ).all()
        done = 0
        for user_id, resource_id in todo:
            try:
                await self._one(user_id, resource_id)
                done += 1
            except Exception:
                # The new model is not answering (or this resource is odd). Stop; the next run starts again from here.
                log.warning("Re-embedding stopped at resource %s", resource_id, exc_info=True)
                break
        return done

    async def _one(self, user_id: uuid.UUID, resource_id: uuid.UUID) -> None:
        model = self._embedder.model_id
        async with self._engine.connect() as conn:
            rows = (
                await conn.execute(
                    text(
                        "SELECT chunk_index, content, collection_id, content_hash FROM ai.chunks "
                        "WHERE user_id = :u AND resource_id = :r AND embedding_model = ("
                        "SELECT embedding_model FROM ai.chunks WHERE user_id = :u AND resource_id = :r "
                        "AND embedding_model <> :m ORDER BY embedding_model LIMIT 1) ORDER BY chunk_index"
                    ),
                    {"u": user_id, "r": resource_id, "m": model},
                )
            ).all()
        if not rows:
            return
        vectors: list[list[float]] = []
        for i in range(0, len(rows), BATCH):
            vectors.extend(await self._embedder.embed_documents([r.content for r in rows[i : i + BATCH]]))
        tokens = estimate_tokens(sum(len(r.content.split()) for r in rows))
        async with self._engine.begin() as conn:
            params: list[dict[str, Any]] = [
                {
                    "id": uuid.uuid4(),
                    "u": user_id,
                    "r": resource_id,
                    "c": r.collection_id,
                    "i": r.chunk_index,
                    "content": r.content,
                    "h": r.content_hash,
                    "m": model,
                    "e": vector_literal(v),
                }
                for r, v in zip(rows, vectors, strict=True)
            ]
            await conn.execute(
                text(
                    "INSERT INTO ai.chunks (id, user_id, resource_id, collection_id, chunk_index, content, "
                    "content_hash, embedding_model, embedding) "
                    "VALUES (:id, :u, :r, :c, :i, :content, :h, :m, CAST(:e AS public.vector))"
                ),
                params,
            )
            await conn.execute(
                text("DELETE FROM ai.chunks WHERE user_id = :u AND resource_id = :r AND embedding_model <> :m"),
                {"u": user_id, "r": resource_id, "m": model},
            )
            await conn.execute(
                text(
                    "INSERT INTO ai.usage (id, user_id, operation, provider, model, tokens_in, cost_usd) "
                    "VALUES (:id, :u, 'embed', :p, :m, :t, :cost)"
                ),
                {
                    "id": uuid.uuid4(),
                    "u": user_id,
                    "p": self._provider,
                    "m": model,
                    "t": tokens,
                    "cost": self._costs.cost(model, tokens),
                },
            )


async def reindex_forever(app: Any, interval: float) -> None:
    """Runs until cancelled. Reads the providers each time, so a restart with a new model starts working at once."""
    while True:
        try:
            p = app.state.providers
            n = await ReindexService(app.state.engine, p.embedder, p.profile, app.state.costs).run_once()
            if n > 0:
                log.info("Re-embedded %d resources", n)
                continue  # there may be more: no pause
        except asyncio.CancelledError:
            raise
        except Exception:
            log.warning("Re-embedding failed", exc_info=True)
        await asyncio.sleep(interval)
