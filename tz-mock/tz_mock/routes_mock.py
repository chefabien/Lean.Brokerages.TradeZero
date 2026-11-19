"""Mock-only control endpoints: /mock/..."""

from __future__ import annotations

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from . import scenarios
from .marketdata import parse_dt
from .models import ts

router = APIRouter(prefix="/mock", tags=["mock"])


class ScenarioRequest(BaseModel):
    name: str
    params: dict = {}


class TimeRequest(BaseModel):
    to: str


def _key(request: Request) -> str | JSONResponse:
    key = request.headers.get("TZ-API-KEY-ID")
    if not key:
        return JSONResponse({"error": "TZ-API-KEY-ID header required (it identifies the session)"}, 400)
    return key


@router.get("/scenarios")
async def list_scenarios():
    scenarios.load_all()
    return [{"name": c.name, "description": c.description} for c in scenarios.REGISTRY.values()]


@router.post("/scenario")
async def choose_scenario(request: Request, req: ScenarioRequest):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    scenarios.load_all()
    if req.name not in scenarios.REGISTRY:
        return JSONResponse({"error": f"unknown scenario {req.name!r}",
                             "available": sorted(scenarios.REGISTRY)}, 404)
    try:
        b = request.app.state.sessions.load(key, req.name, req.params).broker
    except ValueError as e:
        return JSONResponse({"error": str(e)}, 400)
    return {"scenario": b.scenario.name, "accountId": b.account_id, "now": ts(b.now)}


@router.post("/time")
async def move_time(request: Request, req: TimeRequest):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    b = request.app.state.sessions.get(key).broker
    try:
        target = parse_dt(req.to)
        fills = b.advance_to(target)
    except ValueError as e:
        return JSONResponse({"error": str(e), "now": ts(b.now)}, 400)
    return {"now": ts(b.now), "fills": [f.to_json() for f in fills]}


@router.get("/state")
async def state(request: Request):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    return request.app.state.sessions.get(key).broker.state_json()


@router.delete("/session")
async def drop_session(request: Request):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    request.app.state.sessions.drop(key)
    return {"dropped": True}
