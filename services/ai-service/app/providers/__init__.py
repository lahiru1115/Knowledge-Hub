from dataclasses import dataclass

from app.config import Settings
from app.providers.base import Embedder, LlmProvider, Summarizer, Tagger
from app.providers.free.embedder import FastEmbedEmbedder
from app.providers.free.ollama import OllamaProvider
from app.providers.free.summarizer import ExtractiveSummarizer
from app.providers.free.tagger import YakeTagger


@dataclass(frozen=True)
class Providers:
    """What the service uses, chosen once at start-up from AI_PROFILE."""

    profile: str
    summarizer: Summarizer
    embedder: Embedder
    tagger: Tagger
    llm: LlmProvider


def build_providers(settings: Settings) -> Providers:
    if settings.ai_profile == "free":
        embedder = FastEmbedEmbedder(settings.embedding_model, settings.model_cache_dir)
        llm = OllamaProvider(
            settings.ollama_base_url,
            settings.llm_model,
            first_token_timeout=settings.chat_first_token_seconds,
            total_timeout=settings.chat_total_seconds,
        )
        return Providers("free", ExtractiveSummarizer(), embedder, YakeTagger(embedder), llm)
    raise NotImplementedError("The Claude profile is added in step 5.9; use AI_PROFILE=free")
