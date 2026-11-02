"""The ai schema: chunks, summaries, chat sessions and messages, usage (Doc 02 §7.3)

Revision ID: 0001
Revises:
"""

from alembic import op

revision = "0001"
down_revision = None
branch_labels = None
depends_on = None

BGE_SMALL = "BAAI/bge-small-en-v1.5"


def upgrade() -> None:
    # ai_migrator owns the ai schema (infra/db/init.sql); the extension is created there by the superuser.
    op.execute(
        """
        CREATE TABLE ai.chunks (
            id              uuid PRIMARY KEY,
            user_id         uuid        NOT NULL,
            resource_id     uuid        NOT NULL,
            collection_id   uuid,
            chunk_index     integer     NOT NULL,
            content         text        NOT NULL,
            content_hash    text        NOT NULL,
            embedding_model text        NOT NULL,
            embedding       public.vector NOT NULL,
            tsv             tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED,
            created_at      timestamptz NOT NULL DEFAULT now(),
            UNIQUE (resource_id, chunk_index, embedding_model)
        )
        """
    )
    op.execute("CREATE INDEX ix_chunks_user_resource ON ai.chunks (user_id, resource_id)")
    op.execute("CREATE INDEX ix_chunks_user_collection ON ai.chunks (user_id, collection_id)")
    op.execute("CREATE INDEX ix_chunks_tsv ON ai.chunks USING gin (tsv)")
    # One partial HNSW index per embedding model: the column has no fixed dimension, each index casts to its own.
    op.execute(
        f"""
        CREATE INDEX chunks_embedding_bge_small_hnsw ON ai.chunks
        USING hnsw ((embedding::public.vector(384)) public.vector_cosine_ops)
        WHERE embedding_model = '{BGE_SMALL}'
        """
    )
    op.execute(
        """
        CREATE INDEX chunks_embedding_voyage_4_lite_hnsw ON ai.chunks
        USING hnsw ((embedding::public.vector(1024)) public.vector_cosine_ops)
        WHERE embedding_model = 'voyage-4-lite'
        """
    )

    op.execute(
        """
        CREATE TABLE ai.summaries (
            user_id      uuid        NOT NULL,
            content_hash text        NOT NULL,
            model        text        NOT NULL,
            summary      text        NOT NULL,
            source       text        NOT NULL CHECK (source IN ('extracted', 'ai')),
            created_at   timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, content_hash, model)
        )
        """
    )

    op.execute(
        """
        CREATE TABLE ai.chat_sessions (
            id         uuid PRIMARY KEY,
            user_id    uuid        NOT NULL,
            title      text,
            scope      jsonb       NOT NULL DEFAULT '{}'::jsonb,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute("CREATE INDEX ix_chat_sessions_user_updated ON ai.chat_sessions (user_id, updated_at DESC)")
    op.execute(
        """
        CREATE TABLE ai.chat_messages (
            id         uuid PRIMARY KEY,
            session_id uuid        NOT NULL REFERENCES ai.chat_sessions (id) ON DELETE CASCADE,
            role       text        NOT NULL CHECK (role IN ('user', 'assistant')),
            content    text        NOT NULL,
            citations  jsonb       NOT NULL DEFAULT '[]'::jsonb,
            created_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute("CREATE INDEX ix_chat_messages_session_created ON ai.chat_messages (session_id, created_at)")

    op.execute(
        """
        CREATE TABLE ai.usage (
            id         uuid PRIMARY KEY,
            user_id    uuid        NOT NULL,
            operation  text        NOT NULL
                       CHECK (operation IN ('summarize', 'suggest_tags', 'embed', 'search', 'chat')),
            provider   text        NOT NULL,
            model      text        NOT NULL,
            tokens_in  integer     NOT NULL DEFAULT 0,
            tokens_out integer     NOT NULL DEFAULT 0,
            cost_usd   numeric(12, 6) NOT NULL DEFAULT 0,
            created_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute("CREATE INDEX ix_usage_user_operation_created ON ai.usage (user_id, operation, created_at)")
    op.execute("CREATE INDEX ix_usage_created ON ai.usage (created_at)")


def downgrade() -> None:
    for table in ("usage", "chat_messages", "chat_sessions", "summaries", "chunks"):
        op.execute(f"DROP TABLE IF EXISTS ai.{table}")
