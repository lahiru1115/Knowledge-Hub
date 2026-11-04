"""Chat: sessions, history and the streamed, cited answer (Doc 02 §4.4, §8.5)."""

import asyncio
import json
import logging
import uuid
from collections.abc import AsyncIterator
from typing import Any

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine

from app.errors import ApiError
from app.guardrails.citations import MarkerFilter
from app.prompts.chat import HISTORY_MESSAGES, NOTHING_FOUND, Source, build_messages
from app.providers import Providers
from app.providers.base import LlmUnavailable, Usage
from app.quota import Reservation, UsageMeter
from app.retrieval.hybrid import hybrid_chunks, snippet

log = logging.getLogger(__name__)

MAX_SOURCES = 8
#: About 3,000 tokens of sources (Doc 02 §4.3), counted in words at roughly 1.3 tokens each.
MAX_SOURCE_WORDS = 2300
TITLE_CHARS = 60


def sse(event: str, data: dict[str, Any]) -> str:
    return f"event: {event}\ndata: {json.dumps(data, ensure_ascii=False)}\n\n"


def title_from(question: str) -> str:
    q = " ".join(question.split())
    if len(q) <= TITLE_CHARS:
        return q
    return q[:TITLE_CHARS].rsplit(" ", 1)[0].rstrip(",;:- ") + "…"


def _session(row: Any) -> dict[str, Any]:
    scope = row.scope or {}
    return {
        "id": str(row.id),
        "title": row.title or "New chat",
        "scope_collection_id": scope.get("collection_id"),
        "created_at": row.created_at.isoformat(),
        "updated_at": row.updated_at.isoformat(),
    }


