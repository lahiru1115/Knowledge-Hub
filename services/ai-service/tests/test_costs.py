"""Cost metering, the daily and monthly caps, and re-embedding after the embedding model changes."""

import asyncio
import dataclasses
import logging
import uuid
from datetime import UTC, datetime

import pytest
from sqlalchemy import create_engine, text

from app.pricing import CostCalculator, Price
from app.providers import Providers
from app.providers.base import Usage
from app.providers.free.summarizer import ExtractiveSummarizer
from app.providers.free.tagger import YakeTagger
from app.quota import CostGuard, estimate_tokens
from app.reindex import ReindexService, reindex_forever
from tests.conftest import MIGRATOR_URL, make_token
from tests.fakes import ConceptEmbedder, PaidSummarizer, ScriptedLlm

pytestmark = pytest.mark.db
DESCRIPTION = "A practical guide to connection pooling in Npgsql. It explains batching, prepared statements and more."
PRICES = CostCalculator({"paid-small": Price(3.0, 15.0), "paid-llm": Price(3.0, 15.0), "concept-model": Price(0.1)})


@pytest.fixture(scope="module")
def db():
    e = create_engine(MIGRATOR_URL.replace("postgresql://", "postgresql+psycopg://", 1))
    yield e
    e.dispose()


def auth(user):
    return {"Authorization": f"Bearer {make_token(user)}"}


def spend(db, user, op="summarize"):
    with db.connect() as c:
        return c.execute(
            text(
                "SELECT provider, model, tokens_in, tokens_out, cost_usd FROM ai.usage WHERE user_id = :u AND operation = :o"
            ),
            {"u": user, "o": op},
        ).all()


@pytest.fixture
def paid(app, client):
    """A Claude-style deployment: paid providers that report tokens, with a free fallback."""
    summarizer, llm, embedder = PaidSummarizer(), ScriptedLlm(), ConceptEmbedder()
    llm.model_id = "paid-llm"
    free_llm = ScriptedLlm()
    free_llm.pieces = ["From the free model."]
    fallback = Providers("free", ExtractiveSummarizer(), embedder, YakeTagger(None), free_llm)
    app.state.providers = Providers("claude", summarizer, embedder, YakeTagger(None), llm, fallback=fallback)
    app.state.costs = PRICES
    app.state.cost_guard = CostGuard(app.state.engine, monthly_cap=10.0, cache_seconds=0)
    return summarizer, llm, free_llm, app


# ---------- metering ----------


def test_token_estimates_are_four_thirds_of_the_words():
    assert [estimate_tokens(n) for n in (0, 3, 30, 300)] == [0, 4, 40, 400]


async def test_a_paid_call_records_its_provider_model_tokens_and_cost(paid, client, db):
    user = uuid.uuid4()
    r = await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))
    assert r.status_code == 200 and r.json() == {"summary": "A paid summary.", "source": "ai", "model": "paid-small"}
    [(provider, model, tin, tout, cost)] = spend(db, user)
    assert (provider, model, tin, tout) == ("claude", "paid-small", 1000, 200)
    assert float(cost) == pytest.approx((1000 * 3 + 200 * 15) / 1_000_000)  # 0.006


async def test_free_calls_cost_nothing_even_with_a_price_table(app, client, db):
    app.state.costs = PRICES
    user = uuid.uuid4()
    await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))
    [(provider, model, _tin, _tout, cost)] = spend(db, user)
    assert (provider, model, float(cost)) == ("free", "extractive-v1", 0.0)


async def test_indexing_and_search_record_an_estimated_cost_for_a_priced_embedding_model(paid, client, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "T", "text": "word " * 300}, headers=auth(user))
    [(_p, model, tokens, _o, cost)] = spend(db, user, "embed")
    assert model == "concept-model" and tokens >= 400
    assert float(cost) == pytest.approx(tokens * 0.1 / 1_000_000, abs=1e-8)
    await client.post("/search", json={"query": "word"}, headers=auth(user))
    assert float(spend(db, user, "search")[0][4]) > 0


# ---------- the daily cap, per user ----------


