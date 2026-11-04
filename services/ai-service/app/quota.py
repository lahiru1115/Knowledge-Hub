"""Usage metering and daily quotas (Doc 02 §4.6, FR-AI-10).

A call reserves its usage row *before* it runs, under a per-user-and-operation lock, so parallel requests cannot all
slip under the limit. If the work then fails, the reservation is removed, so failures do not use up the quota.
"""

import time
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine

from app.errors import ApiError
from app.pricing import CostCalculator

QUOTA_OPERATIONS = ("summarize", "suggest_tags", "search", "chat")


def day_bounds(now: datetime | None = None) -> tuple[datetime, datetime]:
    """The current UTC day: its start and the moment it resets."""
    now = now or datetime.now(UTC)
    start = now.replace(hour=0, minute=0, second=0, microsecond=0)
    return start, start + timedelta(days=1)


def estimate_tokens(words: int) -> int:
    """Roughly 4 tokens for every 3 words of English, for providers that do not report their own count."""
    return (words * 4 + 2) // 3


#: Tokens a call of each kind is assumed to use while it runs, so calls started together count against the daily cost
#: cap before any of them has finished. Generous on purpose: finish() replaces the estimate with the real figure, and
#: cancel() removes it.
ESTIMATED_TOKENS: dict[str, tuple[int, int]] = {
    "summarize": (4000, 400),
    "suggest_tags": (2000, 100),
    "chat": (6000, 800),
    "embed": (2000, 0),
    "search": (50, 0),
}


@dataclass(frozen=True)
class Reservation:
    id: uuid.UUID
    user_id: uuid.UUID
    model: str = ""


class UsageMeter:
    def __init__(
        self,
        engine: AsyncEngine,
        quotas: dict[str, int | None],
        provider: str,
        costs: CostCalculator | None = None,
        daily_cost_cap: float | None = None,
    ) -> None:
        self._engine = engine
        self._quotas = quotas
        self._provider = provider
        self._costs = costs or CostCalculator()
        self._daily_cost_cap = daily_cost_cap

    async def reserve(
        self,
        user_id: uuid.UUID,
        operation: str,
        model: str,
        now: datetime | None = None,
        provider: str | None = None,
    ) -> Reservation:
        now = now or datetime.now(UTC)
        start, reset = day_bounds(now)
        limit = self._quotas.get(operation)
        rid = uuid.uuid4()
        capped = self._daily_cost_cap is not None and self._costs.has(model)
        estimate = self._costs.cost(model, *ESTIMATED_TOKENS.get(operation, (0, 0))) if self._costs.has(model) else 0.0
        async with self._engine.begin() as conn:
            if capped:
                # One lock per user for the spending cap, taken before the per-operation one (always in this order, so
                # two calls cannot wait on each other): a chat and a summary started together check the cap in turn.
                await conn.execute(
                    text("SELECT pg_advisory_xact_lock(hashtextextended(:k, 0))"), {"k": f"{user_id}:cost"}
                )
            await conn.execute(
                text("SELECT pg_advisory_xact_lock(hashtextextended(:k, 0))"), {"k": f"{user_id}:{operation}"}
            )
            # A paid model also has a spending cap per user per day (Doc 02 §4.6), on top of the count limits. Calls
            # still running count with their estimate, so they cannot all slip under it together.
            if capped and self._daily_cost_cap is not None:
                spent = (
                    await conn.execute(
                        text("SELECT coalesce(sum(cost_usd), 0) FROM ai.usage WHERE user_id = :u AND created_at >= :s"),
                        {"u": user_id, "s": start},
                    )
                ).scalar_one()
                if float(spent) >= self._daily_cost_cap:
                    wait = max(1, int((reset - now).total_seconds()))
                    raise ApiError(429, "ai_quota_exceeded", reset_at=reset.isoformat(), retry_after=wait)
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
                    "INSERT INTO ai.usage (id, user_id, operation, provider, model, cost_usd, created_at) "
                    "VALUES (:id, :u, :o, :p, :m, :c, :t)"
                ),
                {
                    "id": rid,
                    "u": user_id,
                    "o": operation,
                    "p": provider or self._provider,
                    "m": model,
                    "c": estimate,
                    "t": now,
                },
            )
        return Reservation(rid, user_id, model)

    async def finish(
        self, r: Reservation, tokens_in: int = 0, tokens_out: int = 0, cost_usd: float | None = None
    ) -> None:
        """Records what the call used. The cost is worked out from the model's price unless it is given."""
        if cost_usd is None:
            cost_usd = self._costs.cost(r.model, tokens_in, tokens_out)
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


class CostGuard:
    """The deployment-wide monthly spending cap (Doc 02 §4.6). Once reached, paid operations use the free providers."""

    def __init__(self, engine: AsyncEngine, monthly_cap: float, cache_seconds: float = 60.0) -> None:
        self._engine = engine
        self._cap = monthly_cap
        self._cache_seconds = cache_seconds
        self._checked = float("-inf")
        self._reached = False

    async def monthly_spend(self, now: datetime | None = None) -> float:
        now = now or datetime.now(UTC)
        month = now.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
        async with self._engine.connect() as conn:
            spent = (
                await conn.execute(
                    text("SELECT coalesce(sum(cost_usd), 0) FROM ai.usage WHERE created_at >= :m"), {"m": month}
                )
            ).scalar_one()
        return float(spent)

    async def monthly_cap_reached(self) -> bool:
        # Remembered for a minute: this is asked on every paid call, and the spend moves slowly.
        if time.monotonic() - self._checked > self._cache_seconds:
            self._reached = await self.monthly_spend() >= self._cap
            self._checked = time.monotonic()
        return self._reached
