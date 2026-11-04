import json
import logging
import uuid

import pytest

from app.observability import JsonFormatter, redact, trace_id_of, trace_id_var, user_id_var
from tests.conftest import make_token


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("Sign-in failed for ada@example.com", "Sign-in failed for a***@example.com"),
        ("a.b+tag@sub.example.co.uk and x@y.io", "a***@sub.example.co.uk and x***@y.io"),
        ("Cookie: kh_at=abc.def.ghi; kh_csrf=123", "Cookie: [redacted]; kh_csrf=[redacted]"),
        ("X-CSRF-Token: 0123456789abcdef", "X-CSRF-Token: [redacted]"),
        ("sent Bearer abc123def to it", "sent Bearer [redacted] to it"),
        ("password=hunter2 ok", "password=[redacted] ok"),
        ('{"password":"hunter 2","x":1}', '{"password":"[redacted]","x":1}'),
        ("?token=abc123&page=2", "?token=[redacted]&page=2"),
        ('{"notes":"my private \\"quoted\\" thought"}', '{"notes":"[redacted]"}'),
        ("question=What did I save", "question=[redacted] did I save"),
        ("tokenizer ready; the context is large", "tokenizer ready; the context is large"),
        ("", ""),
    ],
)
def test_redaction(text, expected):
    assert redact(text) == expected


def test_a_jwt_is_removed_wherever_it_is():
    jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl"
    out = redact(f"token was {jwt} at the time, again {jwt}.")
    assert "eyJ" not in out and "[redacted]" in out


def _record(msg, *args, exc=None):
    return logging.LogRecord("app.test", logging.WARNING, __file__, 1, msg, args, exc)


def test_a_line_is_json_with_trace_and_user_and_is_redacted():
    t1 = trace_id_var.set("a" * 32)
    t2 = user_id_var.set("0193a000-0000-7000-8000-000000000001")
    try:
        line = JsonFormatter().format(_record("Failed for %s with password=%s", "ada@example.com", "hunter2"))
    finally:
        trace_id_var.reset(t1)
        user_id_var.reset(t2)
    data = json.loads(line)
    assert "\n" not in line
    assert (data["level"], data["category"], data["trace_id"]) == ("warn", "app.test", "a" * 32)
    assert data["user_id"] == "0193a000-0000-7000-8000-000000000001"
    assert data["message"] == "Failed for a***@example.com with password=[redacted]"


def test_an_exception_is_typed_and_redacted():
    try:
        raise ValueError("bad token=abc123 for ada@example.com")
    except ValueError:
        import sys

        data = json.loads(JsonFormatter().format(_record("boom", exc=sys.exc_info())))
    assert data["exception_type"] == "ValueError"
    assert "abc123" not in data["exception"] and "ada@example.com" not in data["exception"]


def test_a_line_without_a_request_has_no_trace():
    assert "trace_id" not in json.loads(JsonFormatter().format(_record("start-up")))


@pytest.mark.parametrize(
    ("header", "trace"),
    [
        ("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "4bf92f3577b34da6a3ce929d0e0e4736"),
        ("  00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01 ", "4bf92f3577b34da6a3ce929d0e0e4736"),
        ("00-" + "0" * 32 + "-00f067aa0ba902b7-01", None),
        ("garbage", None),
        (None, None),
    ],
)
def test_traceparent_parsing(header, trace):
    assert trace_id_of(header) == trace


async def test_a_request_keeps_the_callers_trace_and_user_in_its_logs(client, caplog):
    user = uuid.uuid4()
    trace = "4bf92f3577b34da6a3ce929d0e0e4736"
    seen: list[tuple[str | None, str | None]] = []

    class Capture(logging.Handler):
        def emit(self, record):
            seen.append((trace_id_var.get(), user_id_var.get()))

    log = logging.getLogger("app.test.request")
    handler = Capture()
    log.addHandler(handler)
    log.setLevel(logging.INFO)
    try:
        # Any route that authenticates and logs would do; the test routes in conftest authenticate only, so log from a dependency.
        from fastapi import Depends

        from app.auth import current_user

        app = client._transport.app

        @app.get("/_t/logged")
        async def logged(u: uuid.UUID = Depends(current_user)):  # noqa: B008
            log.info("handling %s", u)
            return {"ok": True}

        r = await client.get(
            "/_t/logged",
            headers={"Authorization": f"Bearer {make_token(user)}", "traceparent": f"00-{trace}-00f067aa0ba902b7-01"},
        )
    finally:
        log.removeHandler(handler)
    assert r.status_code == 200
    assert r.headers["x-trace-id"] == trace
    assert seen == [(trace, str(user))]
    assert trace_id_var.get() is None  # nothing leaks out of the request


async def test_a_request_without_traceparent_gets_a_trace_of_its_own(client):
    r = await client.get("/health/live")
    assert len(r.headers["x-trace-id"]) == 32
