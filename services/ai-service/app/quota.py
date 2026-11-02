"""Usage metering and daily quotas (Doc 02 §4.6, FR-AI-10).

A call reserves its usage row *before* it runs, under a per-user-and-operation lock, so parallel requests cannot all
slip under the limit. If the work then fails, the reservation is removed, so failures do not use up the quota.
"""

import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine

from app.errors import ApiError

QUOTA_OPERATIONS = ("summarize", "suggest_tags", "search", "chat")


def day_bounds(now: datetime | None = None) -> tuple[datetime, datetime]:
    """The current UTC day: its start and the moment it resets."""
    now = now or datetime.now(UTC)
    start = now.replace(hour=0, minute=0, second=0, microsecond=0)
    return start, start + timedelta(days=1)


@dataclass(frozen=True)
class Reservation:
    id: uuid.UUID
    user_id: uuid.UUID


class UsageMeter:
    def __init__(self, engine: AsyncEngine, quotas: dict[str, int | None], provider: str) -> None:
        self._engine = engine
        self._quotas = quotas
        self._provider = provider

    async def reserve(self, user_id: uuid.UUID, operation: str, model: str, now: datetime | None = None) -> Reservation:
        now = now or datetime.now(UTC)
        start, reset = day_bounds(now)
        limit = self._quotas.get(operation)
        rid = uuid.uuid4()
        async with self._engine.begin() as conn:
            await conn.execute(
                text("SELECT pg_advisory_xact_lock(hashtextextended(:k, 0))"), {"k": f"{user_id}:{operation}"}
            )
            if limit is not None:
                used = (
                    await conn.execute(
                        text(
                            "SELECT count(*) FROM ai.usage WHERE user_id = :u AND operation = :o AND created_at >= :s"
                        ),
                        {"u": user_id, "o": operation, "s": start},
                    )
                ).scalar_one()
                if used >= limit:
                    wait = max(1, int((reset - now).total_seconds()))
                    raise ApiError(429, "ai_quota_exceeded", reset_at=reset.isoformat(), retry_after=wait)
            await conn.execute(
                text(
                    "INSERT INTO ai.usage (id, user_id, operation, provider, model, created_at) "
                    "VALUES (:id, :u, :o, :p, :m, :t)"
                ),
                {"id": rid, "u": user_id, "o": operation, "p": self._provider, "m": model, "t": now},
            )
        return Reservation(rid, user_id)

    async def finish(self, r: Reservation, tokens_in: int = 0, tokens_out: int = 0, cost_usd: float = 0.0) -> None:
        async with self._engine.begin() as conn:
            await conn.execute(
                text(
                    "UPDATE ai.usage SET tokens_in = :i, tokens_out = :o, cost_usd = :c WHERE id = :id AND user_id = :u"
                ),
                {"i": tokens_in, "o": tokens_out, "c": cost_usd, "id": r.id, "u": r.user_id},
            )

    async def cancel(self, r: Reservation) -> None:
        async with self._engine.begin() as conn:
            await conn.execute(
                text("DELETE FROM ai.usage WHERE id = :id AND user_id = :u"), {"id": r.id, "u": r.user_id}
            )

    async def today(self, user_id: uuid.UUID, now: datetime | None = None) -> dict[str, object]:
        start, reset = day_bounds(now)
        async with self._engine.connect() as conn:
            rows = (
                await conn.execute(
                    text(
                        "SELECT operation, count(*) FROM ai.usage WHERE user_id = :u AND created_at >= :s "
                        "AND operation = ANY(:ops) GROUP BY operation"
                    ),
                    {"u": user_id, "s": start, "ops": list(QUOTA_OPERATIONS)},
                )
            ).all()
        used = {op: n for op, n in rows}
        return {
            "reset_at": reset.isoformat(),
            "items": [
                {"operation": op, "used": used.get(op, 0), "limit": self._quotas.get(op)} for op in QUOTA_OPERATIONS
            ],
        }
