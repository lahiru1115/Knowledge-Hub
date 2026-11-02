"""Provider interfaces (Doc 02 §4.2, ADR-006). The profile picks the implementations once, at start-up."""

from collections.abc import AsyncIterator, Sequence
from dataclasses import dataclass
from typing import Literal, Protocol

SummarySource = Literal["extracted", "ai"]


class NotEnoughContent(Exception):
    """There is too little text to summarise (becomes `422 not_enough_content`)."""


@dataclass(frozen=True)
class Summary:
    text: str
    source: SummarySource
    model: str


@dataclass(frozen=True)
class Usage:
    """Tokens a provider reports for one call; zero for local providers."""

    tokens_in: int = 0
    tokens_out: int = 0


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


class LlmProvider(Protocol):
    model_id: str

    def stream_chat(self, system: str, messages: Sequence[dict[str, str]]) -> AsyncIterator[str]: ...
