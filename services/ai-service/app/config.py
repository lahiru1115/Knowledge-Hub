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
