import hashlib
import logging
import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Request
from pydantic import BaseModel, Field
from sqlalchemy import text

from app.auth import current_user
from app.errors import ApiError
from app.providers.base import NotEnoughContent
from app.providers.free.tagger import normalise_tag
from app.quota import UsageMeter

router = APIRouter(tags=["ai"])
log = logging.getLogger(__name__)


def meter(request: Request) -> UsageMeter:
    p = request.app.state.providers
    return UsageMeter(request.app.state.engine, request.app.state.settings.quotas(), p.profile)


Meter = Annotated[UsageMeter, Depends(meter)]
User = Annotated[uuid.UUID, Depends(current_user)]


async def stored_text(request: Request, user: uuid.UUID, resource_id: uuid.UUID | None) -> str | None:
    """The page text kept when the resource was indexed; None when there is none."""
    if resource_id is None:
        return None
    async with request.app.state.engine.connect() as conn:
        found = (
            await conn.execute(
                text("SELECT text FROM ai.documents WHERE user_id = :u AND resource_id = :r"),
                {"u": user, "r": resource_id},
            )
        ).scalar_one_or_none()
    return str(found) if found is not None else None


class SummarizeRequest(BaseModel):
    #: When set and `text` is empty, the text stored at indexing time is used.
    resource_id: uuid.UUID | None = None
    title: str | None = Field(default=None, max_length=300)
    description: str | None = Field(default=None, max_length=2000)
    text: str | None = Field(default=None, max_length=500_000)


class SummarizeResponse(BaseModel):
    summary: str
    source: str
    model: str


@router.post("/summarize")
async def summarize(body: SummarizeRequest, user: User, request: Request, usage: Meter) -> SummarizeResponse:
    summarizer = request.app.state.providers.summarizer
    page_text = body.text if body.text is not None else await stored_text(request, user, body.resource_id)
    # The key covers everything the summary depends on, so an edit to the text gives a new summary.
    digest = hashlib.sha256(
        "\x00".join([body.title or "", body.description or "", page_text or ""]).encode()
    ).hexdigest()

    reservation = await usage.reserve(user, "summarize", summarizer.model_id)
    try:
        async with request.app.state.engine.connect() as conn:
            cached = (
                await conn.execute(
                    text(
                        "SELECT summary, source FROM ai.summaries "
                        "WHERE user_id = :u AND content_hash = :h AND model = :m"
                    ),
                    {"u": user, "h": digest, "m": summarizer.model_id},
                )
            ).one_or_none()
        if cached:
            await usage.finish(reservation)
            return SummarizeResponse(summary=cached[0], source=cached[1], model=summarizer.model_id)
        result = await summarizer.summarize(body.title, body.description, page_text)
        async with request.app.state.engine.begin() as conn:
            await conn.execute(
                text(
                    "INSERT INTO ai.summaries (user_id, content_hash, model, summary, source) "
                    "VALUES (:u, :h, :m, :s, :src) ON CONFLICT (user_id, content_hash, model) DO NOTHING"
                ),
                {"u": user, "h": digest, "m": result.model, "s": result.text, "src": result.source},
            )
        await usage.finish(reservation, len((page_text or body.description or "").split()), len(result.text.split()))
        return SummarizeResponse(summary=result.text, source=result.source, model=result.model)
    except NotEnoughContent:
        await usage.cancel(reservation)  # nothing was produced, so it does not count
        raise ApiError(422, "not_enough_content") from None
    except BaseException:
        await usage.cancel(reservation)
        raise


class SuggestTagsRequest(BaseModel):
    resource_id: uuid.UUID | None = None
    title: str | None = Field(default=None, max_length=300)
    description: str | None = Field(default=None, max_length=2000)
    text: str | None = Field(default=None, max_length=20_000)
    existing_tags: list[str] = Field(default_factory=list, max_length=500)


class TagSuggestion(BaseModel):
    name: str
    existing: bool


class SuggestTagsResponse(BaseModel):
    items: list[TagSuggestion]


@router.post("/suggest-tags")
async def suggest_tags(body: SuggestTagsRequest, user: User, request: Request, usage: Meter) -> SuggestTagsResponse:
    providers = request.app.state.providers
    page_text = body.text if body.text is not None else await stored_text(request, user, body.resource_id)
    reservation = await usage.reserve(user, "suggest_tags", providers.embedder.model_id)
    try:
        names = await providers.tagger.suggest(body.title, body.description, page_text, body.existing_tags)
        await usage.finish(reservation, len((page_text or "")[:2000].split()), len(names))
    except BaseException:
        await usage.cancel(reservation)
        raise
    known = {t for t in (normalise_tag(e) for e in body.existing_tags) if t}
    return SuggestTagsResponse(items=[TagSuggestion(name=n, existing=n in known) for n in names])


@router.get("/usage")
async def my_usage(user: User, usage: Meter) -> dict[str, object]:
    return await usage.today(user)
