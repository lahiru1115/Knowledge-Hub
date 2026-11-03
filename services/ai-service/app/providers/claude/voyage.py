from collections.abc import Sequence

import httpx

BATCH = 128  # Voyage accepts at most this many texts in one request
DIMENSIONS = {"voyage-4-lite": 1024}


class EmbeddingUnavailable(Exception):
    """The embedding service did not give vectors (becomes an unavailable AI feature)."""


class VoyageEmbedder:
    """Embeddings from Voyage AI, 1,024 dimensions for voyage-4-lite (ADR-011)."""

    def __init__(
        self,
        api_key: str,
        model_id: str = "voyage-4-lite",
        base_url: str = "https://api.voyageai.com",
        *,
        dimensions: int | None = None,
        timeout: float = 20.0,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        if dimensions is None and model_id not in DIMENSIONS:
            raise ValueError(f"Unknown Voyage model '{model_id}'. Known: {', '.join(DIMENSIONS)}")
        self.model_id = model_id
        self.dimensions = dimensions if dimensions is not None else DIMENSIONS[model_id]
        self._key = api_key
        self._base = base_url.rstrip("/")
        self._timeout = timeout
        self._transport = transport

    async def _embed(self, texts: Sequence[str], input_type: str) -> list[list[float]]:
        out: list[list[float]] = []
        try:
            async with httpx.AsyncClient(
                base_url=self._base,
                timeout=self._timeout,
                transport=self._transport,
                headers={"Authorization": f"Bearer {self._key}"},
            ) as client:
                for i in range(0, len(texts), BATCH):
                    r = await client.post(
                        "/v1/embeddings",
                        json={"input": list(texts[i : i + BATCH]), "model": self.model_id, "input_type": input_type},
                    )
                    if r.status_code != 200:
                        raise EmbeddingUnavailable(f"Voyage answered {r.status_code}.")
                    rows = sorted(r.json()["data"], key=lambda d: d["index"])
                    vectors = [[float(x) for x in d["embedding"]] for d in rows]
                    if len(vectors) != len(texts[i : i + BATCH]) or any(len(v) != self.dimensions for v in vectors):
                        raise EmbeddingUnavailable("Voyage sent vectors of an unexpected number or size.")
                    out.extend(vectors)
        except httpx.HTTPError as exc:
            raise EmbeddingUnavailable("Voyage did not answer.") from exc
        except (KeyError, ValueError, TypeError, AttributeError) as exc:
            raise EmbeddingUnavailable("Voyage sent an answer that could not be read.") from exc
        return out

    async def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        return await self._embed(texts, "document") if texts else []

    async def embed_query(self, text: str) -> list[float]:
        return (await self._embed([text], "query"))[0]
