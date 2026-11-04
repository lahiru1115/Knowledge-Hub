import pytest
from pydantic import ValidationError

from app.config import Settings


def make(**over):
    base = {"AI_DATABASE_URL": "postgresql://x", "SERVICE_JWT_SECRET": "s" * 32}
    return Settings(**{**base, **over})  # type: ignore[arg-type]


def test_defaults_to_the_free_profile():
    assert make().ai_profile == "free"


def test_a_short_secret_is_refused():
    with pytest.raises(ValidationError):
        make(SERVICE_JWT_SECRET="too-short")


def test_an_unknown_profile_is_refused():
    with pytest.raises(ValidationError):
        make(AI_PROFILE="gpt")


def test_the_secret_and_database_url_have_no_defaults(monkeypatch):
    monkeypatch.delenv("SERVICE_JWT_SECRET", raising=False)
    monkeypatch.delenv("AI_DATABASE_URL", raising=False)
    with pytest.raises(ValidationError):
        Settings()  # type: ignore[call-arg]
