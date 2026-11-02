import uuid
from typing import Annotated, Any

from fastapi import APIRouter, Depends, Request, Response
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

from app.api.ai import Meter, User
from app.chat import ChatService

router = APIRouter(tags=["chat"])


def chat_service(request: Request) -> ChatService:
    return ChatService(request.app.state.engine)


Chats = Annotated[ChatService, Depends(chat_service)]


class CreateSession(BaseModel):
    scope_collection_id: uuid.UUID | None = None


class RenameSession(BaseModel):
    title: str = Field(min_length=1, max_length=200)


class Ask(BaseModel):
    content: str = Field(min_length=1, max_length=4000)


@router.post("/chat/sessions", status_code=201)
async def create_session(body: CreateSession, user: User, chats: Chats) -> dict[str, Any]:
    return await chats.create(user, body.scope_collection_id)


@router.get("/chat/sessions")
async def list_sessions(user: User, chats: Chats) -> dict[str, Any]:
    return {"items": await chats.list_sessions(user)}


@router.get("/chat/sessions/{session_id}")
async def get_session(session_id: uuid.UUID, user: User, chats: Chats) -> dict[str, Any]:
    return await chats.get(user, session_id)


@router.patch("/chat/sessions/{session_id}")
async def rename_session(session_id: uuid.UUID, body: RenameSession, user: User, chats: Chats) -> dict[str, Any]:
    return await chats.rename(user, session_id, body.title)


@router.delete("/chat/sessions/{session_id}", status_code=204)
async def delete_session(session_id: uuid.UUID, user: User, chats: Chats) -> Response:
    await chats.delete(user, session_id)
    return Response(status_code=204)


@router.post("/chat/sessions/{session_id}/messages")
async def ask(
    session_id: uuid.UUID, body: Ask, user: User, request: Request, chats: Chats, usage: Meter
) -> StreamingResponse:
    session = await chats.check_session(user, session_id)  # 404 before anything is streamed
    providers = request.app.state.providers
    settings = request.app.state.settings
    reservation = await usage.reserve(user, "chat", providers.llm.model_id)  # 429 before anything is streamed
    stream = chats.ask(
        user,
        session,
        body.content.strip(),
        providers,
        usage,
        reservation,
        min_similarity=settings.chat_min_similarity,
        total_seconds=settings.chat_total_seconds,
    )
    return StreamingResponse(
        stream,
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )
