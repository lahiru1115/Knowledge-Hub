"""A small client for the Anthropic Messages API over httpx (plain HTTP keeps it easy to test with a mock transport)."""

import json
from collections.abc import AsyncIterator
from typing import Any

import httpx

from app.providers.base import LlmUnavailable, Usage

API_VERSION = "2023-06-01"


class AnthropicClient:
    def __init__(
        self,
        api_key: str,
        base_url: str = "https://api.anthropic.com",
        *,
        timeout: float = 20.0,
        first_token_timeout: float = 15.0,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self._key = api_key
        self._base = base_url.rstrip("/")
        self._timeout = timeout
        self._first = first_token_timeout
        self._transport = transport

    def _client(self, timeout: httpx.Timeout | float) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self._base,
            timeout=timeout,
            transport=self._transport,
            headers={"x-api-key": self._key, "anthropic-version": API_VERSION, "content-type": "application/json"},
        )

    async def message(
        self, model: str, system: str, user: str, *, max_tokens: int, temperature: float = 0.2
    ) -> tuple[str, Usage]:
        """One question, one answer. Raises LlmUnavailable for anything that stops it (limit, outage, bad key)."""
        body = {
            "model": model,
            "max_tokens": max_tokens,
            "temperature": temperature,
            "system": system,
            "messages": [{"role": "user", "content": user}],
        }
        try:
            async with self._client(self._timeout) as client:
                r = await client.post("/v1/messages", json=body)
        except httpx.HTTPError as exc:
            raise LlmUnavailable("The Claude API did not answer.") from exc
        _raise_for_status(r.status_code)
        try:
            data = r.json()
            text = "".join(b.get("text", "") for b in data["content"] if b.get("type") == "text")
            u = data.get("usage") or {}
            return text, Usage(int(u.get("input_tokens", 0)), int(u.get("output_tokens", 0)))
        except (ValueError, KeyError, TypeError, AttributeError) as exc:
            raise LlmUnavailable("The Claude API sent an answer that could not be read.") from exc

    async def stream(
        self, model: str, system: str, messages: list[dict[str, str]], *, max_tokens: int
    ) -> AsyncIterator[str | Usage]:
        """Text pieces as they arrive, then one `Usage`."""
        body = {
            "model": model,
            "max_tokens": max_tokens,
            "temperature": 0.2,
            "system": system,
            "messages": messages,
            "stream": True,
        }
        tokens_in = tokens_out = 0
        got_text = False
        try:
            # The read timeout is the wait for the next event: short before the first token (Doc 02 §5.2).
            timeout = httpx.Timeout(self._timeout, read=self._first)
            async with self._client(timeout) as client, client.stream("POST", "/v1/messages", json=body) as r:
                _raise_for_status(r.status_code)
                event = ""
                async for line in r.aiter_lines():
                    if line.startswith("event:"):
                        event = line[6:].strip()
                    elif line.startswith("data:"):
                        data = json.loads(line[5:].strip())
                        if event == "error" or data.get("type") == "error":
                            raise LlmUnavailable("The Claude API reported an error.")
                        if data.get("type") == "message_start":
                            tokens_in = int(((data.get("message") or {}).get("usage") or {}).get("input_tokens", 0))
                        elif data.get("type") == "content_block_delta":
                            delta: dict[str, Any] = data.get("delta") or {}
                            if delta.get("type") == "text_delta" and delta.get("text"):
                                got_text = True
                                yield delta["text"]
                        elif data.get("type") == "message_delta":
                            tokens_out = int((data.get("usage") or {}).get("output_tokens", tokens_out))
                        elif data.get("type") == "message_stop":
                            break
        except (httpx.HTTPError, json.JSONDecodeError) as exc:
            raise LlmUnavailable("The answer was cut short." if got_text else "The Claude API did not answer.") from exc
        yield Usage(tokens_in, tokens_out)

    async def reachable(self) -> bool:
        """True when the key is accepted. Listing models costs nothing."""
        try:
            async with self._client(2.0) as client:
                r = await client.get("/v1/models", params={"limit": 1})
            return r.status_code == 200
        except httpx.HTTPError:
            return False


def _raise_for_status(status: int) -> None:
    if status == 200:
        return
    # 401/403: the key is wrong. 429: the account's rate limit. 5xx and 529: Anthropic's side. All mean "not now".
    raise LlmUnavailable(f"The Claude API answered {status}.")
