"""documents: the extracted text of each indexed resource

The Core API sends a page's text once, when it changes. Later edits to the title, notes or collection re-index
from the stored text, so the Core API never has to keep or resend it. Removed on purge (DR-06).

Revision ID: 0002
Revises: 0001
"""

from alembic import op

revision = "0002"
down_revision = "0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        """
        CREATE TABLE ai.documents (
            user_id     uuid        NOT NULL,
            resource_id uuid        NOT NULL,
            text        text        NOT NULL,
            updated_at  timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, resource_id)
        )
        """
    )


def downgrade() -> None:
    op.execute("DROP TABLE IF EXISTS ai.documents")
