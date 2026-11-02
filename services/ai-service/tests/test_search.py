"""Hybrid search and related resources against a real database, with a small fake embedder that knows synonyms."""

import dataclasses
import random
import re
import uuid

import pytest
from sqlalchemy import create_engine, text

from app.indexing import vector_literal
from app.retrieval.hybrid import fuse, or_query, snippet, vector_leg_sql
from tests.conftest import MIGRATOR_URL, make_token

pytestmark = pytest.mark.db
MODEL = "concept-model"
GROUPS = [{"car", "automobile", "vehicle", "sedan"}, {"dog", "puppy", "canine"}, {"database", "postgres", "sql"}]


class ConceptEmbedder:
    """Words in the same group point the same way, so a synonym is close in vector space even with no shared word."""

    model_id = MODEL
    dimensions = 4

    def __init__(self) -> None:
        self.fail = False

    def _vec(self, t: str) -> list[float]:
        words = re.findall(r"\w+", t.lower())
        v = [float(sum(w in g for w in words)) for g in GROUPS]
        return [v[0], v[1], v[2], 0.5]  # a constant part, so text about nothing is not the same direction as anything

    async def embed_documents(self, texts):
        if self.fail:
            raise RuntimeError("model unavailable")
        return [self._vec(t) for t in texts]

    async def embed_query(self, text):
        return (await self.embed_documents([text]))[0]


@pytest.fixture
def embedder(app, client):
    e = ConceptEmbedder()
    app.state.providers = dataclasses.replace(app.state.providers, embedder=e)
    return e


@pytest.fixture(scope="module")
def db():
    e = create_engine(MIGRATOR_URL.replace("postgresql://", "postgresql+psycopg://", 1))
    yield e
    e.dispose()


def auth(user):
    return {"Authorization": f"Bearer {make_token(user)}"}


async def index(client, user, title, body, collection=None):
    res = uuid.uuid4()
    r = await client.put(
        f"/index/{res}",
        json={"title": title, "text": body, "collection_id": str(collection) if collection else None},
        headers=auth(user),
    )
    assert r.status_code == 200, r.text
    return res


async def find(client, user, q, **extra):
    r = await client.post("/search", json={"query": q, **extra}, headers=auth(user))
    assert r.status_code == 200, r.text
    return r.json()["items"]


# ---------- the pieces ----------


def test_the_keyword_query_ors_the_words_and_ignores_punctuation():
    assert or_query("How do I pool connections?") == "how | do | pool | connections"
    assert or_query("a ? !") is None
    assert or_query("what's up; DROP TABLE x") == "what | up | drop | table"  # nothing that could break tsquery syntax


def test_fusion_adds_reciprocal_ranks():
    a, b, c = (uuid.uuid4() for _ in range(3))
    scores = fuse([a, b], [b, c])
    assert scores[a] == pytest.approx(1 / 61)
    assert scores[b] == pytest.approx(1 / 62 + 1 / 61)
    assert scores[c] == pytest.approx(1 / 62)
    assert max(scores, key=scores.get) == b  # found by both legs, so it wins


def test_snippets_drop_the_title_line_and_stay_short():
    assert snippet("My title\n\nThe body text.") == "The body text."
    long = snippet("T\n\n" + "word " * 200)
    assert len(long) <= 241 and long.endswith("…") and not long.startswith("T")


def test_a_snippet_starts_at_the_sentence_with_the_match():
    filler = " ".join(f"Filler sentence {i} about nothing." for i in range(30))
    body = f"T\n\n{filler} The postgres pool is sized by the driver. More filler follows here."
    out = snippet(body, "how to size a postgres pool")
    assert out.startswith("The postgres pool is sized")
    assert snippet(body, "unrelated words entirely").startswith("Filler sentence 0")  # no match: the start


def test_a_snippet_is_never_empty_for_a_chunk_with_text():
    assert snippet("Title\n\nShort.", "zzz") == "Short."


# ---------- search ----------