async def test_a_user_over_the_daily_cost_cap_is_refused_with_the_reset_time(paid, client, db, app):
    app.state.settings = app.state.settings.model_copy(update={"daily_cost_cap_usd": 0.01})
    summarizer, *_ = paid
    summarizer.usage = Usage(2000, 400)  # costs 0.012, above the 0.01 cap in one call
    user = uuid.uuid4()
    assert (await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))).status_code == 200

    r = await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))

    assert r.status_code == 429 and r.json()["code"] == "ai_quota_exceeded" and "reset_at" in r.json()
    assert summarizer.calls == 1  # the second call was never paid for
    # The cap is per user, and covers every operation.
    other = await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(uuid.uuid4()))
    assert other.status_code == 200
    assert (await client.post("/suggest-tags", json={"title": "Some page"}, headers=auth(user))).status_code == 429


async def test_the_daily_cap_does_not_apply_to_free_providers(app, client):
    app.state.settings = app.state.settings.model_copy(update={"daily_cost_cap_usd": 0.0})
    user = uuid.uuid4()
    for _ in range(3):
        assert (
            await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))
        ).status_code == 200


# ---------- the monthly cap, whole deployment ----------


@pytest.fixture(autouse=True)
def forget_big_spends(db):
    """The cap tests seed spend into this month; none of it may outlive the test."""
    yield
    with db.begin() as c:
        c.execute(text("DELETE FROM ai.usage WHERE model = 'seeded-spend'"))


def add_spend(db, amount, when=None, provider="claude"):
    with db.begin() as c:
        c.execute(
            text(
                "INSERT INTO ai.usage (id, user_id, operation, provider, model, cost_usd, created_at) VALUES (:i, :u, 'chat', :p, 'seeded-spend', :c, :t)"
            ),
            {"i": uuid.uuid4(), "u": uuid.uuid4(), "p": provider, "c": amount, "t": when or datetime.now(UTC)},
        )


async def test_the_guard_counts_this_month_only(client, app, db):
    guard = CostGuard(app.state.engine, monthly_cap=1e9, cache_seconds=0)
    before = await guard.monthly_spend()
    add_spend(db, 1.25)
    add_spend(db, 99, when=datetime(2020, 1, 15, tzinfo=UTC))  # an old month
    assert await guard.monthly_spend() == pytest.approx(before + 1.25)


async def test_once_the_monthly_cap_is_reached_summaries_tags_and_chat_use_the_free_providers(paid, client, db, caplog):
    summarizer, llm, free_llm, _ = paid
    add_spend(db, 10_000)  # far over the cap, whatever else is in the table
    user = uuid.uuid4()

    with caplog.at_level(logging.WARNING):
        s = await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(user))
    assert s.json()["source"] == "extracted" and summarizer.calls == 0
    assert spend(db, user)[0][0] == "free"
    assert "monthly AI cost cap" in caplog.text

    chat = await client.post("/chat/sessions", json={}, headers=auth(user))
    res = uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "Cars", "text": "A sedan is a car."}, headers=auth(user))
    ask = await client.post(
        f"/chat/sessions/{chat.json()['id']}/messages", json={"content": "sedan"}, headers=auth(user)
    )
    assert "From the free model." in ask.text and llm.calls == [] and len(free_llm.calls) == 1
    assert spend(db, user, "chat")[0][0] == "free"


async def test_embeddings_never_switch_when_the_cap_is_reached(paid, client, db):
    add_spend(db, 10_000)
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(
        f"/index/{res}", json={"title": "Cars", "text": "A sedan is a car and a vehicle."}, headers=auth(user)
    )
    hits = await client.post("/search", json={"query": "vehicle"}, headers=auth(user))
    assert [i["resource_id"] for i in hits.json()["items"]] == [str(res)]  # the same index, so it still finds it


async def test_below_the_cap_the_paid_providers_are_used(paid, client):
    summarizer, *_ = paid
    r = await client.post("/summarize", json={"description": DESCRIPTION}, headers=auth(uuid.uuid4()))
    assert r.json()["source"] == "ai" and summarizer.calls == 1


