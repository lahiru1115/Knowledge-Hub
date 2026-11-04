"""Tests against a real PostgreSQL with pgvector and the ai schema migrated (run `alembic upgrade head` first)."""

import random
import uuid

import pytest
from sqlalchemy import create_engine, text
from sqlalchemy.exc import DBAPIError

from tests.conftest import APP_URL, MIGRATOR_URL

pytestmark = pytest.mark.db


def sync_url(url: str) -> str:
    return url.replace("postgresql://", "postgresql+psycopg://", 1)


@pytest.fixture(scope="module")
def migrator():
    e = create_engine(sync_url(MIGRATOR_URL))
    yield e
    e.dispose()


@pytest.fixture(scope="module")
def app_engine():
    e = create_engine(sync_url(APP_URL))
    yield e
    e.dispose()


async def test_ready_reports_the_schema_version(client):
    r = await client.get("/health/ready")
    assert r.status_code == 200
    assert r.json()["status"] == "ok" and r.json()["schema_version"] == "0003"


async def test_ready_is_unavailable_when_the_database_cannot_be_reached(app, bearer):
    from httpx import ASGITransport, AsyncClient

    from app.config import Settings
    from app.main import create_app

    broken = create_app(Settings(AI_DATABASE_URL="postgresql://ai_app:x@127.0.0.1:1/none", SERVICE_JWT_SECRET="s" * 32))  # type: ignore[call-arg]
    async with broken.router.lifespan_context(broken):
        async with AsyncClient(transport=ASGITransport(app=broken), base_url="http://ai") as c:
            r = await c.get("/health/ready")
    assert r.status_code == 503
    assert r.json() == {"status": "unavailable"}


def test_the_tables_and_indexes_of_the_design_exist(migrator):
    with migrator.connect() as c:
        tables = {r[0] for r in c.execute(text("SELECT tablename FROM pg_tables WHERE schemaname = 'ai'"))}
        indexes = {r[0] for r in c.execute(text("SELECT indexname FROM pg_indexes WHERE schemaname = 'ai'"))}
    assert {"chunks", "summaries", "chat_sessions", "chat_messages", "usage", "alembic_version"} <= tables
    assert {
        "chunks_embedding_bge_small_hnsw",
        "chunks_embedding_voyage_4_lite_hnsw",
        "ix_chunks_tsv",
        "ix_chunks_user_resource",
        "ix_usage_user_operation_created",
    } <= indexes


def test_vectors_of_different_sizes_share_the_column_and_are_found_by_model(migrator):
    user, resource = uuid.uuid4(), uuid.uuid4()
    rnd = random.Random(1)

    def vec(n: int) -> str:
        return "[" + ",".join(f"{rnd.random():.4f}" for _ in range(n)) + "]"

    with migrator.begin() as c:
        for model, dim, i in (("BAAI/bge-small-en-v1.5", 384, 0), ("voyage-4-lite", 1024, 1)):
            c.execute(
                text(
                    "INSERT INTO ai.chunks (id, user_id, resource_id, chunk_index, content, content_hash, embedding_model, embedding) "
                    "VALUES (:id, :u, :r, :i, :c, 'h', :m, CAST(:e AS public.vector))"
                ),
                {
                    "id": uuid.uuid4(),
                    "u": user,
                    "r": resource,
                    "i": i,
                    "c": f"hello world {i}",
                    "m": model,
                    "e": vec(dim),
                },
            )
        q = vec(384)
        rows = c.execute(
            text(
                "SELECT embedding_model FROM ai.chunks WHERE user_id = :u AND embedding_model = 'BAAI/bge-small-en-v1.5' "
                "ORDER BY embedding::public.vector(384) <=> CAST(:q AS public.vector(384)) LIMIT 5"
            ),
            {"u": user, "q": q},
        ).all()
        assert [r[0] for r in rows] == ["BAAI/bge-small-en-v1.5"]
        found = c.execute(
            text("SELECT count(*) FROM ai.chunks WHERE tsv @@ plainto_tsquery('english', 'hello') AND user_id = :u"),
            {"u": user},
        ).scalar_one()
        assert found == 2
        c.execute(text("DELETE FROM ai.chunks WHERE user_id = :u"), {"u": user})


def test_the_app_role_can_use_the_ai_schema_but_not_change_it_or_see_core(app_engine):
    with app_engine.begin() as c:
        c.execute(text("SELECT count(*) FROM ai.usage"))
    for sql in ("CREATE TABLE ai._nope (id int)", "DROP TABLE ai.usage", "SELECT * FROM core.users"):
        with pytest.raises(DBAPIError), app_engine.begin() as c:
            c.execute(text(sql))


def test_the_usage_operation_names_are_checked(migrator):
    with pytest.raises(DBAPIError), migrator.begin() as c:
        c.execute(
            text("INSERT INTO ai.usage (id, user_id, operation, provider, model) VALUES (:i, :u, 'hack', 'p', 'm')"),
            {"i": uuid.uuid4(), "u": uuid.uuid4()},
        )


def test_deleting_a_session_removes_its_messages(migrator):
    sid, uid = uuid.uuid4(), uuid.uuid4()
    with migrator.begin() as c:
        c.execute(text("INSERT INTO ai.chat_sessions (id, user_id) VALUES (:s, :u)"), {"s": sid, "u": uid})
        c.execute(
            text("INSERT INTO ai.chat_messages (id, session_id, role, content) VALUES (:i, :s, 'user', 'hi')"),
            {"i": uuid.uuid4(), "s": sid},
        )
        c.execute(text("DELETE FROM ai.chat_sessions WHERE id = :s"), {"s": sid})
        assert (
            c.execute(text("SELECT count(*) FROM ai.chat_messages WHERE session_id = :s"), {"s": sid}).scalar_one() == 0
        )
