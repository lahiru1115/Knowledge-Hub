import sys
import types

import numpy as np
import pytest

from app.config import Settings
from app.providers import build_providers
from app.providers.base import NotEnoughContent
from app.providers.free.embedder import FastEmbedEmbedder
from app.providers.free.summarizer import MAX_SUMMARY_CHARS, ExtractiveSummarizer, trim_to_sentences
from app.providers.free.tagger import YakeTagger, cosine, normalise_tag

ARTICLE = """
Npgsql Performance Tips

PostgreSQL connection pooling reduces the cost of opening connections. Npgsql keeps a pool of open connections and
reuses them between requests, which matters for web applications. Batching commands sends several statements in one
round trip. Prepared statements avoid parsing the same query repeatedly.

Another paragraph about reading large result sets with sequential access and streaming rows instead of buffering them.
"""


# ---------- summariser ----------


async def test_a_long_description_is_the_summary():
    desc = "A practical guide to connection pooling in Npgsql. It explains batching, prepared statements and more."
    s = await ExtractiveSummarizer().summarize("T", desc, "ignored")
    assert s.text == desc
    assert s.source == "extracted"


async def test_without_a_description_the_opening_of_the_text_is_used():
    s = await ExtractiveSummarizer().summarize("T", "short", ARTICLE)
    assert s.text.startswith("PostgreSQL connection pooling")
    assert "Npgsql Performance Tips" not in s.text  # a heading is not a paragraph
    assert len(s.text) <= MAX_SUMMARY_CHARS
    assert s.text.endswith(".")


async def test_the_summary_never_exceeds_600_characters_and_ends_on_a_sentence():
    long_text = " ".join(f"This is sentence number {i} about nothing in particular." for i in range(200))
    s = await ExtractiveSummarizer().summarize(None, None, long_text)
    assert len(s.text) <= MAX_SUMMARY_CHARS
    assert s.text.endswith(".")


def test_a_single_overlong_sentence_is_cut_at_a_word():
    out = trim_to_sentences("word " * 400)
    assert len(out) <= MAX_SUMMARY_CHARS
    assert out.endswith("…")
    assert not out[:-1].endswith(" ")


@pytest.mark.parametrize("text", [None, "", "Home\n\nAbout\n\nContact", "tiny"])
async def test_too_little_content_is_refused(text):
    with pytest.raises(NotEnoughContent):
        await ExtractiveSummarizer().summarize("Title only", "too short", text)


# ---------- tagger ----------


class FakeEmbedder:
    """Maps known words to fixed directions so similarity is predictable without a model."""

    model_id = "fake"
    dimensions = 3

    def __init__(self, vectors: dict[str, list[float]]):
        self.vectors = vectors

    async def embed_documents(self, texts):
        return [next((v for k, v in self.vectors.items() if k in t), [0.0, 0.0, 1.0]) for t in texts]

    async def embed_query(self, text):
        return (await self.embed_documents([text]))[0]


async def test_suggests_keyphrases_from_the_page():
    tags = await YakeTagger().suggest("Npgsql performance tips", "Connection pooling and batching", ARTICLE, [])
    assert 1 <= len(tags) <= 5
    assert any("npgsql" in t or "pooling" in t or "connection" in t for t in tags)
    assert all(t == t.lower() and 2 <= len(t) <= 50 for t in tags)


async def test_existing_tags_that_fit_come_first():
    tags = await YakeTagger().suggest(
        "Npgsql performance tips", "Connection pooling", ARTICLE, ["cooking", "postgresql", "npgsql"]
    )
    assert tags[:2] == ["postgresql", "npgsql"]
    assert "cooking" not in tags


async def test_an_existing_tag_close_in_meaning_is_included_even_if_the_word_is_absent():
    close = [1.0, 0.0, 0.0]
    embedder = FakeEmbedder({"pooling": close, "databases": close, "baking": [0.0, 1.0, 0.0]})
    tags = await YakeTagger(embedder).suggest("Connection pooling", None, ARTICLE, ["databases", "baking"])
    assert tags[0] == "databases"
    assert "baking" not in tags


async def test_at_most_five_and_no_duplicates():
    tags = await YakeTagger().suggest(
        "Python Rust Go Java Kotlin Swift Ruby", "Python Rust Go Java Kotlin Swift Ruby", ARTICLE * 3, []
    )
    assert len(tags) <= 5
    assert len(set(tags)) == len(tags)


@pytest.mark.parametrize(("title", "text"), [(None, None), ("", ""), ("  ", "  ")])
async def test_nothing_to_go_on_gives_no_suggestions(title, text):
    assert await YakeTagger().suggest(title, None, text, ["x"]) == []


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("  Machine   Learning ", "machine learning"),
        ("C#", "c#"),
        ("a", None),
        ("12345", None),
        ("bad/tag", None),
        ("x" * 51, None),
        ("a\tb", None),
    ],
)
def test_tag_names_follow_the_core_apis_rules(raw, expected):
    assert normalise_tag(raw) == expected


def test_cosine():
    assert cosine([1, 0], [1, 0]) == pytest.approx(1)
    assert cosine([1, 0], [0, 1]) == pytest.approx(0)
    assert cosine([0, 0], [1, 1]) == 0


# ---------- embedder ----------


def test_the_model_is_not_loaded_until_needed(monkeypatch):
    monkeypatch.setitem(sys.modules, "fastembed", None)  # importing it would fail
    e = FastEmbedEmbedder()
    assert (e.model_id, e.dimensions) == ("BAAI/bge-small-en-v1.5", 384)


def test_an_unknown_model_is_refused_with_the_known_ones_listed():
    with pytest.raises(ValueError, match=r"BAAI/bge-small-en-v1\.5"):
        FastEmbedEmbedder("made-up/model")


async def test_documents_and_queries_use_the_models_own_methods(monkeypatch):
    loaded: list[tuple[str, str | None]] = []

    class FakeTextEmbedding:
        def __init__(self, name, cache_dir=None):
            loaded.append((name, cache_dir))

        def embed(self, texts, batch_size):
            self.batch_size = batch_size
            return (np.full(384, float(i + 1)) for i, _ in enumerate(texts))

        def query_embed(self, text):
            return iter([np.full(384, 9.0)])

    monkeypatch.setitem(sys.modules, "fastembed", types.SimpleNamespace(TextEmbedding=FakeTextEmbedding))
    e = FastEmbedEmbedder(cache_dir="/models")
    docs = await e.embed_documents(["a", "b"])
    q = await e.embed_query("what")
    assert [d[0] for d in docs] == [1.0, 2.0]
    assert len(docs[0]) == 384 and q[0] == 9.0
    assert await e.embed_documents([]) == []
    assert loaded == [("BAAI/bge-small-en-v1.5", "/models")]  # loaded once


# ---------- profile ----------


def test_the_free_profile_builds_the_free_providers():
    p = build_providers(Settings(AI_DATABASE_URL="postgresql://x", SERVICE_JWT_SECRET="s" * 32))  # type: ignore[call-arg]
    assert p.profile == "free"
    assert p.embedder.model_id == "BAAI/bge-small-en-v1.5"
    assert p.summarizer.model_id == "extractive-v1"


def test_the_claude_profile_is_not_available_yet():
    s = Settings(AI_DATABASE_URL="postgresql://x", SERVICE_JWT_SECRET="s" * 32, AI_PROFILE="claude")  # type: ignore[call-arg]
    with pytest.raises(NotImplementedError):
        build_providers(s)
