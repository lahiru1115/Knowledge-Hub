"""Provider interfaces (Doc 02 §4.2, ADR-006). The profile picks the implementations once, at start-up."""

from collections.abc import AsyncIterator, Sequence
from dataclasses import dataclass
from typing import Literal, Protocol

SummarySource = Literal["extracted", "ai"]


class NotEnoughContent(Exception):
    """There is too little text to summarise (becomes `422 not_enough_content`)."""


@dataclass(frozen=True)
class Usage:
    """Tokens a provider reports for one call; zero for local providers."""

    tokens_in: int = 0
    tokens_out: int = 0


@dataclass(frozen=True)
class Summary:
    text: str
    source: SummarySource
    model: str
    #: What the call used, for metering and cost. Local providers leave it at zero.
    usage: Usage = Usage()


class Suggestions(list[str]):
    """Tag suggestions as a plain list, plus what producing them used (zero for local providers)."""

    def __init__(self, tags: Sequence[str] = (), usage: Usage | None = None) -> None:
        super().__init__(tags)
        self.usage = usage or Usage()


class Summarizer(Protocol):
    model_id: str

    async def summarize(self, title: str | None, description: str | None, text: str | None) -> Summary: ...


class Embedder(Protocol):
    model_id: str
    dimensions: int

    async def embed_documents(self, texts: Sequence[str]) -> list[list[float]]: ...

    async def embed_query(self, text: str) -> list[float]: ...


class Tagger(Protocol):
    async def suggest(
        self,
        title: str | None,
        description: str | None,
        text: str | None,
        existing_tags: Sequence[str],
    ) -> list[str]: ...


class LlmUnavailable(Exception):
    """The language model did not answer in time or at all (becomes an `ai_unavailable` event)."""


class LlmProvider(Protocol):
    """Streams an answer: text pieces, then one final `Usage`. It has no tools and cannot fetch or write anything."""

    model_id: str

    def stream_chat(self, system: str, messages: Sequence[dict[str, str]]) -> AsyncIterator[str | Usage]: ...

    async def available(self) -> bool: ...