# ---------- re-embedding after the model changes ----------


def seed_old(db, user, resource, contents, model="old-model", collection=None):
    with db.begin() as c:
        for i, content in enumerate(contents):
            c.execute(
                text(
                    "INSERT INTO ai.chunks (id, user_id, resource_id, collection_id, chunk_index, content, content_hash, "
                    "embedding_model, embedding) VALUES (:id, :u, :r, :c, :i, :t, 'hash-1', :m, CAST('[1,0]' AS public.vector))"
                ),
                {"id": uuid.uuid4(), "u": user, "r": resource, "c": collection, "i": i, "t": content, "m": model},
            )


def chunks(db, user, resource):
    with db.connect() as c:
        return c.execute(
            text(
                "SELECT chunk_index, content, collection_id, content_hash, embedding_model FROM ai.chunks "
                "WHERE user_id = :u AND resource_id = :r ORDER BY embedding_model, chunk_index"
            ),
            {"u": user, "r": resource},
        ).all()


async def test_resources_with_only_old_chunks_are_re_embedded_and_the_old_ones_removed(client, app, db):
    user, res, col = uuid.uuid4(), uuid.uuid4(), uuid.uuid4()
    seed_old(db, user, res, ["Cars\n\nA sedan is a car.", "Cars\n\nMore about the automobile."], collection=col)
    service = ReindexService(app.state.engine, ConceptEmbedder(), "free", PRICES)
    assert await service.pending() >= 1

    assert await service.run_once(batch=1000) >= 1

    rows = chunks(db, user, res)
    assert [(r.chunk_index, r.content, r.collection_id, r.content_hash, r.embedding_model) for r in rows] == [
        (0, "Cars\n\nA sedan is a car.", col, "hash-1", "concept-model"),
        (1, "Cars\n\nMore about the automobile.", col, "hash-1", "concept-model"),
    ]
    with db.connect() as c:
        used = c.execute(
            text("SELECT tokens_in, cost_usd FROM ai.usage WHERE user_id = :u AND operation = 'embed'"), {"u": user}
        ).one()
    assert used[0] > 0 and float(used[1]) > 0


