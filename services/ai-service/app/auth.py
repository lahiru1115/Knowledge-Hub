"""Service-to-service authentication (Doc 02 §5.6).

The Core API signs a short-lived JWT for each call. The `sub` is the only source of the user's identity:
no endpoint takes a user ID from a request body. Calls that are not for one user use `sub = system` and a `scope`.
"""

import uuid
from collections.abc import Callable
from dataclasses import dataclass
from typing import Annotated

import jwt
from fastapi import Depends, Request

from app.config import Settings, get_settings
from app.errors import ApiError
from app.observability import user_id_var

SYSTEM = "system"
MAX_LIFETIME_SECONDS = 120


@dataclass(frozen=True)
class Caller:
    """Who is calling. `user_id` is set for user calls; `scopes` for system calls."""

    user_id: uuid.UUID | None
    scopes: frozenset[str]
    jti: str

    @property
    def is_system(self) -> bool:
        return self.user_id is None


def _decode(token: str, settings: Settings) -> Caller:
    try:
        claims = jwt.decode(
            token,
            settings.service_jwt_secret,
            algorithms=["HS256"],  # never accept the algorithm named in the token
            issuer=settings.service_jwt_issuer,
            audience=settings.service_jwt_audience,
            options={"require": ["exp", "iat", "iss", "aud", "sub", "jti"]},
        )
    except jwt.PyJWTError as exc:
        raise ApiError(401, "unauthenticated") from exc
    if claims["exp"] - claims["iat"] > MAX_LIFETIME_SECONDS:
        raise ApiError(401, "unauthenticated")  # a long-lived token is not one the Core API would issue
    sub = str(claims["sub"])
    scopes = frozenset(str(claims.get("scope", "")).split())
    if sub == SYSTEM:
        if not scopes:
            raise ApiError(401, "unauthenticated")
        return Caller(None, scopes, str(claims["jti"]))
    try:
        user = uuid.UUID(sub)
        user_id_var.set(str(user))  # the logs of this request name the user (an opaque ID)
        return Caller(user, frozenset(), str(claims["jti"]))
    except ValueError as exc:
        raise ApiError(401, "unauthenticated") from exc


async def caller(request: Request, settings: Annotated[Settings, Depends(get_settings)]) -> Caller:
    # async on purpose: a sync dependency runs in a thread with a copy of the context, and the user ID set while
    # decoding the token (for the logs) would then never reach the handler.
    header = request.headers.get("authorization", "")
    scheme, _, token = header.partition(" ")
    if scheme.lower() != "bearer" or not token:
        raise ApiError(401, "unauthenticated")
    return _decode(token.strip(), settings)


def current_user(who: Annotated[Caller, Depends(caller)]) -> uuid.UUID:
    """The user making the call. A system token is refused here: it cannot act as a user."""
    if who.user_id is None:
        raise ApiError(403, "forbidden")
    return who.user_id


def require_scope(scope: str) -> Callable[..., Caller]:
    def check(who: Annotated[Caller, Depends(caller)]) -> Caller:
        if who.user_id is not None or scope not in who.scopes:
            raise ApiError(403, "forbidden")
        return who

    return check