async def test_a_synonym_finds_the_resource_although_no_word_matches(client, embedder):
    user = uuid.uuid4()
    car = await index(client, user, "Weekend notes", "Spent the day washing the automobile and checking the tyres.")
    await index(client, user, "Training", "Teaching the puppy to sit and stay.")
    items = await find(client, user, "vehicle")
    assert items[0]["resource_id"] == str(car)
    assert "automobile" in items[0]["snippet"]
    assert "Weekend notes" not in items[0]["snippet"]


async def test_an_exact_rare_term_is_found_by_the_keyword_leg_even_when_vectors_know_nothing(client, embedder):
    user = uuid.uuid4()
    target = await index(client, user, "Parts list", "The part number is zq7781 and it ships on Tuesday.")
    await index(client, user, "Other", "Teaching the puppy to sit.")
    assert (await find(client, user, "zq7781"))[0]["resource_id"] == str(target)


async def test_one_result_per_resource_with_its_best_chunk_as_the_snippet(client, embedder):
    user = uuid.uuid4()
    filler = " ".join(f"Filler sentence {i} about nothing in particular here." for i in range(80))
    res = await index(client, user, "Long", filler + " The postgres database chapter explains sql. " + filler)
    items = await find(client, user, "postgres sql database")
    assert [i["resource_id"] for i in items].count(str(res)) == 1
    assert "postgres" in items[0]["snippet"]


async def test_results_are_ranked_by_how_well_they_match_and_limited(client, embedder):
    user = uuid.uuid4()
    ids = [await index(client, user, f"Doc {i}", "A car is a vehicle. " * (i + 1)) for i in range(5)]
    await index(client, user, "Unrelated", "Teaching the puppy.")
    items = await find(client, user, "automobile vehicle car", limit=3)
    assert len(items) == 3
    assert {i["resource_id"] for i in items} <= {str(i) for i in ids}
    scores = [i["score"] for i in items]
    assert scores == sorted(scores, reverse=True)


async def test_search_only_sees_the_callers_own_resources(client, embedder):
    mine, theirs = uuid.uuid4(), uuid.uuid4()
    await index(client, theirs, "Their car", "A secret vehicle shipment.")
    assert await find(client, mine, "vehicle") == []
    own = await index(client, mine, "My car", "My vehicle.")
    assert [i["resource_id"] for i in await find(client, mine, "vehicle")] == [str(own)]


async def test_search_can_be_scoped_to_a_collection(client, embedder):
    user, col = uuid.uuid4(), uuid.uuid4()
    inside = await index(client, user, "In", "A car.", collection=col)
    await index(client, user, "Out", "Another car.")
    assert [i["resource_id"] for i in await find(client, user, "car", collection_id=str(col))] == [str(inside)]


async def test_chunks_from_another_model_are_ignored(client, embedder, db):
    user, res = uuid.uuid4(), uuid.uuid4()
    with db.begin() as c:
        c.execute(
            text(
                "INSERT INTO ai.chunks (id, user_id, resource_id, chunk_index, content, content_hash, embedding_model, embedding) "
                "VALUES (:i, :u, :r, 0, 'T\n\ncar vehicle', 'h', 'old-model', CAST('[1,0,0,0.5]' AS public.vector))"
            ),
            {"i": uuid.uuid4(), "u": user, "r": res},
        )
    assert await find(client, user, "vehicle") == []


async def test_nothing_indexed_means_no_results_not_an_error(client, embedder):
    assert await find(client, uuid.uuid4(), "anything") == []


@pytest.mark.parametrize(
    "body", [{}, {"query": ""}, {"query": "x" * 501}, {"query": "ok", "limit": 0}, {"query": "ok", "limit": 51}]
)
async def test_bad_search_requests_are_validation_problems(client, embedder, body):
    r = await client.post("/search", json=body, headers=auth(uuid.uuid4()))
    assert r.status_code == 400 and r.json()["code"] == "validation_failed"


async def test_search_is_metered_and_refunded_when_it_fails(client, embedder, db):
    user = uuid.uuid4()
    await find(client, user, "car")
    embedder.fail = True
    with pytest.raises(RuntimeError):
        await client.post("/search", json={"query": "car"}, headers=auth(user))
    with db.connect() as c:
        n = c.execute(
            text("SELECT count(*) FROM ai.usage WHERE user_id = :u AND operation = 'search'"), {"u": user}
        ).scalar_one()
    assert n == 1


