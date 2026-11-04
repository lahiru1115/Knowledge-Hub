"""More decimal places for cost

Embedding costs are fractions of a thousandth of a cent per call. At six decimals each row rounded to the nearest
millionth of a dollar, so the daily and monthly caps would slowly under-count real spend.

Revision ID: 0003
Revises: 0002
"""

from alembic import op

revision = "0003"
down_revision = "0002"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute("ALTER TABLE ai.usage ALTER COLUMN cost_usd TYPE numeric(14, 8)")


def downgrade() -> None:
    op.execute("ALTER TABLE ai.usage ALTER COLUMN cost_usd TYPE numeric(12, 6)")
