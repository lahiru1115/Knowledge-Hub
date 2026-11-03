import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Request, Response
from pydantic import BaseModel, Field

from app.auth import Caller, current_user, require_scope
from app.indexing import IndexInput, IndexService

router = APIRouter(tags=["index"])


class IndexRequest(BaseModel):
    title: str | None = Field(default=None, max_length=300)
    description: str | None = Field(default=None, max_length=2000)
    notes: str | None = Field(default=None, max_length=100_000)
    collection_id: uuid.UUID | None = None
    #: The extracted page text. Leave out to keep the stored text (a title or notes edit).
    text: str | None = Field(default=None, max_length=500_000)


class IndexResponse(BaseModel):
    status: str
    chunks: int


def index_service(request: Request) -> IndexService:
    p = request.app.state.providers
    return IndexService(request.app.state.engine, p.embedder, p.profile, request.app.state.costs)


Service = Annotated[IndexService, Depends(index_service)]


@router.put("/index/{resource_id}")
async def put_index(
    resource_id: uuid.UUID, body: IndexRequest, user: Annotated[uuid.UUID, Depends(current_user)], service: Service
) -> IndexResponse:
    result = await service.put(
        user, resource_id, IndexInput(body.title, body.description, body.notes, body.collection_id, body.text)
    )
    return IndexResponse(status=result.status, chunks=result.chunks)


@router.delete("/index/{resource_id}", status_code=204)
async def delete_index(
    resource_id: uuid.UUID,
    user: Annotated[uuid.UUID, Depends(current_user)],
    service: Service,
    forget: bool = False,
) -> Response:
    await service.delete(user, resource_id, forget)
    return Response(status_code=204)


@router.delete("/users/{user_id}", status_code=204)
async def purge_user(
    user_id: uuid.UUID, _: Annotated[Caller, Depends(require_scope("users:purge"))], service: Service
) -> Response:
    await service.purge_user(user_id)
    return Response(status_code=204)
