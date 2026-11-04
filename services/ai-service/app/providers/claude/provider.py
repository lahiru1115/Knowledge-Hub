import time
from collections.abc import AsyncIterator, Sequence

from app.providers.base import LlmUnavailable, Usage
from app.providers.claude.client import AnthropicClient

REACHABLE_CACHE_SECONDS = 30.0
MAX_ANSWER_TOKENS = 1024


class ClaudeProvider:
    """Chat answers streamed from a Claude model. It is given no tools, so it can only write text."""

    def __init__(self, client: AnthropicClient, model_id: str) -> None:
        self._client = client
        self.model_id = model_id
        self._checked_at = float("-inf")
        self._reachable = False

    async def available(self) -> bool:
        # The readiness check is polled, so the answer is remembered rather than asked of Anthropic each time.
        if time.monotonic() - self._checked_at > REACHABLE_CACHE_SECONDS:
            self._reachable = await self._client.reachable()
            self._checked_at = time.monotonic()
        return self._reachable

    async def stream_chat(self, system: str, messages: Sequence[dict[str, str]]) -> AsyncIterator[str | Usage]:
        try:
            async for piece in self._client.stream(self.model_id, system, list(messages), max_tokens=MAX_ANSWER_TOKENS):
                yield piece
        except LlmUnavailable:
            self._checked_at = float("-inf")  # look again next time instead of trusting an old "up"
            raise