class ChatService:
    def __init__(self, engine: AsyncEngine) -> None:
        self._engine = engine

    # ---- sessions ----

    async def create(self, user_id: uuid.UUID, collection_id: uuid.UUID | None) -> dict[str, Any]:
        scope = {"collection_id": str(collection_id)} if collection_id else {}
        async with self._engine.begin() as conn:
            row = (
                await conn.execute(
                    text(
                        "INSERT INTO ai.chat_sessions (id, user_id, scope) VALUES (:id, :u, CAST(:s AS jsonb)) "
                        "RETURNING id, title, scope, created_at, updated_at"
                    ),
                    {"id": uuid.uuid4(), "u": user_id, "s": json.dumps(scope)},
                )
            ).one()
        return _session(row)

    async def list_sessions(self, user_id: uuid.UUID) -> list[dict[str, Any]]:
        async with self._engine.connect() as conn:
            rows = (
                await conn.execute(
                    text(
                        "SELECT id, title, scope, created_at, updated_at FROM ai.chat_sessions "
                        "WHERE user_id = :u ORDER BY updated_at DESC LIMIT 200"
                    ),
                    {"u": user_id},
                )
            ).all()
        return [_session(r) for r in rows]

    async def get(self, user_id: uuid.UUID, session_id: uuid.UUID) -> dict[str, Any]:
        async with self._engine.connect() as conn:
            row = await self._row(conn, user_id, session_id)
            messages = (
                await conn.execute(
                    text(
                        "SELECT id, role, content, citations, created_at FROM ai.chat_messages "
                        "WHERE session_id = :s ORDER BY created_at, id"
                    ),
                    {"s": session_id},
                )
            ).all()
        return {
            **_session(row),
            "messages": [
                {
                    "id": str(m.id),
                    "role": m.role,
                    "content": m.content,
                    "citations": m.citations or [],
                    "created_at": m.created_at.isoformat(),
                }
                for m in messages
            ],
        }

    async def rename(self, user_id: uuid.UUID, session_id: uuid.UUID, title: str) -> dict[str, Any]:
        async with self._engine.begin() as conn:
            row = (
                await conn.execute(
                    text(
                        "UPDATE ai.chat_sessions SET title = :t, updated_at = now() WHERE id = :s AND user_id = :u "
                        "RETURNING id, title, scope, created_at, updated_at"
                    ),
                    {"t": title.strip(), "s": session_id, "u": user_id},
                )
            ).one_or_none()
        if row is None:
            raise ApiError(404, "not_found")
        return _session(row)

    async def delete(self, user_id: uuid.UUID, session_id: uuid.UUID) -> None:
        async with self._engine.begin() as conn:
            deleted = (
                await conn.execute(
                    text("DELETE FROM ai.chat_sessions WHERE id = :s AND user_id = :u RETURNING id"),
                    {"s": session_id, "u": user_id},
                )
            ).one_or_none()
        if deleted is None:
            raise ApiError(404, "not_found")

    async def _row(self, conn: Any, user_id: uuid.UUID, session_id: uuid.UUID) -> Any:
        row = (
            await conn.execute(
                text(
                    "SELECT id, title, scope, created_at, updated_at FROM ai.chat_sessions "
                    "WHERE id = :s AND user_id = :u"
                ),
                {"s": session_id, "u": user_id},
            )
        ).one_or_none()
        if row is None:
            raise ApiError(404, "not_found")  # also for another user's session: nothing is revealed
        return row

    async def check_session(self, user_id: uuid.UUID, session_id: uuid.UUID) -> dict[str, Any]:
        async with self._engine.connect() as conn:
            return _session(await self._row(conn, user_id, session_id))

    # ---- asking ----

    async def _history(self, session_id: uuid.UUID) -> list[dict[str, str]]:
        async with self._engine.connect() as conn:
            rows = (
                await conn.execute(
                    text(
                        "SELECT role, content FROM (SELECT role, content, created_at, id FROM ai.chat_messages "
                        "WHERE session_id = :s ORDER BY created_at DESC, id DESC LIMIT :n) t ORDER BY created_at, id"
                    ),
                    {"s": session_id, "n": HISTORY_MESSAGES},
                )
            ).all()
        return [{"role": r.role, "content": r.content} for r in rows]

    async def _sources(
        self,
        providers: Providers,
        user_id: uuid.UUID,
        question: str,
        collection_id: uuid.UUID | None,
        min_similarity: float,
    ) -> list[Source]:
        chunks = await hybrid_chunks(self._engine, providers.embedder, user_id, question, collection_id=collection_id)
        sources: list[Source] = []
        words = 0
        for c in chunks:
            # Relevant means a word matched, or the meaning is close. The nearest chunk is always returned by the
            # vector search, so without this an unrelated question would be "answered" from unrelated pages.
            if not (c.keyword or (c.similarity is not None and c.similarity >= min_similarity)):
                continue
            title, _, body = c.content.partition("\n\n")
            n_words = len(body.split())
            if sources and words + n_words > MAX_SOURCE_WORDS:
                break
            words += n_words
            sources.append(Source(len(sources) + 1, str(c.resource_id), title, body or title))
            if len(sources) == MAX_SOURCES:
                break
        return sources

    async def _save(
        self,
        user_id: uuid.UUID,
        session_id: uuid.UUID,
        question: str,
        answer: str,
        citations: list[dict[str, Any]],
    ) -> uuid.UUID:
        message_id = uuid.uuid4()
        async with self._engine.begin() as conn:
            await conn.execute(
                text("INSERT INTO ai.chat_messages (id, session_id, role, content) VALUES (:i, :s, 'user', :c)"),
                {"i": uuid.uuid4(), "s": session_id, "c": question},
            )
            await conn.execute(
                text(
                    "INSERT INTO ai.chat_messages (id, session_id, role, content, citations, created_at) "
                    "VALUES (:i, :s, 'assistant', :c, CAST(:cit AS jsonb), now() + interval '1 millisecond')"
                ),
                {"i": message_id, "s": session_id, "c": answer, "cit": json.dumps(citations)},
            )
            await conn.execute(
                text(
                    "UPDATE ai.chat_sessions SET updated_at = now(), title = COALESCE(title, :t) "
                    "WHERE id = :s AND user_id = :u"
                ),
                {"t": title_from(question), "s": session_id, "u": user_id},
            )
        return message_id

    async def ask(
        self,
        user_id: uuid.UUID,
        session: dict[str, Any],
        question: str,
        providers: Providers,
        meter: UsageMeter,
        reservation: Reservation,
        *,
        min_similarity: float,
        total_seconds: float,
    ) -> AsyncIterator[str]:
        """The answer as server-sent events. The reservation is released if the answer does not complete."""
        session_id = uuid.UUID(session["id"])
        collection = session["scope_collection_id"]
        finished = False
        try:
            sources = await self._sources(
                providers, user_id, question, uuid.UUID(collection) if collection else None, min_similarity
            )
            history = await self._history(session_id)
            filt = MarkerFilter({s.n for s in sources})
            parts: list[str] = []
            usage = Usage()
            if not sources:
                parts.append(NOTHING_FOUND)
                yield sse("token", {"text": NOTHING_FOUND})
            else:
                system, messages = build_messages(question, history, sources)
                async with asyncio.timeout(total_seconds):
                    async for piece in providers.llm.stream_chat(system, messages):
                        if isinstance(piece, Usage):
                            usage = piece
                            continue
                        if out := filt.feed(piece):
                            parts.append(out)
                            yield sse("token", {"text": out})
                if tail := filt.flush():
                    parts.append(tail)
                    yield sse("token", {"text": tail})

            by_n = {s.n: s for s in sources}
            citations = [
                {
                    "n": n,
                    "resource_id": by_n[n].resource_id,
                    "title": by_n[n].title,
                    "quote": snippet(by_n[n].content, "", title_prefix=False),
                }
                for n in filt.used
            ]
            yield sse("citations", {"items": citations})
            message_id = await self._save(user_id, session_id, question, "".join(parts), citations)
            await meter.finish(reservation, usage.tokens_in, usage.tokens_out)
            finished = True
            yield sse(
                "done",
                {
                    "message_id": str(message_id),
                    "usage": {"tokens_in": usage.tokens_in, "tokens_out": usage.tokens_out},
                },
            )
        except (LlmUnavailable, TimeoutError):
            log.warning("The language model did not answer", exc_info=True)
            yield sse("error", {"code": "ai_unavailable"})
        except asyncio.CancelledError:
            raise  # the reader went away; the reservation is released below
        except Exception:
            log.exception("Chat failed")
            yield sse("error", {"code": "internal_error"})
        finally:
            if not finished:
                await asyncio.shield(meter.cancel(reservation))  # an answer that did not complete is not counted
