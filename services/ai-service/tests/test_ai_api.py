"""Summaries, tag suggestions, usage and quotas through the API, against a real database."""

import asyncio
import dataclasses
import uuid
from datetime import UTC, datetime

import pytest
from sqlalchemy import create_engine, text

from app.quota import UsageMeter, day_bounds
from tests.conftest import MIGRATOR_URL, make_token

pytestmark = pytest.mark.db

DESCRIPTION = "A practical guide to connection pooling in Npgsql. It explains batching, prepared statements and more."
PAGE = " ".join(f"Sentence {i} about connection pooling and batching in the driver library." for i in range(30))


def headers(user):
    return {"Authorization": f"Bearer {make_token(user)}"}


@pytest.fixture(scope="module")
def db():
    e = create_engine(MIGRATOR_URL.replace("postgresql://", "postgresql+psycopg://", 1))
    yield e
    e.dispose()


def usage_rows(db, user, op=None):
    with db.connect() as c:
        sql = "SELECT operation FROM ai.usage WHERE user_id = :u" + (" AND operation = :o" if op else "")
        return c.execute(text(sql), {"u": user, "o": op}).all()


@pytest.fixture
def providers(app, client):
    """The real extractive summariser and YAKE tagger, but no embedding model (nothing to download)."""

    class NoModel:
        model_id = "none"
        dimensions = 3

        async def embed_documents(self, texts):
            raise RuntimeError("no model in tests")

        async def embed_query(self, text):
            raise RuntimeError("no model in tests")

    from app.providers.free.tagger import YakeTagger

    app.state.providers = dataclasses.replace(app.state.providers, embedder=NoModel(), tagger=YakeTagger(NoModel()))
    return app.state.providers


# ---------- summaries ----------


async def test_a_description_becomes_an_extracted_summary(client, providers):
    r = await client.post("/summarize", json={"title": "T", "description": DESCRIPTION}, headers=headers(uuid.uuid4()))
    assert r.status_code == 200
    assert r.json() == {"summary": DESCRIPTION, "source": "extracted", "model": "extractive-v1"}


async def test_the_page_text_is_used_when_there_is_no_description(client, providers):
    r = await client.post("/summarize", json={"title": "T", "text": PAGE}, headers=headers(uuid.uuid4()))
    assert r.status_code == 200
    body = r.json()
    assert body["summary"].startswith("Sentence 0") and len(body["summary"]) <= 600


