"""Usage across users, for the administrator's screen (Doc 02 §5.6). System token with scope `usage:read_all` only."""

import datetime as dt
import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Request
from pydantic import BaseModel
from sqlalchemy import text

from app.auth import Caller, require_scope

router = APIRouter(tags=["usage"])

DAYS = 30


class UsageRow(BaseModel):
    date: dt.date
    user_id: uuid.UUID | None = None
    operation: str
    calls: int
    tokens_in: int
    tokens_out: int
    cost_usd: float


class UsageResponse(BaseModel):
    items: list[UsageRow]


@router.get("/admin/usage")
async def usage(
    request: Request,
    _: Annotated[Caller, Depends(require_scope("usage:read_all"))],
    user_id: uuid.UUID | None = None,
) -> UsageResponse:
    """Per UTC day for the last 30 days (today included), newest first. One user, or totals over everyone."""
    per_user = user_id is not None
    sql = (
        "SELECT (created_at AT TIME ZONE 'UTC')::date AS day, "
        + ("user_id, " if per_user else "")
        + "operation, count(*) AS calls, coalesce(sum(tokens_in), 0) AS tokens_in, "
        "coalesce(sum(tokens_out), 0) AS tokens_out, coalesce(sum(cost_usd), 0) AS cost_usd "
        "FROM ai.usage "
        "WHERE created_at >= date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' "
        "- make_interval(days => :back) "
        + ("AND user_id = :u " if per_user else "")
        + "GROUP BY day, "
        + ("user_id, " if per_user else "")
        + "operation ORDER BY day DESC, operation"
    )  # only fixed fragments are joined; the values are bound
    async with request.app.state.engine.connect() as conn:
        found = (await conn.execute(text(sql), {"back": DAYS - 1, "u": user_id})).mappings().all()
    return UsageResponse(
        items=[
            UsageRow(
                date=r["day"],
                user_id=user_id if per_user else None,
                operation=r["operation"],
                calls=r["calls"],
                tokens_in=int(r["tokens_in"]),
                tokens_out=int(r["tokens_out"]),
                cost_usd=float(r["cost_usd"]),
            )
            for r in found
        ]
    )
