import asyncio
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app import errors
from app.api import ai, chat, health, index, search, usage
from app.config import Settings, get_settings
from app.db import make_engine
from app.pricing import CostCalculator, parse_price_table
from app.providers import build_providers
from app.quota import CostGuard
from app.reindex import reindex_forever


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        app.state.settings = settings
        app.state.engine = make_engine(settings)
        app.state.providers = build_providers(settings)  # a profile that cannot run fails here, at start-up
        app.state.costs = CostCalculator(parse_price_table(settings.price_table))
        app.state.cost_guard = CostGuard(app.state.engine, settings.monthly_cost_cap_usd)
        task = (
            asyncio.create_task(reindex_forever(app, settings.reindex_interval_seconds))
            if settings.reindex_enabled
            else None
        )
        try:
            yield
        finally:
            if task is not None:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
            await app.state.engine.dispose()

    app = FastAPI(title="Knowledge Hub AI service", version="0.1.0", lifespan=lifespan, docs_url=None, redoc_url=None)
    app.dependency_overrides[get_settings] = lambda: settings
    errors.install(app)
    app.include_router(health.router)
    app.include_router(index.router)
    app.include_router(ai.router)
    app.include_router(search.router)
    app.include_router(chat.router)
    app.include_router(usage.router)
    return app
