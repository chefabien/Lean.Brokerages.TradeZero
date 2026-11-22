"""FastAPI app factory. Run with `python -m tz_mock` (or `uvicorn tz_mock.main:app`)."""

from __future__ import annotations

import asyncio
import os

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response
from starlette.exceptions import HTTPException

from . import errors, routes_mock, routes_tz
from .sessions import SessionStore


def create_app() -> FastAPI:
    app = FastAPI(title="tz-mock", description="Mock of the TradeZero Trading API")
    app.state.sessions = SessionStore()
    app.include_router(routes_tz.router)
    app.include_router(routes_mock.router)

    @app.middleware("http")
    async def _inject_faults(request: Request, call_next):
        key = request.headers.get("TZ-API-KEY-ID")
        session = app.state.sessions.peek(key) if key else None
        fault = None
        if session is not None and request.url.path.startswith("/v1/api"):
            fault = session.take_fault(request.method, request.url.path)
        if fault is None:
            return await call_next(request)
        if fault.delay_ms:
            await asyncio.sleep(fault.delay_ms / 1000)
        if fault.when == "after":
            await call_next(request)  # processed, but the client never sees the answer
        return Response(fault.body, status_code=fault.status,
                        media_type="text/plain; charset=utf-8" if fault.body else None)

    @app.exception_handler(errors.TZError)
    async def _tz_error(request: Request, exc: errors.TZError) -> Response:
        return exc.response

    @app.exception_handler(HTTPException)
    async def _http_error(request: Request, exc: HTTPException) -> Response:
        if exc.status_code == 404:
            return errors.not_found()
        if exc.status_code == 405:
            return Response("405 method not allowed", status_code=405, media_type="text/plain")
        return JSONResponse({"detail": exc.detail}, status_code=exc.status_code)

    @app.exception_handler(RequestValidationError)
    async def _validation_error(request: Request, exc: RequestValidationError) -> Response:
        if request.url.path.startswith("/mock"):
            return JSONResponse({"error": "invalid request", "detail": exc.errors()}, status_code=400)
        return errors.not_found()

    return app


app = create_app()


def run() -> None:
    import uvicorn

    uvicorn.run("tz_mock.main:app", host=os.environ.get("TZ_MOCK_HOST", "127.0.0.1"),
                port=int(os.environ.get("TZ_MOCK_PORT", "8000")))
