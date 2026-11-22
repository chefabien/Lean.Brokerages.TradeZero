"""Mock-only control endpoints: /mock/..."""

from __future__ import annotations

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from . import scenarios
from .marketdata import parse_dt
from .models import ts
from .sessions import Fault

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


class InventoryRequest(BaseModel):
    symbol: str
    available: int
    locateType: str = "Locate"  # Locate | PreBorrow | SingleUse


class LocateErrorRequest(BaseModel):
    symbol: str
    code: int  # locateError
    text: str
    status: int = 56  # locateStatus of the refused row
    count: int = 1  # how many of the next quotes for the symbol get it


class FaultRequest(BaseModel):
    method: str
    path: str
    status: int = 500
    count: int = 1
    when: str = "before"
    delayMs: int = 0
    body: str = ""


@router.post("/locates/inventory")
async def set_inventory(request: Request, req: InventoryRequest):
    """Set one inventory row's `available` shares without a quote (seed, or simulate another consumer)."""
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    b = request.app.state.sessions.get(key).broker
    try:
        inv = b.set_inventory(req.symbol, req.locateType, req.available)
    except ValueError as e:
        return JSONResponse({"error": str(e)}, 400)
    return inv.to_json()


@router.post("/locates/next-error")
async def next_locate_error(request: Request, req: LocateErrorRequest):
    """Make the next quote(s) for a symbol be refused with this row (locateType 0)."""
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    b = request.app.state.sessions.get(key).broker
    b.next_locate_errors.setdefault(req.symbol, []).extend([(req.status, req.code, req.text)] * req.count)
    return {"symbol": req.symbol, "queued": len(b.next_locate_errors[req.symbol])}


@router.post("/faults")
async def add_fault(request: Request, req: FaultRequest):
    """Fail the next matching /v1/api request(s) of this session with a bare HTTP status."""
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    if req.when not in ("before", "after"):
        return JSONResponse({"error": "when must be 'before' or 'after'"}, 400)
    session = request.app.state.sessions.get(key)
    session.faults.append(Fault(req.method, req.path, req.status, req.count, req.when, req.delayMs, req.body))
    return {"faults": [f.to_json() for f in session.faults]}


@router.get("/faults")
async def list_faults(request: Request):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    return {"faults": [f.to_json() for f in request.app.state.sessions.get(key).faults]}


@router.delete("/faults")
async def clear_faults(request: Request):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    request.app.state.sessions.get(key).faults.clear()
    return {"faults": []}


@router.delete("/session")
async def drop_session(request: Request):
    key = _key(request)
    if isinstance(key, JSONResponse):
        return key
    request.app.state.sessions.drop(key)
    return {"dropped": True}
