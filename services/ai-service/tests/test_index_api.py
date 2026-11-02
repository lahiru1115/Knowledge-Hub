"""Indexing through the HTTP API against a real database, with a fake embedder (no model download)."""

import dataclasses
import hashlib
import uuid

import pytest
from sqlalchemy import create_engine, text

from tests.conftest import MIGRATOR_URL, make_token

pytestmark = pytest.mark.db
MODEL = "fake-embed-3"


class CountingEmbedder:
    model_id = MODEL
    dimensions = 3

    def __init__(self) -> None:
        self.batches: list[int] = []

    async def embed_documents(self, texts):
        self.batches.append(len(texts))
        return [[(hashlib.md5(t.encode()).digest()[i] + 1) / 256 for i in range(3)] for t in texts]  # noqa: S324

    async def embed_query(self, text):
        return (await self.embed_documents([text]))[0]


@pytest.fixture
def embedder(app, client):
    e = CountingEmbedder()
    app.state.providers = dataclasses.replace(app.state.providers, embedder=e)
    return e


@pytest.fixture(scope="module")
def db():
    e = create_engine(MIGRATOR_URL.replace("postgresql://", "postgresql+psycopg://", 1))
    yield e
    e.dispose()


def rows(db, user, resource, table="chunks"):
    with db.connect() as c:
        return (
            c.execute(
                text(
                    f"SELECT * FROM ai.{table} WHERE user_id = :u AND resource_id = :r ORDER BY {'chunk_index' if table == 'chunks' else 'user_id'}"
                ),
                {"u": user, "r": resource},
            )
            .mappings()
            .all()
        )


def headers(user):
    return {"Authorization": f"Bearer {make_token(user)}"}


LONG = " ".join(f"Sentence {i} about pooling and batching in the driver." for i in range(120))  # ~1,000 words


async def test_a_resource_is_chunked_embedded_and_stored(client, embedder, db):
    user, res, col = uuid.uuid4(), uuid.uuid4(), uuid.uuid4()
    r = await client.put(
        f"/index/{res}",
        json={"title": "Pooling", "description": "d", "notes": "n", "collection_id": str(col), "text": LONG},
        headers=headers(user),
    )
    assert r.status_code == 200
    body = r.json()
    assert body["status"] == "indexed" and body["chunks"] >= 3
    stored = rows(db, user, res)
    assert len(stored) == body["chunks"]
    assert all(c["collection_id"] == col and c["embedding_model"] == MODEL for c in stored)
    assert all(c["content"].startswith("Pooling\n\n") for c in stored)
    assert [c["chunk_index"] for c in stored] == list(range(len(stored)))
    assert sum(embedder.batches) == len(stored)
    assert rows(db, user, res, "documents")[0]["text"] == LONG
    with db.connect() as c:
        used = c.execute(
            text("SELECT operation, provider, tokens_in FROM ai.usage WHERE user_id = :u"), {"u": user}
        ).all()
    assert used and used[0][0] == "embed" and used[0][2] > 0


async def test_chunks_are_embedded_in_batches_of_32(client, embedder):
    big = " ".join(f"Sentence {i} " + "word " * 40 + "end." for i in range(900))  # ~40,000 words -> >100 chunks
    r = await client.put(f"/index/{uuid.uuid4()}", json={"title": "Big", "text": big}, headers=headers(uuid.uuid4()))
    assert r.json()["chunks"] > 64
    assert max(embedder.batches) == 32
    assert sum(embedder.batches) == r.json()["chunks"]


async def test_the_same_content_is_not_embedded_again_but_the_collection_follows(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    payload = {"title": "T", "text": LONG}
    await client.put(f"/index/{res}", json=payload, headers=headers(user))
    calls = len(embedder.batches)
    new_col = uuid.uuid4()
    r = await client.put(f"/index/{res}", json={**payload, "collection_id": str(new_col)}, headers=headers(user))
    assert r.json()["status"] == "unchanged"
    assert len(embedder.batches) == calls  # NFR-REL-03
    assert {c["collection_id"] for c in rows(db, user, res)} == {new_col}


async def test_an_edit_without_text_reindexes_from_the_stored_text(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "Old title", "text": LONG}, headers=headers(user))
    r = await client.put(f"/index/{res}", json={"title": "New title", "notes": "my notes"}, headers=headers(user))
    assert r.json()["status"] == "indexed"
    stored = rows(db, user, res)
    assert all(c["content"].startswith("New title") for c in stored)
    assert any("my notes" in c["content"] for c in stored)
    assert any("pooling" in c["content"] for c in stored)  # the page text is still there


async def test_changed_text_replaces_every_old_chunk(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "T", "text": LONG}, headers=headers(user))
    await client.put(f"/index/{res}", json={"title": "T", "text": "A short replacement page."}, headers=headers(user))
    stored = rows(db, user, res)
    assert len(stored) == 1 and "replacement" in stored[0]["content"]


