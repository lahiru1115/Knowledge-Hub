import hashlib
import logging
import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Request
from pydantic import BaseModel, Field
from sqlalchemy import text

from app.auth import current_user
from app.errors import ApiError
from app.providers import Providers
from app.providers.base import NotEnoughContent, Usage
from app.providers.free.tagger import normalise_tag
from app.quota import UsageMeter, estimate_tokens

router = APIRouter(tags=["ai"])
log = logging.getLogger(__name__)


def meter(request: Request) -> UsageMeter:
    state = request.app.state
    p = state.providers
    # Paid providers also have a daily spending cap per user; the free profile has none.
    cap = state.settings.daily_cost_cap_usd if p.profile == "claude" else None
    return UsageMeter(state.engine, state.settings.quotas(), p.profile, state.costs, cap)


async def active_providers(request: Request) -> Providers:
    """The providers for a summary, tag suggestions or chat: the free ones once the monthly spending cap is reached."""
    p: Providers = request.app.state.providers
    if p.fallback is not None and await request.app.state.cost_guard.monthly_cap_reached():
        log.warning("The monthly AI cost cap is reached; using the free providers")
        return p.fallback
    return p


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
    chosen = await active_providers(request)
    summarizer = chosen.summarizer
    page_text = body.text if body.text is not None else await stored_text(request, user, body.resource_id)
    # The key covers everything the summary depends on, so an edit to the text gives a new summary.
    digest = hashlib.sha256(
        "\x00".join([body.title or "", body.description or "", page_text or ""]).encode()
    ).hexdigest()

    reservation = await usage.reserve(user, "summarize", summarizer.model_id, provider=chosen.profile)
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
        used = result.usage
        if (
            used.tokens_in == 0 and used.tokens_out == 0
        ):  # a provider that does not count tokens: estimate from the words
            used = Usage(
                estimate_tokens(len((page_text or body.description or "").split())),
                estimate_tokens(len(result.text.split())),
            )
        await usage.finish(reservation, used.tokens_in, used.tokens_out)
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
    providers = await active_providers(request)
    page_text = body.text if body.text is not None else await stored_text(request, user, body.resource_id)
    model = getattr(providers.tagger, "model_id", providers.embedder.model_id)  # a paid tagger is its own model
    reservation = await usage.reserve(user, "suggest_tags", model, provider=providers.profile)
    try:
        names = await providers.tagger.suggest(body.title, body.description, page_text, body.existing_tags)
        used: Usage = getattr(names, "usage", Usage())
        if used.tokens_in == 0 and used.tokens_out == 0:
            used = Usage(estimate_tokens(len((page_text or "")[:2000].split())), len(names))
        await usage.finish(reservation, used.tokens_in, used.tokens_out)
    except BaseException:
        await usage.cancel(reservation)
        raise
    known = {t for t in (normalise_tag(e) for e in body.existing_tags) if t}
    return SuggestTagsResponse(items=[TagSuggestion(name=n, existing=n in known) for n in names])


@router.get("/usage")
async def my_usage(user: User, usage: Meter) -> dict[str, object]:
    return await usage.today(user)