async def test_search_over_the_quota_is_429(app, client, embedder):
    app.state.settings = app.state.settings.model_copy(update={"quota_search": 1})
    user = uuid.uuid4()
    await find(client, user, "car")
    r = await client.post("/search", json={"query": "car"}, headers=auth(user))
    assert r.status_code == 429 and r.json()["code"] == "ai_quota_exceeded"


async def test_search_needs_a_user_token(client, embedder):
    assert (await client.post("/search", json={"query": "x"})).status_code == 401


# ---------- related ----------


async def related_ids(client, user, res, **q):
    r = await client.get(f"/resources/{res}/related", params=q, headers=auth(user))
    assert r.status_code == 200, r.text
    return [i["resource_id"] for i in r.json()["items"]]


async def test_related_resources_are_the_nearest_others_and_never_itself(client, embedder):
    user = uuid.uuid4()
    me = await index(client, user, "Sedan review", "A sedan is a car.")
    sibling = await index(client, user, "Automobile care", "How to wash an automobile.")
    far = await index(client, user, "Puppy", "A puppy and a canine.")
    got = await related_ids(client, user, me)
    assert got[0] == str(sibling)
    assert str(me) not in got
    assert str(far) in got and got.index(str(far)) > got.index(str(sibling))


async def test_related_respects_the_limit_and_the_owner(client, embedder):
    user, stranger = uuid.uuid4(), uuid.uuid4()
    me = await index(client, user, "Me", "A car.")
    for i in range(4):
        await index(client, user, f"Other {i}", "A car.")
    await index(client, stranger, "Not yours", "A car.")
    assert len(await related_ids(client, user, me, limit=2)) == 2
    all_ids = await related_ids(client, user, me, limit=20)
    assert len(all_ids) == 4
    # Asking about someone else's resource ID reveals nothing.
    assert await related_ids(client, stranger, me) == []


async def test_related_for_an_unindexed_resource_is_empty(client, embedder):
    assert await related_ids(client, uuid.uuid4(), uuid.uuid4()) == []


async def test_deleted_resources_drop_out_of_related(client, embedder):
    user = uuid.uuid4()
    me = await index(client, user, "Me", "A car.")
    gone = await index(client, user, "Gone", "A car.")
    await client.delete(f"/index/{gone}", headers=auth(user))
    assert str(gone) not in await related_ids(client, user, me)


# ---------- the real index ----------


def test_the_vector_query_can_use_the_models_hnsw_index(db):
    """Same SQL as the search uses. On 30 rows the planner would rather use a btree index, so those are dropped
    inside a transaction that is rolled back; if the query then still cannot use HNSW, the index does not fit it."""
    rnd = random.Random(7)
    conn = db.connect()
    tx = conn.begin()
    try:
        user, res = uuid.uuid4(), uuid.uuid4()
        for i in range(30):
            conn.execute(
                text(
                    "INSERT INTO ai.chunks (id, user_id, resource_id, chunk_index, content, content_hash, embedding_model, embedding) "
                    "VALUES (:i, :u, :r, :n, 'c', 'h', 'BAAI/bge-small-en-v1.5', CAST(:e AS public.vector))"
                ),
                {
                    "i": uuid.uuid4(),
                    "u": user,
                    "r": res,
                    "n": i,
                    "e": vector_literal([rnd.random() for _ in range(384)]),
                },
            )
        for name in ("ix_chunks_user_resource", "ix_chunks_user_collection", "ix_chunks_tsv"):
            conn.execute(text(f"DROP INDEX ai.{name}"))
        conn.execute(text("SET LOCAL enable_seqscan = off"))
        plan = "\n".join(
            r[0]
            for r in conn.execute(
                text("EXPLAIN " + vector_leg_sql(384)),
                {
                    "u": user,
                    "m": "BAAI/bge-small-en-v1.5",
                    "q": vector_literal([rnd.random() for _ in range(384)]),
                    "n": 50,
                },
            )
        )
    finally:
        tx.rollback()
        conn.close()
    assert "chunks_embedding_bge_small_hnsw" in plan, plan
