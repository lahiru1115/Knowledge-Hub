import json
from collections.abc import AsyncIterator, Sequence

import httpx

from app.providers.base import LlmUnavailable, Usage

NUM_CTX = 8192  # Ollama's default is much smaller and would silently cut the sources off (Doc 02 §4.2)


class OllamaProvider:
    """Streams chat from a local Ollama server (ADR-009)."""

    def __init__(
        self,
        base_url: str,
        model_id: str,
        *,
        first_token_timeout: float = 15.0,
        total_timeout: float = 120.0,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self.model_id = model_id
        self._base_url = base_url.rstrip("/")
        self._first = first_token_timeout
        self._total = total_timeout
        self._transport = transport

    def _client(self, timeout: float) -> httpx.AsyncClient:
        return httpx.AsyncClient(base_url=self._base_url, timeout=timeout, transport=self._transport)

    async def available(self) -> bool:
        """True when the server answers and has the model."""
        try:
            async with self._client(2.0) as client:
                r = await client.get("/api/tags")
            if r.status_code != 200:
                return False
            names = {m.get("name", "") for m in r.json().get("models", [])}
            base = self.model_id.split(":")[0]
            return self.model_id in names or (":" not in self.model_id and any(n.split(":")[0] == base for n in names))
        except (httpx.HTTPError, ValueError):
            return False

    async def stream_chat(self, system: str, messages: Sequence[dict[str, str]]) -> AsyncIterator[str | Usage]:
        body = {
            "model": self.model_id,
            "stream": True,
            "messages": [{"role": "system", "content": system}, *messages],
            "options": {"num_ctx": NUM_CTX, "temperature": 0.2},
        }
        usage = Usage()
        got_token = False
        try:
            # The read timeout is the wait for the next piece: short before the first token, longer after.
            async with (
                self._client(self._total) as client,
                client.stream(
                    "POST", "/api/chat", json=body, timeout=httpx.Timeout(self._total, read=self._first)
                ) as r,
            ):
                if r.status_code != 200:
                    raise LlmUnavailable(f"The language model answered {r.status_code}.")
                async for line in r.aiter_lines():
                    if not line.strip():
                        continue
                    data = json.loads(line)
                    if "error" in data:
                        raise LlmUnavailable("The language model reported an error.")
                    piece = (data.get("message") or {}).get("content") or ""
                    if piece:
                        got_token = True
                        yield piece
                    if data.get("done"):
                        usage = Usage(int(data.get("prompt_eval_count") or 0), int(data.get("eval_count") or 0))
                        break
        except (httpx.HTTPError, json.JSONDecodeError) as exc:
            raise LlmUnavailable(
                "The language model did not answer." if not got_token else "The answer was cut short."
            ) from exc
        yield usage
