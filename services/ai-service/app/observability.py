"""Structured, redacted JSON logs that carry the caller's trace (NFR-OBS-01, NFR-OBS-02, NFR-PRIV-04).

The Core API sends a W3C `traceparent` header with every call. Its trace ID goes on every log line written while that
request is handled, so a request, the outbox job and the AI work can be followed in one search. The redaction is the
same as in the Core API (`LogRedactor`): tokens, cookies, passwords and content fields are removed, emails masked.
"""

import contextvars
import json
import logging
import re
import time
import uuid

from starlette.types import ASGIApp, Message, Receive, Scope, Send

REDACTED = "[redacted]"

trace_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar("trace_id", default=None)
user_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar("user_id", default=None)

_JWT = re.compile(r"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")
_BEARER = re.compile(r"\bBearer\s+[^\s,;\"']+", re.IGNORECASE)
_HEADER = re.compile(
    r"\b(kh_at|kh_rt|kh_csrf|x-csrf-token|authorization|set-cookie|cookie)\b(\s*[=:]\s*)[^;\r\n]+", re.IGNORECASE
)
_FIELD = re.compile(
    r"(?P<k>\"?\b(?:password|new_password|current_password|passwd|secret|token|api[_-]?key|apikey|notes|content|summary"
    r"|description|text|query|question|answer)\b\"?)(?P<sep>\s*[=:]\s*)(?!\[redacted\])"
    r"(?P<v>\"(?:[^\"\\]|\\.)*\"|[^&\s,;}\]]+)",
    re.IGNORECASE,
)
_EMAIL = re.compile(r"\b([A-Za-z0-9])[A-Za-z0-9._%+-]*@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")
_TRACEPARENT = re.compile(r"^00-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$")


def redact(text: str | None) -> str:
    if not text:
        return text or ""
    text = _JWT.sub(REDACTED, text)
    text = _BEARER.sub("Bearer " + REDACTED, text)
    text = _HEADER.sub(lambda m: m.group(1) + m.group(2) + REDACTED, text)
    text = _FIELD.sub(
        lambda m: m.group("k") + m.group("sep") + ('"' + REDACTED + '"' if m.group("v").startswith('"') else REDACTED),
        text,
    )
    return _EMAIL.sub(r"\1***@\2", text)


class JsonFormatter(logging.Formatter):
    """One JSON object per line, with the trace and user of the request being handled where known."""

    def format(self, record: logging.LogRecord) -> str:
        entry: dict[str, object] = {
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(record.created)) + f".{int(record.msecs):03d}Z",
            "level": {"WARNING": "warn", "CRITICAL": "critical"}.get(record.levelname, record.levelname.lower()),
            "category": record.name,
            "message": redact(record.getMessage()),
        }
        if (trace := trace_id_var.get()) is not None:
            entry["trace_id"] = trace
        if (user := user_id_var.get()) is not None:
            entry["user_id"] = user
        if record.exc_info and record.exc_info[0] is not None:
            entry["exception_type"] = record.exc_info[0].__name__
            entry["exception"] = redact(self.formatException(record.exc_info))
        return json.dumps(entry, ensure_ascii=False)


def configure_logging(level: int = logging.INFO) -> None:
    """Replaces the root handlers with the JSON one. Safe to call twice."""
    root = logging.getLogger()
    for h in [h for h in root.handlers if getattr(h, "_kh", False)]:
        root.removeHandler(h)
    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())
    handler._kh = True  # type: ignore[attr-defined]
    root.addHandler(handler)
    root.setLevel(level)
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        lg = logging.getLogger(name)  # let them flow to the root handler instead of printing their own format
        lg.handlers.clear()
        lg.propagate = True


def trace_id_of(traceparent: str | None) -> str | None:
    m = _TRACEPARENT.match((traceparent or "").strip().lower())
    return m.group(1) if m and m.group(1) != "0" * 32 else None


class TraceContextMiddleware:
    """Reads `traceparent` into the log context for the duration of a request; makes one up when there is none."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        header = next((v.decode("latin-1") for k, v in scope["headers"] if k == b"traceparent"), None)
        trace = trace_id_of(header) or uuid.uuid4().hex
        t_token = trace_id_var.set(trace)
        u_token = user_id_var.set(None)

        async def send_with_trace(message: Message) -> None:
            if message["type"] == "http.response.start":
                message.setdefault("headers", []).append((b"x-trace-id", trace.encode()))
            await send(message)

        try:
            await self.app(scope, receive, send_with_trace)
        finally:
            trace_id_var.reset(t_token)
            user_id_var.reset(u_token)
