import math
import re
from collections.abc import Sequence

import yake

from app.providers.base import Embedder

MAX_SUGGESTIONS = 5
MAX_TAG_LENGTH = 50  # same limit as the Core API (FR-TAG-01)
TEXT_CHARS = 2000
SIMILARITY_THRESHOLD = 0.75
_ALLOWED = re.compile(r"^[\w \-.#+]+$")


def normalise_tag(raw: str) -> str | None:
    """Lower case, one space between words; None when the Core API would refuse it."""
    name = re.sub(" +", " ", raw.strip().lower())  # only spaces collapse; tabs and line breaks stay and fail below
    if not 2 <= len(name) <= MAX_TAG_LENGTH or not _ALLOWED.match(name) or name.isdigit():
        return None
    return name


def cosine(a: Sequence[float], b: Sequence[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b, strict=True))
    na, nb = math.sqrt(sum(x * x for x in a)), math.sqrt(sum(y * y for y in b))
    return dot / (na * nb) if na and nb else 0.0


class YakeTagger:
    """Keyphrases from the title, description and start of the text; the user's own tags come first when they fit."""

    def __init__(self, embedder: Embedder | None = None) -> None:
        self._embedder = embedder
        self._yake = yake.KeywordExtractor(lan="en", n=2, dedupLim=0.7, top=15)

    async def suggest(
        self,
        title: str | None,
        description: str | None,
        text: str | None,
        existing_tags: Sequence[str],
    ) -> list[str]:
        # The title counts twice: it is the strongest signal of what a page is about.
        document = " . ".join(p for p in (title, title, description, (text or "")[:TEXT_CHARS]) if p and p.strip())
        if not document.strip():
            return []
        scored = sorted(self._yake.extract_keywords(document), key=lambda kv: kv[1])  # lower score = better
        phrases: list[str] = []
        for phrase, _ in scored:
            tag = normalise_tag(phrase)
            if tag and tag not in phrases:
                phrases.append(tag)

        words = set(re.findall(r"[\w.#+\-]+", document.lower()))
        fitting: list[str] = []
        similar = await self._similar_existing(document, existing_tags)
        for existing in existing_tags:
            tag = normalise_tag(existing)
            if not tag or tag in fitting:
                continue
            in_phrases = any(tag == p or tag in p.split() or p in tag.split() for p in phrases)
            if in_phrases or tag in words or tag in similar:
                fitting.append(tag)

        suggestions = fitting + [p for p in phrases if p not in fitting]
        return suggestions[:MAX_SUGGESTIONS]

    async def _similar_existing(self, document: str, existing_tags: Sequence[str]) -> set[str]:
        tags = [t for t in (normalise_tag(e) for e in existing_tags) if t]
        if self._embedder is None or not tags:
            return set()
        doc_vec, *tag_vecs = await self._embedder.embed_documents([document[: TEXT_CHARS + 500], *tags])
        return {t for t, v in zip(tags, tag_vecs, strict=True) if cosine(doc_vec, v) >= SIMILARITY_THRESHOLD}
