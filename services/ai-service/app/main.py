from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app import errors
from app.api import ai, chat, health, index, search
from app.config import Settings, get_settings
from app.db import make_engine
from app.providers import build_providers


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        app.state.settings = settings
        app.state.engine = make_engine(settings)
        app.state.providers = build_providers(settings)
        try:
            yield
        finally:
            await app.state.engine.dispose()

    app = FastAPI(title="Knowledge Hub AI service", version="0.1.0", lifespan=lifespan, docs_url=None, redoc_url=None)
    app.dependency_overrides[get_settings] = lambda: settings
    errors.install(app)
    app.include_router(health.router)
    app.include_router(index.router)
    app.include_router(ai.router)
    app.include_router(search.router)
    app.include_router(chat.router)
    return app
