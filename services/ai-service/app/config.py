from functools import lru_cache

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Configuration from environment variables (Doc 02 §10). Nothing here has a secret default."""

    model_config = SettingsConfigDict(env_file=None, extra="ignore")

    # The AI service connects as ai_app, which can use only the ai schema.
    ai_database_url: str = Field(alias="AI_DATABASE_URL")
    # Shared with the Core API, which signs each call (Doc 02 §5.6).
    service_jwt_secret: str = Field(alias="SERVICE_JWT_SECRET")
    service_jwt_issuer: str = "core-api"
    service_jwt_audience: str = "ai-service"
    ai_profile: str = Field(default="free", alias="AI_PROFILE")
    # Model IDs are configuration so newer models need no code change (IR-32).
    # Unset means the profile's own default (see the *_id methods below).
    embedding_model: str | None = Field(default=None, alias="EMBEDDING_MODEL")
    summary_model: str | None = Field(default=None, alias="SUMMARY_MODEL")
    model_cache_dir: str | None = Field(default=None, alias="MODEL_CACHE_DIR")
    # Claude profile only (FR-AI-01). Both keys are needed; the service refuses to start without them.
    anthropic_api_key: str | None = Field(default=None, alias="ANTHROPIC_API_KEY")
    voyage_api_key: str | None = Field(default=None, alias="VOYAGE_API_KEY")
    anthropic_base_url: str = Field(default="https://api.anthropic.com", alias="ANTHROPIC_BASE_URL")
    voyage_base_url: str = Field(default="https://api.voyageai.com", alias="VOYAGE_BASE_URL")
    #: JSON: {"model-id": {"input": usd_per_million, "output": usd_per_million}}. Required for the Claude profile.
    price_table: str | None = Field(default=None, alias="PRICE_TABLE")
    #: Cost caps, Claude profile (Doc 02 §4.6): per user per day, and for the whole deployment per month.
    daily_cost_cap_usd: float = Field(default=0.50, alias="DAILY_COST_CAP_USD", ge=0)
    monthly_cost_cap_usd: float = Field(default=10.0, alias="MONTHLY_COST_CAP_USD", ge=0)
    #: Re-embedding after the embedding model changes (FR-AI-01).
    reindex_enabled: bool = Field(default=True, alias="REINDEX_ENABLED")
    reindex_interval_seconds: float = Field(default=30.0, alias="REINDEX_INTERVAL_SECONDS", gt=0)

    ollama_base_url: str = Field(default="http://localhost:11434", alias="OLLAMA_BASE_URL")
    llm_model: str | None = Field(default=None, alias="LLM_MODEL")
    #: How close in meaning a chunk must be (cosine similarity) to count as relevant when no word matches.
    #: Not calibrated yet: the evaluation set (step 5.8) is where this gets tuned.
    chat_min_similarity: float = Field(default=0.5, alias="CHAT_MIN_SIMILARITY", ge=0, le=1)
    chat_first_token_seconds: float = Field(default=15.0, alias="CHAT_FIRST_TOKEN_SECONDS", gt=0)
    chat_total_seconds: float = Field(default=120.0, alias="CHAT_TOTAL_SECONDS", gt=0)

    # Daily per-user limits (Appendix C). Leave unset for the profile's defaults; set to 0 or more to override.
    quota_summarize: int | None = Field(default=None, alias="QUOTA_SUMMARIZE", ge=0)
    quota_suggest_tags: int | None = Field(default=None, alias="QUOTA_SUGGEST_TAGS", ge=0)
    quota_search: int | None = Field(default=None, alias="QUOTA_SEARCH", ge=0)
    quota_chat: int | None = Field(default=None, alias="QUOTA_CHAT", ge=0)

    def embedding_model_id(self) -> str:
        return self.embedding_model or ("voyage-4-lite" if self.ai_profile == "claude" else "BAAI/bge-small-en-v1.5")

    def llm_model_id(self) -> str:
        return self.llm_model or ("claude-sonnet-5-5" if self.ai_profile == "claude" else "llama3.2:3b")

    def summary_model_id(self) -> str:
        """The model for summaries and tag suggestions in the Claude profile (a small, cheap one)."""
        return self.summary_model or "claude-haiku-5-5"

    def quotas(self) -> dict[str, int | None]:
        """Daily limit per quota-bearing operation. None means no limit (Claude profile has no search limit)."""
        free = {"summarize": 300, "suggest_tags": 300, "search": 500, "chat": 100}
        claude = {"summarize": 100, "suggest_tags": 200, "search": None, "chat": 30}
        base: dict[str, int | None] = dict(free if self.ai_profile == "free" else claude)
        overrides = {
            "summarize": self.quota_summarize,
            "suggest_tags": self.quota_suggest_tags,
            "search": self.quota_search,
            "chat": self.quota_chat,
        }
        return {op: overrides[op] if overrides[op] is not None else limit for op, limit in base.items()}

    @field_validator("service_jwt_secret")
    @classmethod
    def _long_enough(cls, v: str) -> str:
        if len(v.encode()) < 32:
            raise ValueError("SERVICE_JWT_SECRET must be at least 32 bytes")
        return v

    @field_validator("price_table")
    @classmethod
    def _price_table_is_json(cls, v: str | None) -> str | None:
        from app.pricing import parse_price_table

        parse_price_table(v)  # raises a clear error now rather than at the first paid call
        return v

    @field_validator("ai_profile")
    @classmethod
    def _known_profile(cls, v: str) -> str:
        if v not in {"free", "claude"}:
            raise ValueError("AI_PROFILE must be 'free' or 'claude'")
        return v


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
