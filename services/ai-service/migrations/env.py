"""Alembic environment. Runs as ai_migrator; history lives in ai.alembic_version (Doc 02 §7.4)."""

import os
from logging.config import fileConfig

from alembic import context
from sqlalchemy import create_engine, pool

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

SCHEMA = "ai"


def _url() -> str:
    url = os.environ.get("AI_MIGRATOR_DATABASE_URL")
    if not url:
        raise RuntimeError("AI_MIGRATOR_DATABASE_URL is not set")
    for prefix in ("postgresql://", "postgres://"):
        if url.startswith(prefix):
            return "postgresql+psycopg://" + url[len(prefix) :]
    return url


def run_migrations_online() -> None:
    engine = create_engine(_url(), poolclass=pool.NullPool)
    with engine.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=None,
            version_table="alembic_version",
            version_table_schema=SCHEMA,
            include_schemas=True,
        )
        with context.begin_transaction():
            context.run_migrations()


run_migrations_online()
