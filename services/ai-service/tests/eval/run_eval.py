"""Model evaluation (Doc 02 §4.5, closes OI-03).

Indexes a small library, then measures retrieval, chat answers, citations, refusals, resistance to hostile pages and
the time to the first token. Run it with the real models on the reference machine::

    uv run python -m tests.eval.run_eval run --llm-model llama3.2:3b
    uv run python -m tests.eval.run_eval run --llm-model qwen3:4b
    uv run python -m tests.eval.run_eval run --embedding-model nomic-ai/nomic-embed-text-v1.5 --dimensions 768

then score the answers in the CSV by hand and combine them with ``summarise``. See README.md.
"""

import argparse
import asyncio
import json
import os
import time
import uuid
from collections.abc import Sequence
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from sqlalchemy.ext.asyncio import AsyncEngine

from app.chat import ChatService
from app.config import Settings
from app.indexing import IndexInput, IndexService
from app.providers import Providers
from app.quota import UsageMeter
from app.retrieval.hybrid import hybrid_search
from tests.eval import scoring

HERE = Path(__file__).parent
DATA = HERE / "data"
NAMESPACE = uuid.UUID("0f6a1c5e-6c1b-4c8e-9a43-1f8a7c0d2b11")
FIRST_TOKEN_LIMIT_SECONDS = 10.0  # NFR-PERF-05, p90


def load(data_dir: Path = DATA) -> dict[str, list[dict[str, Any]]]:
    return {
        name: json.loads((data_dir / f"{name}.json").read_text()) for name in ("resources", "questions", "injection")
    }


def rid(key: str) -> uuid.UUID:
    """A stable resource id for a dataset key, so reports can be compared between runs."""
    return uuid.uuid5(NAMESPACE, key)


async def _ask(
    engine: AsyncEngine,
    providers: Providers,
    user: uuid.UUID,
    question: str,
    *,
    min_similarity: float,
    total_seconds: float,
) -> dict[str, Any]:
    """Asks through the real chat service, timing the first token."""
    chats = ChatService(engine)
    meter = UsageMeter(engine, {}, providers.profile)  # no quota: the evaluation is not a user
    session = await chats.create(user, None)
    reservation = await meter.reserve(user, "chat", providers.llm.model_id)
    started = time.perf_counter()
    first: float | None = None
    chunks: list[str] = []
    async for piece in chats.ask(
        user,
        session,
        question,
        providers,
        meter,
        reservation,
        min_similarity=min_similarity,
        total_seconds=total_seconds,
    ):
        if first is None and piece.startswith("event: token"):
            first = time.perf_counter() - started
        chunks.append(piece)
    events = scoring.parse_sse(chunks)
    text = "".join(d["text"] for e, d in events if e == "token")
    cited = [c["resource_id"] for e, d in events if e == "citations" for c in d["items"]]
    error = next((d["code"] for e, d in events if e == "error"), None)
    return {
        "answer": text,
        "cited": cited,
        "cited_markers": [c["n"] for e, d in events if e == "citations" for c in d["items"]],
        "first_token_seconds": first,
        "total_seconds": time.perf_counter() - started,
        "error": error,
    }


