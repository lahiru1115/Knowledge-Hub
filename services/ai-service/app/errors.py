"""Problem Details responses in the same shape as the Core API (RFC 9457 with a `code`), so it can pass them on."""

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

TITLES = {
    "unauthenticated": "You need to sign in.",
    "forbidden": "You are not allowed to do that.",
    "validation_failed": "One or more fields are not valid.",
    "not_found": "Not found.",
    "ai_quota_exceeded": "Daily AI limit reached.",
    "internal_error": "Something went wrong.",
}


class ApiError(Exception):
    def __init__(self, status: int, code: str, detail: str | None = None, **extensions: object) -> None:
        self.status = status
        self.code = code
        self.detail = detail or TITLES.get(code, code)
        self.extensions = extensions


def problem(request: Request, status: int, code: str, detail: str | None = None, **ext: object) -> JSONResponse:
    body: dict[str, object] = {
        "type": f"/problems/{code}",
        "title": TITLES.get(code, code),
        "status": status,
        "detail": detail or TITLES.get(code, code),
        "instance": request.url.path,
        "code": code,
        **ext,
    }
    headers = {"WWW-Authenticate": "Bearer"} if status == 401 else None
    return JSONResponse(body, status_code=status, media_type="application/problem+json", headers=headers)


def install(app: FastAPI) -> None:
    @app.exception_handler(ApiError)
    async def _api_error(request: Request, exc: ApiError) -> JSONResponse:
        return problem(request, exc.status, exc.code, exc.detail, **exc.extensions)

    @app.exception_handler(RequestValidationError)
    async def _validation(request: Request, exc: RequestValidationError) -> JSONResponse:
        errors: dict[str, list[str]] = {}
        for e in exc.errors():
            field = ".".join(str(p) for p in e["loc"] if p not in {"body", "query"})
            errors.setdefault(field or "request", []).append(e["msg"])
        return problem(request, 400, "validation_failed", errors=errors)

    @app.exception_handler(StarletteHTTPException)
    async def _http(request: Request, exc: StarletteHTTPException) -> JSONResponse:
        code = {401: "unauthenticated", 403: "forbidden", 404: "not_found"}.get(exc.status_code, "internal_error")
        return problem(request, exc.status_code, code)
