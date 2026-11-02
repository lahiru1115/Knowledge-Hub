import uuid

import pytest

from tests.conftest import make_token


async def test_a_valid_user_token_identifies_the_user(client, bearer):
    uid = uuid.uuid4()
    r = await client.get("/_t/user", headers=bearer(make_token(uid)))
    assert r.status_code == 200
    assert r.json() == {"user": str(uid)}


@pytest.mark.parametrize(
    "token",
    [
        pytest.param(make_token(secret="a-different-secret-0123456789abcdefghij"), id="wrong secret"),
        pytest.param(make_token(iss="someone-else"), id="wrong issuer"),
        pytest.param(make_token(aud="web"), id="wrong audience"),
        pytest.param(make_token(issued_ago=300), id="expired"),
        pytest.param(make_token(lifetime=3600), id="lives too long"),
        pytest.param(make_token(drop=("jti",)), id="no jti"),
        pytest.param(make_token(drop=("exp",)), id="no expiry"),
        pytest.param(make_token(drop=("sub",)), id="no subject"),
        pytest.param(make_token("not-a-uuid"), id="subject is not a user id"),
        pytest.param(make_token("system"), id="system token without a scope"),
        pytest.param(make_token(algorithm="HS512"), id="a different algorithm"),
        pytest.param("not.a.token", id="garbage"),
    ],
)
async def test_bad_tokens_are_refused_with_401(client, bearer, token):
    r = await client.get("/_t/user", headers=bearer(token))
    assert r.status_code == 401
    assert r.headers["content-type"].startswith("application/problem+json")
    assert r.json()["code"] == "unauthenticated"
    assert r.headers["www-authenticate"] == "Bearer"


async def test_the_unsigned_none_algorithm_is_refused(client, bearer):
    import base64
    import json

    def b64(d: dict) -> str:
        return base64.urlsafe_b64encode(json.dumps(d).encode()).rstrip(b"=").decode()

    import time

    now = int(time.time())
    token = (
        b64({"alg": "none", "typ": "JWT"})
        + "."
        + b64(
            {
                "iss": "core-api",
                "aud": "ai-service",
                "sub": str(uuid.uuid4()),
                "jti": "x",
                "iat": now,
                "exp": now + 60,
            }
        )
        + "."
    )
    assert (await client.get("/_t/user", headers=bearer(token))).status_code == 401


@pytest.mark.parametrize(
    "header", [{}, {"Authorization": "Basic abc"}, {"Authorization": "Bearer"}, {"Authorization": ""}]
)
async def test_a_missing_or_malformed_header_is_refused(client, header):
    assert (await client.get("/_t/user", headers=header)).status_code == 401


async def test_a_system_token_cannot_act_as_a_user(client, bearer):
    r = await client.get("/_t/user", headers=bearer(make_token("system", scope="users:purge")))
    assert r.status_code == 403
    assert r.json()["code"] == "forbidden"


async def test_scoped_calls_need_a_system_token_with_that_scope(client, bearer):
    ok = await client.get("/_t/purge", headers=bearer(make_token("system", scope="usage:read_all users:purge")))
    assert ok.status_code == 200
    wrong_scope = await client.get("/_t/purge", headers=bearer(make_token("system", scope="usage:read_all")))
    assert wrong_scope.status_code == 403
    user_token = await client.get("/_t/purge", headers=bearer(make_token(uuid.uuid4(), scope="users:purge")))
    assert user_token.status_code == 403  # a user token cannot claim a system scope


async def test_a_user_id_in_the_query_is_ignored(client, bearer):
    mine, other = uuid.uuid4(), uuid.uuid4()
    r = await client.get("/_t/user", params={"user_id": str(other)}, headers=bearer(make_token(mine)))
    assert r.json() == {"user": str(mine)}


async def test_unknown_paths_are_problem_details(client):
    r = await client.get("/nope")
    assert r.status_code == 404
    assert r.json()["code"] == "not_found"


async def test_liveness_needs_no_token_and_no_database(client):
    r = await client.get("/health/live")
    assert r.status_code == 200
    assert r.json() == {"status": "ok"}
