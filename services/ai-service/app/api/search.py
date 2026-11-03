import uuid
from typing import Annotated

from fastapi import APIRouter, Request
from pydantic import BaseModel, Field

from app.api.ai import Meter, User
from app.quota import estimate_tokens
from app.retrieval.hybrid import hybrid_search, related, snippet

router = APIRouter(tags=["search"])


class SearchRequest(BaseModel):
    query: str = Field(min_length=1, max_length=500)
    limit: int = Field(default=20, ge=1, le=50)
    collection_id: uuid.UUID | None = None


class SearchItem(BaseModel):
    resource_id: uuid.UUID
    snippet: str
    score: float


class SearchResponse(BaseModel):
    items: list[SearchItem]


@router.post("/search")
async def search(body: SearchRequest, user: User, request: Request, usage: Meter) -> SearchResponse:
    embedder = request.app.state.providers.embedder
    reservation = await usage.reserve(user, "search", embedder.model_id)
    try:
        hits = await hybrid_search(
            request.app.state.engine,
            embedder,
            user,
            body.query.strip(),
            limit=body.limit,
            collection_id=body.collection_id,
        )
        await usage.finish(reservation, estimate_tokens(len(body.query.split())), 0)
    except BaseException:
        await usage.cancel(reservation)  # a search that failed does not use up the day's allowance
        raise
    return SearchResponse(
        items=[
            SearchItem(resource_id=h.resource_id, snippet=snippet(h.content, body.query), score=h.score) for h in hits
        ]
    )


class RelatedItem(BaseModel):
    resource_id: uuid.UUID
    score: float


class RelatedResponse(BaseModel):
    items: list[RelatedItem]


@router.get("/resources/{resource_id}/related")
async def related_resources(
    resource_id: uuid.UUID, user: User, request: Request, limit: Annotated[int, Field(ge=1, le=20)] = 5
) -> RelatedResponse:
    pairs = await related(
        request.app.state.engine, request.app.state.providers.embedder, user, resource_id, limit=limit
    )
    return RelatedResponse(items=[RelatedItem(resource_id=rid, score=score) for rid, score in pairs])