async def test_after_re_embedding_search_finds_the_resource_with_the_new_model(client, app, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    seed_old(db, user, res, ["Garage\n\nWashing the automobile and the sedan."])
    app.state.providers = dataclasses.replace(app.state.providers, embedder=ConceptEmbedder())
    before = await client.post("/search", json={"query": "vehicle"}, headers=auth(user))
    assert before.json()["items"] == []  # the old model's chunks are not searched

    await ReindexService(app.state.engine, ConceptEmbedder(), "free").run_once(batch=1000)

    after = await client.post("/search", json={"query": "vehicle"}, headers=auth(user))
    assert [i["resource_id"] for i in after.json()["items"]] == [str(res)]


async def test_a_second_run_finds_nothing_to_do_and_current_chunks_are_left_alone(client, app, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    seed_old(db, user, res, ["T\n\ntext"], model="concept-model")  # already on the current model
    service = ReindexService(app.state.engine, ConceptEmbedder(), "free")
    await service.run_once(batch=1000)
    assert await service.pending() == 0
    assert [r.embedding_model for r in chunks(db, user, res)] == ["concept-model"]
    assert await service.run_once(batch=1000) == 0


async def test_when_the_new_model_is_down_nothing_is_lost_and_it_resumes_later(client, app, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    seed_old(db, user, res, ["T\n\ntext one", "T\n\ntext two"])
    embedder = ConceptEmbedder()
    embedder.fail = True
    service = ReindexService(app.state.engine, embedder, "free")

    assert await service.run_once(batch=1000) == 0
    assert [r.embedding_model for r in chunks(db, user, res)] == ["old-model", "old-model"]  # untouched

    embedder.fail = False
    assert await service.run_once(batch=1000) >= 1
    assert {r.embedding_model for r in chunks(db, user, res)} == {"concept-model"}


async def test_the_background_loop_keeps_going_after_a_failure_and_stops_when_cancelled(client, app, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    seed_old(db, user, res, ["T\n\nloop text"])
    embedder = ConceptEmbedder()
    embedder.fail = True
    app.state.providers = dataclasses.replace(app.state.providers, embedder=embedder)
    task = asyncio.create_task(reindex_forever(app, 0.05))
    await asyncio.sleep(0.2)
    assert {r.embedding_model for r in chunks(db, user, res)} == {"old-model"}
    embedder.fail = False
    for _ in range(40):
        await asyncio.sleep(0.05)
        if {r.embedding_model for r in chunks(db, user, res)} == {"concept-model"}:
            break
    task.cancel()
    await asyncio.gather(task, return_exceptions=True)
    assert {r.embedding_model for r in chunks(db, user, res)} == {"concept-model"}
    assert task.cancelled()


# ---------- the daily cap under concurrency ----------


def _seed_user_spend(db, user, amount):
    with db.begin() as c:
        c.execute(
            text(
                "INSERT INTO ai.usage (id, user_id, operation, provider, model, cost_usd, created_at) "
                "VALUES (:i, :u, 'chat', 'claude', 'seeded-spend', :c, now())"
            ),
            {"i": uuid.uuid4(), "u": user, "c": amount},
        )


async def test_paid_calls_started_together_cannot_all_slip_under_the_daily_cap(client, app, db):
    """Just under the cap, a chat and a summary started at the same moment: only one may go ahead."""
    from app.errors import ApiError
    from app.quota import UsageMeter

    user = uuid.uuid4()
    _seed_user_spend(db, user, 0.0099)
    meter = UsageMeter(app.state.engine, {}, "claude", PRICES, daily_cost_cap=0.01)

    results = await asyncio.gather(
        meter.reserve(user, "chat", "paid-llm"),
        meter.reserve(user, "summarize", "paid-small"),
        meter.reserve(user, "suggest_tags", "paid-small"),
        return_exceptions=True,
    )

    refused = [r for r in results if isinstance(r, ApiError)]
    assert len(refused) == 2, results
    assert refused[0].code == "ai_quota_exceeded"


async def test_a_reservation_holds_an_estimate_until_the_real_cost_replaces_it(client, app, db):
    from app.quota import UsageMeter

    user = uuid.uuid4()
    meter = UsageMeter(app.state.engine, {}, "claude", PRICES, daily_cost_cap=10.0)
    r = await meter.reserve(user, "chat", "paid-llm")
    held = spend(db, user, "chat")[0].cost_usd
    assert held > 0  # counted against the cap while the call runs
    await meter.finish(r, 100, 10)
    assert float(spend(db, user, "chat")[0].cost_usd) == pytest.approx(PRICES.cost("paid-llm", 100, 10))
    r2 = await meter.reserve(user, "summarize", "paid-small")
    await meter.cancel(r2)
    assert spend(db, user, "summarize") == []


async def test_free_models_reserve_no_estimate(client, app, db):
    from app.quota import UsageMeter

    user = uuid.uuid4()
    meter = UsageMeter(app.state.engine, {}, "free", PRICES, daily_cost_cap=None)
    await meter.reserve(user, "chat", "llama3.2:3b")
    assert float(spend(db, user, "chat")[0].cost_usd) == 0


async def test_one_resource_that_cannot_be_embedded_does_not_hold_up_the_others(client, app, db):
    class Picky(ConceptEmbedder):
        async def embed_documents(self, texts):
            if any("poison" in t for t in texts):
                raise ValueError("this text upsets the model")
            return await super().embed_documents(texts)

    user = uuid.uuid4()
    bad, good = uuid.UUID(int=1), uuid.uuid4()  # the bad one sorts first, as it would every run
    seed_old(db, user, bad, ["T\n\npoison"])
    seed_old(db, user, good, ["T\n\ntext one"])
    service = ReindexService(app.state.engine, Picky(), "free")

    await service.run_once(batch=1000)

    assert {r.embedding_model for r in chunks(db, user, good)} == {"concept-model"}
    assert {r.embedding_model for r in chunks(db, user, bad)} == {"old-model"}  # left as it was, searchable as before
