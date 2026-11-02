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
    embedding_model: str = Field(default="BAAI/bge-small-en-v1.5", alias="EMBEDDING_MODEL")
    model_cache_dir: str | None = Field(default=None, alias="MODEL_CACHE_DIR")

    # Daily per-user limits (Appendix C). Leave unset for the profile's defaults; set to 0 or more to override.
    quota_summarize: int | None = Field(default=None, alias="QUOTA_SUMMARIZE", ge=0)
    quota_suggest_tags: int | None = Field(default=None, alias="QUOTA_SUGGEST_TAGS", ge=0)
    quota_search: int | None = Field(default=None, alias="QUOTA_SEARCH", ge=0)
    quota_chat: int | None = Field(default=None, alias="QUOTA_CHAT", ge=0)

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

    @field_validator("ai_profile")
    @classmethod
    def _known_profile(cls, v: str) -> str:
        if v not in {"free", "claude"}:
            raise ValueError("AI_PROFILE must be 'free' or 'claude'")
        return v


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
