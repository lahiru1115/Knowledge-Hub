from collections.abc import AsyncIterator

from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine

from app.config import Settings


def make_engine(settings: Settings) -> AsyncEngine:
    url = settings.ai_database_url
    # Accept the plain postgresql:// form used by the other tools and select the async psycopg driver.
    for prefix in ("postgresql://", "postgres://"):
        if url.startswith(prefix):
            url = "postgresql+psycopg://" + url[len(prefix) :]
    return create_async_engine(url, pool_pre_ping=True)


async def sessions(factory: async_sessionmaker[AsyncSession]) -> AsyncIterator[AsyncSession]:
    async with factory() as session:
        yield session
