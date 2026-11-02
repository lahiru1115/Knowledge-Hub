from dataclasses import dataclass

from app.config import Settings
from app.providers.base import Embedder, Summarizer, Tagger
from app.providers.free.embedder import FastEmbedEmbedder
from app.providers.free.summarizer import ExtractiveSummarizer
from app.providers.free.tagger import YakeTagger


@dataclass(frozen=True)
class Providers:
    """What the service uses, chosen once at start-up from AI_PROFILE."""

    profile: str
    summarizer: Summarizer
    embedder: Embedder
    tagger: Tagger


def build_providers(settings: Settings) -> Providers:
    if settings.ai_profile == "free":
        embedder = FastEmbedEmbedder(settings.embedding_model, settings.model_cache_dir)
        return Providers("free", ExtractiveSummarizer(), embedder, YakeTagger(embedder))
    raise NotImplementedError("The Claude profile is added in step 5.9; use AI_PROFILE=free")
