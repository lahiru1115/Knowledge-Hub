FROM python:3.12-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy
COPY --from=ghcr.io/astral-sh/uv:0.5 /uv /usr/local/bin/uv
WORKDIR /srv
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project
COPY alembic.ini ./
COPY migrations ./migrations
COPY app ./app
RUN useradd --system --uid 10001 app && chown -R app /srv
USER app
ENV PATH="/srv/.venv/bin:$PATH"
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD python -c "import urllib.request as u; u.urlopen('http://127.0.0.1:8000/health/live', timeout=2)"
# The service never migrates itself. The one-shot `migrate-ai` job runs `alembic upgrade head` with this image
# as ai_migrator, before the service starts (Doc 02 §7.4).
CMD ["uvicorn", "app.main:create_app", "--factory", "--host", "0.0.0.0", "--port", "8000"]
