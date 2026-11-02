import re

from app.providers.base import NotEnoughContent, Summary

MAX_SUMMARY_CHARS = 600
MIN_DESCRIPTION_CHARS = 80
MIN_TEXT_CHARS = 80
_SENTENCE_END = re.compile(r"(?<=[.!?])\s+(?=[\"'(\[]?[A-Z0-9])")


def trim_to_sentences(text: str, limit: int = MAX_SUMMARY_CHARS) -> str:
    """Keeps whole sentences up to `limit` characters. A first sentence that is too long is cut at a word."""
    text = " ".join(text.split())
    if len(text) <= limit:
        return text
    out = ""
    for sentence in _SENTENCE_END.split(text):
        candidate = f"{out} {sentence}".strip()
        if len(candidate) > limit:
            break
        out = candidate
    if out:
        return out
    cut = text[: limit - 1].rsplit(" ", 1)[0].rstrip(",;:- ")
    return f"{cut}…"


class ExtractiveSummarizer:
    """The free profile's summary: the page's description or the opening of its text, labelled extracted (FR-AI-11)."""

    model_id = "extractive-v1"

    async def summarize(self, title: str | None, description: str | None, text: str | None) -> Summary:
        description = " ".join((description or "").split())
        if len(description) >= MIN_DESCRIPTION_CHARS:
            return Summary(trim_to_sentences(description), "extracted", self.model_id)
        paragraphs = [" ".join(p.split()) for p in re.split(r"\n\s*\n", text or "")]
        body = " ".join(p for p in paragraphs if len(p) >= 40)  # skip headings, menus and captions
        if len(body) < MIN_TEXT_CHARS:
            raise NotEnoughContent
        return Summary(trim_to_sentences(body), "extracted", self.model_id)