async def run(
    engine: AsyncEngine,
    providers: Providers,
    *,
    data_dir: Path = DATA,
    min_similarity: float = 0.5,
    total_seconds: float = 120.0,
) -> dict[str, Any]:
    """Runs the whole evaluation for one pair of models and returns the report. Leaves no data behind."""
    data = load(data_dir)
    user = uuid.uuid4()  # a throwaway user, so nothing else in the database is touched
    index = IndexService(engine, providers.embedder, providers.profile)
    back = {str(rid(r["id"])): r["id"] for r in [*data["resources"], *data["injection"]]}

    try:
        for r in [*data["resources"], *data["injection"]]:
            await index.put(user, rid(r["id"]), IndexInput(r["title"], None, None, None, r["text"]))

        retrieval: list[dict[str, Any]] = []
        for q in data["questions"]:
            if not q["expected"]:
                continue
            hits = await hybrid_search(engine, providers.embedder, user, q["question"], limit=5)
            ranked = [back[str(h.resource_id)] for h in hits]
            retrieval.append(
                {
                    "id": q["id"],
                    "ranked": ranked,
                    "recall_at_5": scoring.recall_at_k(ranked, q["expected"], 5),
                    "reciprocal_rank": scoring.reciprocal_rank(ranked, q["expected"]),
                }
            )

        answers: list[dict[str, Any]] = []
        for q in data["questions"]:
            r = await _ask(
                engine, providers, user, q["question"], min_similarity=min_similarity, total_seconds=total_seconds
            )
            cited = [back.get(c, c) for c in r["cited"]]
            if q["kind"] == "unanswerable":
                ok = not cited and scoring.looks_like_refusal(r["answer"]) and r["error"] is None
            else:
                ok = (
                    r["error"] is None
                    and scoring.includes_any(r["answer"], q["must_include_any"])
                    and any(c in q["expected"] for c in cited)
                )
            answers.append(
                {
                    "id": q["id"],
                    "kind": q["kind"],
                    "question": q["question"],
                    "answer": r["answer"],
                    "cited": cited,
                    "citation_precision": scoring.citation_precision(cited, q["expected"]),
                    "auto_pass": ok,
                    "first_token_seconds": r["first_token_seconds"],
                    "total_seconds": r["total_seconds"],
                    "error": r["error"],
                }
            )

        attacks: list[dict[str, Any]] = []
        for page in data["injection"]:
            r = await _ask(
                engine, providers, user, page["question"], min_similarity=min_similarity, total_seconds=total_seconds
            )
            hits_found = scoring.injection_hits(r["answer"], page["canaries"])
            attacks.append(
                {
                    "id": page["id"],
                    "question": page["question"],
                    "answer": r["answer"],
                    "cited": [back.get(c, c) for c in r["cited"]],
                    "canaries_found": hits_found,
                    "resisted": not hits_found and r["error"] is None,
                    "error": r["error"],
                }
            )
    finally:
        await index.purge_user(user)

    times = [a["first_token_seconds"] for a in answers if a["first_token_seconds"] is not None]
    answerable = [a for a in answers if a["kind"] != "unanswerable"]
    unanswerable = [a for a in answers if a["kind"] == "unanswerable"]
    p90 = scoring.percentile(times, 90)
    return {
        "models": {"chat": providers.llm.model_id, "embedding": providers.embedder.model_id},
        "settings": {"min_similarity": min_similarity},
        "summary": {
            "retrieval_recall_at_5": scoring.mean([r["recall_at_5"] for r in retrieval]),
            "retrieval_mrr": scoring.mean([r["reciprocal_rank"] for r in retrieval]),
            "answerable_auto_pass": scoring.mean([1.0 if a["auto_pass"] else 0.0 for a in answerable]),
            "citation_precision": scoring.mean([a["citation_precision"] for a in answerable]),
            "refusal_rate": scoring.mean([1.0 if a["auto_pass"] else 0.0 for a in unanswerable]),
            "injection_resisted": f"{sum(1 for a in attacks if a['resisted'])}/{len(attacks)}",
            "first_token_p50_seconds": scoring.percentile(times, 50),
            "first_token_p90_seconds": p90,
            "first_token_within_limit": p90 is not None and p90 < FIRST_TOKEN_LIMIT_SECONDS,
            "errors": sum(1 for a in [*answers, *attacks] if a["error"]),
        },
        "retrieval": retrieval,
        "answers": answers,
        "attacks": attacks,
        "run_at": datetime.now(UTC).isoformat(),
    }


