import itertools

from app.retrieval.chunker import build_document, chunk_document


def sentences(n: int, words: int = 10, start: int = 0) -> str:
    return " ".join(f"Sentence {start + i} " + "word " * (words - 3) + "end." for i in range(n))


def test_the_document_is_title_description_notes_then_text():
    assert build_document("T", "D", "N", "X") == "T\n\nD\n\nN\n\nX"
    assert build_document(None, "  ", "N", None) == "N"
    assert build_document(None, None, None, None) == ""


def test_the_text_is_capped():
    assert len(build_document(None, None, None, "a" * 300_000)) == 200_000


def test_a_short_document_is_one_chunk_with_the_title():
    assert chunk_document("Just a few words. Nothing more.", "My title") == [
        "My title\n\nJust a few words. Nothing more."
    ]


def test_no_title_means_no_prefix():
    assert chunk_document("Hello there.", None) == ["Hello there."]


def test_nothing_in_nothing_out():
    assert chunk_document("", "T") == []


def test_chunks_are_about_300_words_and_overlap_by_about_50():
    doc = sentences(200)  # 2,000 words
    chunks = chunk_document(doc, None)
    assert 6 <= len(chunks) <= 9
    for c in chunks[:-1]:
        assert 250 <= len(c.split()) <= 360
    # The start of each chunk repeats the end of the one before.
    for a, b in itertools.pairwise(chunks):
        tail = a.split(" end. ")[-3:]
        assert any(t.strip() and t.strip() in b for t in tail)


def test_every_sentence_is_in_some_chunk_and_the_order_is_kept():
    doc = sentences(150)
    chunks = chunk_document(doc, None)
    joined = " ".join(chunks)
    last_pos = -1
    for i in range(150):
        pos = joined.find(f"Sentence {i} ")
        assert pos >= 0, i
        assert pos >= last_pos
        last_pos = pos


def test_chunks_break_at_sentence_ends_not_in_the_middle():
    chunks = chunk_document(sentences(100), None)
    assert all(c.rstrip().endswith("end.") for c in chunks)


def test_a_paragraph_break_near_the_target_starts_a_clean_chunk():
    doc = sentences(24) + "\n\n" + sentences(24, start=24)  # 240 words, then 240 more
    chunks = chunk_document(doc, None)
    assert chunks[0].endswith("Sentence 23 " + "word " * 7 + "end.")
    assert chunks[1].startswith("Sentence 24 ")


def test_one_enormous_sentence_is_cut_by_words():
    chunks = chunk_document("word " * 1000, None)
    assert len(chunks) >= 3
    assert all(len(c.split()) <= 360 for c in chunks)


def test_every_chunk_carries_the_title():
    chunks = chunk_document(sentences(100), "Deep dive")
    assert len(chunks) > 1
    assert all(c.startswith("Deep dive\n\n") for c in chunks)
