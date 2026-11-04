"""Splits a document into overlapping chunks on paragraph and sentence boundaries (Doc 02 §4.3)."""

import re

TARGET_WORDS = 300
OVERLAP_WORDS = 50
MAX_DOCUMENT_CHARS = 200_000
_SENTENCE_END = re.compile(r"(?<=[.!?])\s+(?=[\"'(\[]?[A-Z0-9])")


def build_document(title: str | None, description: str | None, notes: str | None, text: str | None) -> str:
    """Title, description, the user's notes, then the extracted text (capped)."""
    parts = [p.strip() for p in (title, description, notes, (text or "")[:MAX_DOCUMENT_CHARS]) if p and p.strip()]
    return "\n\n".join(parts)


def _units(document: str, target: int) -> list[tuple[str, bool]]:
    """Sentences, each with a flag saying it ends a paragraph. A sentence longer than a chunk is cut by words."""
    units: list[tuple[str, bool]] = []
    for paragraph in re.split(r"\n\s*\n", document):
        sentences = [s.strip() for s in _SENTENCE_END.split(" ".join(paragraph.split())) if s.strip()]
        for i, sentence in enumerate(sentences):
            last = i == len(sentences) - 1
            words = sentence.split()
            if len(words) <= target:
                units.append((sentence, last))
                continue
            pieces = [" ".join(words[j : j + target]) for j in range(0, len(words), target)]
            units.extend((p, last and k == len(pieces) - 1) for k, p in enumerate(pieces))
    return units


def chunk_document(
    document: str,
    title: str | None = None,
    target_words: int = TARGET_WORDS,
    overlap_words: int = OVERLAP_WORDS,
) -> list[str]:
    """About `target_words` per chunk, each starting with the last ~`overlap_words` of the one before.

    Every chunk is prefixed with the title so it carries its context on its own.
    """
    units = _units(document, target_words)
    prefix = f"{title.strip()}\n\n" if title and title.strip() else ""
    chunks: list[str] = []
    current: list[tuple[str, int]] = []  # (sentence, words)
    fresh = 0  # words in `current` that are new (not carried over as overlap)

    def flush() -> None:
        chunks.append(prefix + " ".join(s for s, _ in current))

    for sentence, ends_paragraph in units:
        n = len(sentence.split())
        if current and fresh + n > target_words:
            flush()
            carried: list[tuple[str, int]] = []
            total = 0
            for s, w in reversed(current):
                if total + w > overlap_words:
                    break
                carried.insert(0, (s, w))
                total += w
            current, fresh = carried, 0
        current.append((sentence, n))
        fresh += n
        if ends_paragraph and fresh >= target_words * 0.75:
            flush()
            current, fresh = [], 0  # a natural break: start the next chunk clean
    if fresh > 0:
        flush()
    return chunks
