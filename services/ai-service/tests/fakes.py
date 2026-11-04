"""Stand-ins for the models, so tests need no downloads."""

import re
from collections.abc import AsyncIterator, Callable, Sequence

from app.providers.base import LlmUnavailable, Usage

GROUPS = [{"car", "automobile", "vehicle", "sedan"}, {"dog", "puppy", "canine"}, {"database", "postgres", "sql"}]


class ConceptEmbedder:
    """Words in the same group point the same way, so a synonym is close in vector space even with no shared word."""

    model_id = "concept-model"
    dimensions = 4

    def __init__(self) -> None:
        self.fail = False

    def _vec(self, t: str) -> list[float]:
        words = re.findall(r"\w+", t.lower())
        v = [float(sum(w in g for w in words)) for g in GROUPS]
        return [v[0], v[1], v[2], 0.5]  # a constant part, so text about nothing is not the same direction as anything

    async def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        if self.fail:
            raise RuntimeError("model unavailable")
        return [self._vec(t) for t in texts]

    async def embed_query(self, text: str) -> list[float]:
        return (await self.embed_documents([text]))[0]


class ScriptedLlm:
    """Replays a scripted answer in small pieces and records what it was asked."""

    model_id = "scripted-llm"

    def __init__(self) -> None:
        self.calls: list[tuple[str, list[dict[str, str]]]] = []
        self.pieces: list[str] = ["Pooling keeps connections open [1]."]
        self.usage = Usage(120, 12)
        self.error: Exception | None = None
        self.fail_after: int | None = None
        self.delay: Callable[[], Sequence[float]] | None = None
        self.up = True

    async def available(self) -> bool:
        return self.up

    async def stream_chat(self, system: str, messages: Sequence[dict[str, str]]) -> AsyncIterator[str | Usage]:
        self.calls.append((system, [dict(m) for m in messages]))
        if self.error:
            raise self.error
        for i, piece in enumerate(self.pieces):
            if self.fail_after is not None and i >= self.fail_after:
                raise LlmUnavailable("cut off")
            yield piece
        yield self.usage


class PaidSummarizer:
    """Stands in for a paid summariser: reports tokens, so cost can be checked."""

    model_id = "paid-small"

    def __init__(self, usage: Usage = Usage(1000, 200)) -> None:  # noqa: B008
        self.usage = usage
        self.calls = 0

    async def summarize(self, title, description, text):
        from app.providers.base import Summary

        self.calls += 1
        return Summary("A paid summary.", "ai", self.model_id, self.usage)