def markdown(report: dict[str, Any]) -> str:
    s = report["summary"]

    def pct(v: float | None) -> str:
        return "n/a" if v is None else f"{v * 100:.0f}%"

    def sec(v: float | None) -> str:
        return "n/a" if v is None else f"{v:.1f} s"

    rows = [
        ("Retrieval recall@5 (hybrid)", pct(s["retrieval_recall_at_5"])),
        ("Retrieval MRR", "n/a" if s["retrieval_mrr"] is None else f"{s['retrieval_mrr']:.2f}"),
        ("Answerable questions passing the automatic check", pct(s["answerable_auto_pass"])),
        ("Citation precision", pct(s["citation_precision"])),
        ("Refusal when the answer is not in the library", pct(s["refusal_rate"])),
        ("Hostile pages resisted", s["injection_resisted"]),
        ("First token, p50", sec(s["first_token_p50_seconds"])),
        (
            "First token, p90 (limit 10 s)",
            f"{sec(s['first_token_p90_seconds'])} ({'pass' if s['first_token_within_limit'] else 'FAIL'})",
        ),
        ("Requests that ended in an error", str(s["errors"])),
    ]
    lines = [
        f"# Evaluation: chat `{report['models']['chat']}`, embeddings `{report['models']['embedding']}`",
        "",
        f"Run at {report['run_at']}, minimum similarity {report['settings']['min_similarity']}.",
        "",
        "| Measure | Result |",
        "|---|---|",
        *[f"| {a} | {b} |" for a, b in rows],
        "",
        "The automatic check is a floor, not a verdict: score the answers by hand in the CSV (0, 1 or 2) and run `summarise`.",
    ]
    return "\n".join(lines) + "\n"


def write_outputs(report: dict[str, Any], out_dir: Path) -> list[Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%d-%H%M%S")
    safe = report["models"]["chat"].replace("/", "_").replace(":", "_")
    base = out_dir / f"{stamp}-{safe}"
    paths = [base.with_suffix(".json"), base.with_suffix(".md"), base.with_name(base.name + "-scores.csv")]
    paths[0].write_text(json.dumps(report, indent=1, ensure_ascii=False))
    paths[1].write_text(markdown(report))
    paths[2].write_text(scoring.manual_sheet(report["answers"]))
    return paths


def _providers(args: argparse.Namespace, settings: Settings) -> Providers:
    from app.providers.free.embedder import FastEmbedEmbedder
    from app.providers.free.ollama import OllamaProvider
    from app.providers.free.summarizer import ExtractiveSummarizer
    from app.providers.free.tagger import YakeTagger

    embedder = FastEmbedEmbedder(args.embedding_model, settings.model_cache_dir, args.dimensions)
    llm = OllamaProvider(
        args.ollama_url,
        args.llm_model,
        first_token_timeout=settings.chat_first_token_seconds,
        total_timeout=settings.chat_total_seconds,
    )
    return Providers("free", ExtractiveSummarizer(), embedder, YakeTagger(embedder), llm)


async def _main(argv: Sequence[str] | None) -> int:
    parser = argparse.ArgumentParser(prog="tests.eval.run_eval")
    sub = parser.add_subparsers(dest="command", required=True)
    r = sub.add_parser("run", help="run the evaluation against real models")
    r.add_argument("--llm-model", default="llama3.2:3b")
    r.add_argument("--embedding-model", default="BAAI/bge-small-en-v1.5")
    r.add_argument(
        "--dimensions", type=int, default=None, help="needed for an embedding model the service does not know"
    )
    r.add_argument("--ollama-url", default=os.environ.get("OLLAMA_BASE_URL", "http://localhost:11434"))
    r.add_argument("--min-similarity", type=float, default=0.5)
    r.add_argument("--out-dir", type=Path, default=HERE / "results")
    s = sub.add_parser("summarise", help="combine a hand-scored CSV into the final figures")
    s.add_argument("scores", type=Path)
    args = parser.parse_args(argv)

    if args.command == "summarise":
        print(json.dumps(scoring.summarise_manual(args.scores.read_text()), indent=1))
        return 0

    from app.db import make_engine

    settings = Settings(
        AI_DATABASE_URL=os.environ["AI_DATABASE_URL"],
        SERVICE_JWT_SECRET=os.environ.get("SERVICE_JWT_SECRET", "evaluation-only-secret-0123456789abcdef"),
    )
    engine = make_engine(settings)
    try:
        report = await run(
            engine,
            _providers(args, settings),
            min_similarity=args.min_similarity,
            total_seconds=settings.chat_total_seconds,
        )
    finally:
        await engine.dispose()
    for path in write_outputs(report, args.out_dir):
        print(path)
    print(markdown(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(_main(None)))
