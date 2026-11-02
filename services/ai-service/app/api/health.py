from fastapi import APIRouter, Request, Response
from sqlalchemy import text

router = APIRouter(tags=["health"])


@router.get("/health/live")
async def live() -> dict[str, str]:
    """The process is running. Does not touch the database."""
    return {"status": "ok"}


@router.get("/health/ready")
async def ready(request: Request, response: Response) -> dict[str, object]:
    """Ready when the database answers and the `ai` schema has been migrated."""
    try:
        async with request.app.state.engine.connect() as conn:
            version = (await conn.execute(text("SELECT version_num FROM ai.alembic_version"))).scalar_one_or_none()
    except Exception:
        response.status_code = 503
        return {"status": "unavailable"}
    if version is None:
        response.status_code = 503
        return {"status": "not_migrated"}
    return {
        "status": "ok",
        "schema_version": str(version),
        "profile": request.app.state.providers.profile,
        "chat_available": await request.app.state.providers.llm.available(),
    }
