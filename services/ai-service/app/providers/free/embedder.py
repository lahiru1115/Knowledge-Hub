import asyncio
from collections.abc import Sequence
from typing import Any

DEFAULT_MODEL = "BAAI/bge-small-en-v1.5"
DIMENSIONS = {"BAAI/bge-small-en-v1.5": 384}


class FastEmbedEmbedder:
    """Local ONNX embeddings on the CPU (ADR-011). The model loads on first use, so start-up never downloads."""

    def __init__(
        self, model_id: str = DEFAULT_MODEL, cache_dir: str | None = None, dimensions: int | None = None
    ) -> None:
        # `dimensions` is for trying a candidate model in the evaluation; the service itself only runs known models.
        if dimensions is None and model_id not in DIMENSIONS:
            raise ValueError(f"Unknown embedding model '{model_id}'. Known: {', '.join(DIMENSIONS)}")
        self.model_id = model_id
        self.dimensions = dimensions if dimensions is not None else DIMENSIONS[model_id]
        self._cache_dir = cache_dir
        self._model: Any = None
        self._lock = asyncio.Lock()

    async def _load(self) -> Any:
        async with self._lock:
            if self._model is None:
                from fastembed import TextEmbedding  # imported late: it pulls in onnxruntime

                self._model = await asyncio.to_thread(TextEmbedding, self.model_id, cache_dir=self._cache_dir)
            return self._model

    async def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        if not texts:
            return []
        model = await self._load()
        # Some models want a prefix on documents; fastembed adds it in passage_embed where the model defines one.
        embed = getattr(model, "passage_embed", None)
        if embed is not None:
            vectors = await asyncio.to_thread(lambda: [v.tolist() for v in embed(list(texts), batch_size=32)])
        else:
            vectors = await asyncio.to_thread(lambda: [v.tolist() for v in model.embed(list(texts), batch_size=32)])
        return vectors

    async def embed_query(self, text: str) -> list[float]:
        model = await self._load()
        return await asyncio.to_thread(lambda: next(iter(model.query_embed(text))).tolist())