async def test_chunks_of_another_model_are_left_alone(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    with db.begin() as c:
        c.execute(
            text(
                "INSERT INTO ai.chunks (id, user_id, resource_id, chunk_index, content, content_hash, embedding_model, embedding) "
                "VALUES (:i, :u, :r, 0, 'old model', 'h', 'other-model', CAST('[1,2]' AS public.vector))"
            ),
            {"i": uuid.uuid4(), "u": user, "r": res},
        )
    await client.put(
        f"/index/{res}", json={"title": "T", "text": "Some text for the new model to index."}, headers=headers(user)
    )
    assert {c["embedding_model"] for c in rows(db, user, res)} == {"other-model", MODEL}


async def test_nothing_to_index_removes_the_chunks(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "T", "text": LONG}, headers=headers(user))
    r = await client.put(f"/index/{res}", json={}, headers=headers(user))
    # No title any more, but the stored text remains, so it is still indexed.
    assert r.json()["status"] == "indexed"
    other = uuid.uuid4()
    r = await client.put(f"/index/{other}", json={}, headers=headers(user))
    assert r.json() == {"status": "empty", "chunks": 0}
    assert rows(db, user, other) == []


async def test_delete_removes_chunks_but_keeps_the_text_so_a_restore_can_reindex(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "T", "text": LONG}, headers=headers(user))
    assert (await client.delete(f"/index/{res}", headers=headers(user))).status_code == 204
    assert rows(db, user, res) == []
    assert len(rows(db, user, res, "documents")) == 1
    restored = await client.put(f"/index/{res}", json={"title": "T"}, headers=headers(user))
    assert restored.json()["status"] == "indexed" and rows(db, user, res)


async def test_forget_also_removes_the_text(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "T", "text": LONG}, headers=headers(user))
    assert (await client.delete(f"/index/{res}?forget=true", headers=headers(user))).status_code == 204
    assert rows(db, user, res) == [] and rows(db, user, res, "documents") == []


async def test_deleting_something_that_is_not_there_is_fine(client, embedder):
    assert (await client.delete(f"/index/{uuid.uuid4()}", headers=headers(uuid.uuid4()))).status_code == 204


async def test_one_user_cannot_touch_another_users_index(client, embedder, db):
    owner, intruder, res = uuid.uuid4(), uuid.uuid4(), uuid.uuid4()
    await client.put(f"/index/{res}", json={"title": "Mine", "text": LONG}, headers=headers(owner))
    before = [c["content"] for c in rows(db, owner, res)]

    await client.delete(f"/index/{res}?forget=true", headers=headers(intruder))
    assert [c["content"] for c in rows(db, owner, res)] == before
    assert len(rows(db, owner, res, "documents")) == 1

    # Writing under someone else's resource ID must not change or reveal their data.
    try:
        await client.put(f"/index/{res}", json={"title": "Theirs", "text": "Intruder text."}, headers=headers(intruder))
    except Exception:  # noqa: S110
        pass  # a clash on the shared resource ID is an error, which is also safe
    assert [c["content"] for c in rows(db, owner, res)] == before
    assert rows(db, owner, res, "documents")[0]["text"] == LONG


async def test_index_calls_need_a_user_token(client, embedder):
    res = uuid.uuid4()
    assert (await client.put(f"/index/{res}", json={})).status_code == 401
    system = {"Authorization": f"Bearer {make_token('system', scope='users:purge')}"}
    assert (await client.put(f"/index/{res}", json={}, headers=system)).status_code == 403
    assert (await client.delete(f"/index/{res}", headers=system)).status_code == 403


async def test_bad_input_is_a_validation_problem(client, embedder):
    r = await client.put(f"/index/{uuid.uuid4()}", json={"title": "x" * 301}, headers=headers(uuid.uuid4()))
    assert r.status_code == 400 and r.json()["code"] == "validation_failed"
    assert (await client.put("/index/not-a-uuid", json={}, headers=headers(uuid.uuid4()))).status_code == 400


async def test_purging_a_user_removes_everything_of_theirs_and_nothing_else(client, embedder, db):
    gone, kept, r1, r2 = uuid.uuid4(), uuid.uuid4(), uuid.uuid4(), uuid.uuid4()
    for u, r in ((gone, r1), (kept, r2)):
        await client.put(f"/index/{r}", json={"title": "T", "text": LONG}, headers=headers(u))
    session = uuid.uuid4()
    with db.begin() as c:
        c.execute(text("INSERT INTO ai.chat_sessions (id, user_id) VALUES (:s, :u)"), {"s": session, "u": gone})
        c.execute(
            text("INSERT INTO ai.chat_messages (id, session_id, role, content) VALUES (:i, :s, 'user', 'hi')"),
            {"i": uuid.uuid4(), "s": session},
        )
        c.execute(
            text(
                "INSERT INTO ai.summaries (user_id, content_hash, model, summary, source) VALUES (:u, 'h', 'm', 's', 'ai')"
            ),
            {"u": gone},
        )

    token = {"Authorization": f"Bearer {make_token('system', scope='users:purge')}"}
    assert (await client.delete(f"/users/{gone}", headers=token)).status_code == 204

    with db.connect() as c:
        for table in ("chunks", "documents", "summaries", "chat_sessions", "usage"):
            n = c.execute(text(f"SELECT count(*) FROM ai.{table} WHERE user_id = :u"), {"u": gone}).scalar_one()
            assert n == 0, table
        assert (
            c.execute(text("SELECT count(*) FROM ai.chat_messages WHERE session_id = :s"), {"s": session}).scalar_one()
            == 0
        )
    assert rows(db, kept, r2)  # untouched


async def test_purge_needs_the_system_scope(client, embedder):
    victim = uuid.uuid4()
    assert (await client.delete(f"/users/{victim}", headers=headers(victim))).status_code == 403
    wrong = {"Authorization": f"Bearer {make_token('system', scope='usage:read_all')}"}
    assert (await client.delete(f"/users/{victim}", headers=wrong)).status_code == 403
    assert (await client.delete(f"/users/{victim}")).status_code == 401