async def test_the_stored_text_of_an_indexed_resource_is_used(client, providers, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    with db.begin() as c:
        c.execute(
            text("INSERT INTO ai.documents (user_id, resource_id, text) VALUES (:u, :r, :t)"),
            {"u": user, "r": res, "t": PAGE},
        )
    r = await client.post("/summarize", json={"resource_id": str(res), "title": "T"}, headers=headers(user))
    assert r.status_code == 200 and r.json()["summary"].startswith("Sentence 0")
    # Another user's resource ID gives them nothing to summarise.
    other = await client.post("/summarize", json={"resource_id": str(res), "title": "T"}, headers=headers(uuid.uuid4()))
    assert other.status_code == 422


@pytest.mark.parametrize("body", [{}, {"title": "Only a title"}, {"text": "tiny"}, {"description": "too short"}])
async def test_too_little_to_summarise_is_422_and_does_not_count(client, providers, db, body):
    user = uuid.uuid4()
    r = await client.post("/summarize", json=body, headers=headers(user))
    assert r.status_code == 422
    assert r.json()["code"] == "not_enough_content"
    assert usage_rows(db, user) == []


async def test_a_repeat_is_served_from_the_stored_summary_but_still_counts(client, providers, db):
    user = uuid.uuid4()
    payload = {"title": "T", "description": DESCRIPTION}
    await client.post("/summarize", json=payload, headers=headers(user))
    with db.connect() as c:
        stored = c.execute(text("SELECT count(*) FROM ai.summaries WHERE user_id = :u"), {"u": user}).scalar_one()
    again = await client.post("/summarize", json=payload, headers=headers(user))
    assert again.json()["summary"] == DESCRIPTION
    assert stored == 1
    assert len(usage_rows(db, user, "summarize")) == 2


async def test_summaries_need_a_user_token(client, providers):
    assert (await client.post("/summarize", json={})).status_code == 401
    system = {"Authorization": f"Bearer {make_token('system', scope='users:purge')}"}
    assert (await client.post("/summarize", json={}, headers=system)).status_code == 403


# ---------- tag suggestions ----------


async def test_suggestions_flag_which_tags_already_exist(client, providers):
    r = await client.post(
        "/suggest-tags",
        json={
            "title": "Npgsql connection pooling",
            "description": DESCRIPTION,
            "text": PAGE,
            "existing_tags": ["pooling", "cooking"],
        },
        headers=headers(uuid.uuid4()),
    )
    assert r.status_code == 200
    items = r.json()["items"]
    assert 1 <= len(items) <= 5
    by_name = {i["name"]: i["existing"] for i in items}
    assert by_name.get("pooling") is True
    assert "cooking" not in by_name
    assert all(not v for k, v in by_name.items() if k != "pooling")


async def test_suggestions_work_without_the_embedding_model(client, providers):
    r = await client.post(
        "/suggest-tags", json={"title": "Rust ownership and borrowing"}, headers=headers(uuid.uuid4())
    )
    assert r.status_code == 200 and r.json()["items"]


async def test_suggestions_for_an_empty_page_are_empty_and_too_many_existing_tags_are_refused(client, providers):
    u = headers(uuid.uuid4())
    assert (await client.post("/suggest-tags", json={}, headers=u)).json() == {"items": []}
    r = await client.post("/suggest-tags", json={"existing_tags": [f"t{i}" for i in range(501)]}, headers=u)
    assert r.status_code == 400


# ---------- usage and quotas ----------


async def test_usage_lists_every_quota_operation_with_its_limit(client, providers):
    user = uuid.uuid4()
    await client.post("/summarize", json={"description": DESCRIPTION}, headers=headers(user))
    r = await client.get("/usage", headers=headers(user))
    assert r.status_code == 200
    body = r.json()
    items = {i["operation"]: i for i in body["items"]}
    assert items["summarize"] == {"operation": "summarize", "used": 1, "limit": 300}
    assert (items["suggest_tags"]["used"], items["suggest_tags"]["limit"]) == (0, 300)
    assert (items["search"]["limit"], items["chat"]["limit"]) == (500, 100)
    assert "embed" not in items  # background indexing is metered but not a quota operation
    assert datetime.fromisoformat(body["reset_at"]) == day_bounds()[1]


async def test_indexing_is_metered_but_does_not_use_the_quota(client, providers, db):
    meter = UsageMeter(client._transport.app.state.engine, {"summarize": 0}, "free")
    user = uuid.uuid4()
    r = await meter.reserve(user, "embed", "m")  # embed has no limit, even with other limits at zero
    await meter.finish(r, 10)
    assert len(usage_rows(db, user, "embed")) == 1


async def test_over_the_limit_is_429_with_the_reset_time_and_retry_after(app, client, providers):
    app.state.settings = app.state.settings.model_copy(update={"quota_summarize": 2})
    user = uuid.uuid4()
    for _ in range(2):
        assert (
            await client.post("/summarize", json={"description": DESCRIPTION}, headers=headers(user))
        ).status_code == 200
    r = await client.post("/summarize", json={"description": DESCRIPTION}, headers=headers(user))
    assert r.status_code == 429
    body = r.json()
    assert body["code"] == "ai_quota_exceeded"
    assert datetime.fromisoformat(body["reset_at"]) == day_bounds()[1]
    assert int(r.headers["retry-after"]) == body["retry_after"] >= 1
    # The limit is per user and per operation.
    assert (
        await client.post("/summarize", json={"description": DESCRIPTION}, headers=headers(uuid.uuid4()))
    ).status_code == 200
    assert (
        await client.post("/suggest-tags", json={"title": "Plenty left here"}, headers=headers(user))
    ).status_code == 200


async def test_a_limit_of_zero_turns_the_operation_off(app, client, providers):
    app.state.settings = app.state.settings.model_copy(update={"quota_suggest_tags": 0})
    r = await client.post("/suggest-tags", json={"title": "Anything at all"}, headers=headers(uuid.uuid4()))
    assert r.status_code == 429


async def test_yesterdays_usage_does_not_count(client, providers, db):
    user = uuid.uuid4()
    with db.begin() as c:
        for _ in range(5):
            c.execute(
                text(
                    "INSERT INTO ai.usage (id, user_id, operation, provider, model, created_at) VALUES (:i, :u, 'summarize', 'free', 'm', :t)"
                ),
                {
                    "i": uuid.uuid4(),
                    "u": user,
                    "t": day_bounds()[0].replace(day=1)
                    if day_bounds()[0].day > 1
                    else datetime(2020, 1, 1, tzinfo=UTC),
                },
            )
    r = await client.get("/usage", headers=headers(user))
    assert {i["operation"]: i["used"] for i in r.json()["items"]}["summarize"] == 0


async def test_parallel_requests_cannot_slip_past_the_limit(app, client, providers, db):
    app.state.settings = app.state.settings.model_copy(update={"quota_summarize": 3})
    user = uuid.uuid4()
    results = await asyncio.gather(
        *(client.post("/summarize", json={"description": DESCRIPTION}, headers=headers(user)) for _ in range(10))
    )
    codes = sorted(r.status_code for r in results)
    assert codes.count(200) == 3 and codes.count(429) == 7
    assert len(usage_rows(db, user, "summarize")) == 3


async def test_profile_defaults_and_overrides():
    from app.config import Settings

    base = {"AI_DATABASE_URL": "postgresql://x", "SERVICE_JWT_SECRET": "s" * 32}
    assert Settings(**base).quotas() == {"summarize": 300, "suggest_tags": 300, "search": 500, "chat": 100}  # type: ignore[arg-type]
    claude = Settings(**base, AI_PROFILE="claude").quotas()  # type: ignore[arg-type]
    assert claude == {"summarize": 100, "suggest_tags": 200, "search": None, "chat": 30}
    assert Settings(**base, QUOTA_CHAT=7).quotas()["chat"] == 7  # type: ignore[arg-type]


async def test_the_ready_check_reports_the_profile(client, providers):
    r = await client.get("/health/ready")
    assert r.json()["profile"] == "free" and r.json()["chat_available"] is False
