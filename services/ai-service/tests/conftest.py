import os
import time
import uuid
from collections.abc import AsyncIterator, Callable
from typing import Annotated

import jwt
import pytest
from fastapi import Depends
from httpx import ASGITransport, AsyncClient

from app.auth import Caller, caller, current_user, require_scope
from app.config import Settings
from app.main import create_app

SECRET = "unit-test-service-secret-0123456789abcdef"
APP_URL = os.environ.get("AI_TEST_APP_URL", "postgresql://ai_app:ai_app_dev@127.0.0.1:55432/knowledge_hub")
MIGRATOR_URL = os.environ.get(
    "AI_TEST_MIGRATOR_URL", "postgresql://ai_migrator:ai_migrator_dev@127.0.0.1:55432/knowledge_hub"
)


def settings() -> Settings:
    return Settings(AI_DATABASE_URL=APP_URL, SERVICE_JWT_SECRET=SECRET, REINDEX_ENABLED=False)  # type: ignore[call-arg]


def make_token(
    sub: str | uuid.UUID = "",
    *,
    scope: str | None = None,
    secret: str = SECRET,
    iss: str = "core-api",
    aud: str = "ai-service",
    lifetime: int = 60,
    issued_ago: int = 0,
    algorithm: str = "HS256",
    drop: tuple[str, ...] = (),
) -> str:
    now = int(time.time()) - issued_ago
    claims: dict[str, object] = {
        "iss": iss,
        "aud": aud,
        "sub": str(sub or uuid.uuid4()),
        "jti": uuid.uuid4().hex,
        "iat": now,
        "exp": now + lifetime,
    }
    if scope is not None:
        claims["scope"] = scope
    for name in drop:
        claims.pop(name, None)
    return jwt.encode(claims, secret, algorithm=algorithm)


@pytest.fixture
def app():
    a = create_app(settings())

    # Routes that exist only to exercise the authentication rules.
    @a.get("/_t/user")
    async def user_route(user: Annotated[uuid.UUID, Depends(current_user)]) -> dict[str, str]:
        return {"user": str(user)}

    @a.get("/_t/purge")
    async def purge_route(who: Annotated[Caller, Depends(require_scope("users:purge"))]) -> dict[str, str]:
        return {"jti": who.jti}

    @a.get("/_t/who")
    async def who_route(who: Annotated[Caller, Depends(caller)]) -> dict[str, bool]:
        return {"system": who.is_system}

    return a


@pytest.fixture
async def client(app) -> AsyncIterator[AsyncClient]:
    async with app.router.lifespan_context(app):
        async with AsyncClient(transport=ASGITransport(app=app), base_url="http://ai") as c:
            yield c


Auth = Callable[[str], dict[str, str]]


@pytest.fixture
def bearer() -> Auth:
    return lambda token: {"Authorization": f"Bearer {token}"}
