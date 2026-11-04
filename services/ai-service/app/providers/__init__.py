from dataclasses import dataclass

from app.config import Settings
from app.pricing import PriceTableError, parse_price_table
from app.providers.base import Embedder, LlmProvider, Summarizer, Tagger
from app.providers.claude.client import AnthropicClient
from app.providers.claude.provider import ClaudeProvider
from app.providers.claude.summarizer import ClaudeSummarizer
from app.providers.claude.tagger import ClaudeTagger
from app.providers.claude.voyage import VoyageEmbedder
from app.providers.free.embedder import FastEmbedEmbedder
from app.providers.free.ollama import OllamaProvider
from app.providers.free.summarizer import ExtractiveSummarizer
from app.providers.free.tagger import YakeTagger


class ConfigurationError(Exception):
    """The settings cannot run the chosen profile. The service refuses to start rather than fail later."""


@dataclass(frozen=True)
class Providers:
    """What the service uses, chosen once at start-up from AI_PROFILE."""

    profile: str
    summarizer: Summarizer
    embedder: Embedder
    tagger: Tagger
    llm: LlmProvider
    #: The free providers, used instead of the paid ones for summaries, tags and chat when the deployment's monthly
    #: cost cap is reached (Doc 02 §4.6). Embeddings never switch, as the index would no longer match.
    #: None in the free profile.
    fallback: "Providers | None" = None


def _ollama(settings: Settings, model: str) -> OllamaProvider:
    return OllamaProvider(
        settings.ollama_base_url,
        model,
        first_token_timeout=settings.chat_first_token_seconds,
        total_timeout=settings.chat_total_seconds,
    )


def build_providers(settings: Settings) -> Providers:
    if settings.ai_profile == "free":
        embedder = FastEmbedEmbedder(settings.embedding_model_id(), settings.model_cache_dir)
        return Providers(
            "free", ExtractiveSummarizer(), embedder, YakeTagger(embedder), _ollama(settings, settings.llm_model_id())
        )

    # The Claude profile: validated now, so a missing key is a start-up error and not a failed request at 2 a.m.
    missing = [
        n
        for n, v in (("ANTHROPIC_API_KEY", settings.anthropic_api_key), ("VOYAGE_API_KEY", settings.voyage_api_key))
        if not v
    ]
    if missing:
        raise ConfigurationError(f"AI_PROFILE=claude needs {' and '.join(missing)}.")
    table = parse_price_table(settings.price_table)
    in_use = {settings.summary_model_id(), settings.llm_model_id(), settings.embedding_model_id()}
    unpriced = sorted(m for m in in_use if m not in table)
    if unpriced:
        raise PriceTableError(
            f"PRICE_TABLE has no price for {', '.join(unpriced)}. The cost caps need one for every model in use "
            'in the form {"model-id": {"input": usd_per_million_tokens, "output": usd_per_million_tokens}}.'
        )
    assert settings.anthropic_api_key and settings.voyage_api_key  # for the type checker; checked above
    client = AnthropicClient(
        settings.anthropic_api_key,
        settings.anthropic_base_url,
        first_token_timeout=settings.chat_first_token_seconds,
    )
    voyage = VoyageEmbedder(settings.voyage_api_key, settings.embedding_model_id(), settings.voyage_base_url)
    free = Providers("free", ExtractiveSummarizer(), voyage, YakeTagger(None), _ollama(settings, "llama3.2:3b"))
    return Providers(
        "claude",
        ClaudeSummarizer(client, settings.summary_model_id()),
        voyage,
        ClaudeTagger(client, settings.summary_model_id()),
        ClaudeProvider(client, settings.llm_model_id()),
        fallback=free,
    )
