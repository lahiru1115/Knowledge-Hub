"""The cross-user usage report: system scope only, per UTC day, last 30 days."""

import uuid

import pytest
from sqlalchemy import create_engine, text

from tests.conftest import MIGRATOR_URL, make_token

pytestmark = pytest.mark.db
MODEL = "usage-report-test"


@pytest.fixture(scope="module")
def db():
    e = create_engine(MIGRATOR_URL.replace("postgresql://", "postgresql+psycopg://", 1))
    yield e
    e.dispose()


@pytest.fixture(autouse=True)
def clean(db):
    yield
    with db.begin() as c:
        c.execute(text("DELETE FROM ai.usage WHERE model = :m"), {"m": MODEL})


def system(scope="usage:read_all"):
    return {"Authorization": f"Bearer {make_token('system', scope=scope)}"}


def add(db, user, operation, days_ago, *, tokens_in=10, tokens_out=5, cost="0.5", hours_ago=0):
    with db.begin() as c:
        c.execute(
            text(
                "INSERT INTO ai.usage (id, user_id, operation, provider, model, tokens_in, tokens_out, cost_usd, created_at) "
                "VALUES (:id, :u, :o, 'claude', :m, :ti, :to, :c, "
                "date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' - make_interval(days => :d) + make_interval(hours => 12 - :h))"
            ),
            {
                "id": uuid.uuid4(),
                "u": user,
                "o": operation,
                "m": MODEL,
                "ti": tokens_in,
                "to": tokens_out,
                "c": cost,
                "d": days_ago,
                "h": hours_ago,
            },
        )


def mine(items, users):
    return [i for i in items if i.get("user_id") in users]


async def test_it_needs_the_usage_scope(client):
    assert (await client.get("/admin/usage")).status_code == 401
    assert (await client.get("/admin/usage", headers=system("users:purge"))).status_code == 403
    user = {"Authorization": f"Bearer {make_token(uuid.uuid4())}"}
    assert (await client.get("/admin/usage", headers=user)).status_code == 403


async def test_one_user_gets_a_row_per_day_and_operation_newest_first(client, db):
    me, other = uuid.uuid4(), uuid.uuid4()
    add(db, me, "chat", 0, tokens_in=100, tokens_out=40, cost="0.25")
    add(db, me, "chat", 0, tokens_in=50, tokens_out=10, cost="0.5")
    add(db, me, "embed", 0, cost="0.01")
    add(db, me, "chat", 3, cost="1")
    add(db, other, "chat", 0, cost="9")

    r = await client.get("/admin/usage", params={"user_id": str(me)}, headers=system())

    assert r.status_code == 200
    items = r.json()["items"]
    assert [(i["operation"], i["calls"]) for i in items] == [("chat", 2), ("embed", 1), ("chat", 1)]
    assert items[0]["date"] > items[2]["date"]
    assert (items[0]["tokens_in"], items[0]["tokens_out"], items[0]["cost_usd"]) == (150, 50, 0.75)
    assert {i["user_id"] for i in items} == {str(me)}


async def test_without_a_user_it_gives_totals_with_no_user_id(client, db):
    a, b = uuid.uuid4(), uuid.uuid4()
    add(db, a, "search", 1, cost="0.1")
    add(db, b, "search", 1, cost="0.2")

    items = (await client.get("/admin/usage", headers=system())).json()["items"]

    assert all("user_id" not in i or i["user_id"] is None for i in items)
    yesterday = [i for i in items if i["operation"] == "search"]
    assert sum(i["calls"] for i in yesterday) >= 2


async def test_only_the_last_thirty_days_are_counted(client, db):
    me = uuid.uuid4()
    add(db, me, "chat", 29)
    add(db, me, "chat", 30)
    add(db, me, "chat", 90)

    items = (await client.get("/admin/usage", params={"user_id": str(me)}, headers=system())).json()["items"]

    assert len(items) == 1


async def test_a_bad_user_id_is_a_validation_problem(client):
    r = await client.get("/admin/usage", params={"user_id": "nope"}, headers=system())
    assert r.status_code == 400 and r.json()["code"] == "validation_failed"
