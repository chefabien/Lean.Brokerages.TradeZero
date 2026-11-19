"""The TradeZero Trading API surface: /v1/api/..."""

from __future__ import annotations

import json

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response

from . import errors
from .broker import LOCATE_TYPES, Broker
from .errors import TZError
from .validation import validate_order

router = APIRouter(prefix="/v1/api")

PAPER_ROUTES = {"routes": [
    {"orderTypes": ["Market", "Limit", "Stop", "StopLimit", "RangeOrder"], "routeName": "PAPER",
     "securityTypes": ["Stock", "Option"], "timesInForce": ["Day", "GoodTillCancel", "GoodTillCrossing"],
     "useDisplayQty": False},
    {"orderTypes": ["Market", "Limit", "Stop", "StopLimit", "RangeOrder"], "routeName": "PAPERM",
     "securityTypes": ["MLEG"], "timesInForce": ["Day"], "useDisplayQty": False},
]}


def broker(request: Request, missing=errors.not_found) -> Broker:
    key = request.headers.get("TZ-API-KEY-ID")
    if not key:
        raise TZError(missing())
    return request.app.state.sessions.get(key).broker


def account(request: Request, account_id: str, operation: str) -> Broker:
    b = broker(request)
    if account_id != b.account_id:
        raise TZError(errors.wrong_account(operation, b.account_id, account_id))
    return b


async def json_body(request: Request) -> dict:
    try:
        body = json.loads(await request.body())
    except ValueError:
        raise TZError(errors.not_found())
    if not isinstance(body, dict):
        raise TZError(errors.not_found())
    return body


def require(body: dict, *fields: str) -> None:
    missing = [f"- (root): {f} is required" for f in fields if body.get(f) in (None, "")]
    if missing:
        raise TZError(errors.text_400(missing))


# ---------------------------------------------------------------------------- accounts
@router.get("/accounts")
async def list_accounts(request: Request):
    return {"accounts": [broker(request).account_json(detail=False)]}


@router.get("/account/{account_id}")
async def account_details(request: Request, account_id: str):
    return account(request, account_id, "GetAccountDetails").account_json(detail=True)


@router.get("/accounts/{account_id}/pnl")
async def pnl(request: Request, account_id: str):
    return account(request, account_id, "GetPnl").pnl_json()


# ---------------------------------------------------------------------------- positions
@router.get("/accounts/{account_id}/positions")
async def positions(request: Request, account_id: str):
    b = account(request, account_id, "GetPositions")
    return {"positions": [p.to_json() for p in b.positions]}


@router.get("/accounts/{account_id}/positions/closed")
async def closed_positions(request: Request, account_id: str):
    b = account(request, account_id, "GetClosedPositions")
    return {"closedPositions": [p.to_closed_json() for p in b.closed]}


@router.get("/accounts/{account_id}/positions/historical")
async def historical_positions(request: Request, account_id: str, date: str | None = None):
    b = account(request, account_id, "GetHistoricalPositions")
    if not date:
        return errors.bad_request("GetHistoricalPositions",
                                  f"{b.account_id} - {b.account_id} - Date is required.")
    return {"historicalPositions": []}


# ---------------------------------------------------------------------------- orders
@router.post("/accounts/{account_id}/order")
async def place_order(request: Request, account_id: str):
    b = account(request, account_id, "PlaceOrderWithResponse")
    body = validate_order(await json_body(request))
    return b.place(body)


@router.get("/accounts/{account_id}/orders")
async def todays_orders(request: Request, account_id: str):
    b = account(request, account_id, "GetOrders")
    return {"orders": [o.to_json() for o in b.today_orders()]}


@router.get("/accounts/{account_id}/order/{client_order_id}")
async def get_order(request: Request, account_id: str, client_order_id: str):
    b = account(request, account_id, "GetOrder")
    o = b.orders.get(client_order_id)
    if o is None:
        return errors.bad_request(
            "GetOrder", f"{b.account_id} - {b.account_id} - ASLib Unable to find specified order. Ok")
    return o.to_json()


@router.delete("/accounts/orders")
async def cancel_all(request: Request, symbol: str | None = None):
    b = broker(request)
    ctype = request.headers.get("content-type", "")
    if not ctype.startswith(("multipart/form-data", "application/x-www-form-urlencoded")):
        return errors.not_found()
    acc = (await request.form()).get("account")
    if not acc:
        return errors.not_found()
    if acc != b.account_id:
        return errors.bad_request(f"ASLib {errors.ENTITLEMENT}", None)
    b.cancel_all(symbol)
    return {"message": "Cancel Request Submitted Successfully"}


@router.delete("/accounts/{account_id}/orders/{client_order_id}")
async def cancel_order(request: Request, account_id: str, client_order_id: str):
    broker(request, missing=errors.unauthorized_json)
    b = account(request, account_id, "CancelOrderWithResponse")
    row = b.cancel(client_order_id)
    return errors.not_found() if row is None else row


@router.get("/accounts/{account_id}/routes")
async def routes(request: Request, account_id: str):
    account(request, account_id, "GetRoutes")
    return PAPER_ROUTES


@router.get("/accounts/{account_id}/is-easy-to-borrow/symbol/{symbol}")
async def is_easy_to_borrow(request: Request, account_id: str, symbol: str):
    b = account(request, account_id, "IsEasyToBorrow")
    return {"isEasyToBorrow": symbol not in b.scenario.htb}


# ---------------------------------------------------------------------------- history (empty)
@router.get("/accounts/{account_id}/orders/start-date/{start_date}")
async def historical_fills(request: Request, account_id: str, start_date: str):
    account(request, account_id, "GetHistoricalOrders")
    return Response("Authorization header is required for master controller.", status_code=401,
                    media_type="text/plain; charset=utf-8")


@router.get("/accounts/{account_id}/orders-with-pagination/start-date/{start_date}")
async def historical_orders(request: Request, account_id: str, start_date: str, offset: int = 0,
                            limit: int = 100, symbol: str | None = None):
    account(request, account_id, "GetHistoricalOrdersWithPagination")
    return {"pagination": {"currentLimit": limit, "currentOffset": offset, "specifiedSymbol": symbol,
                           "totalRecords": 0}, "tradingHistory": []}


@router.get("/accounts/{account_id}/cash-transactions/start-date/{start_date}")
async def cash_transactions(request: Request, account_id: str, start_date: str, offset: int = 0,
                            limit: int = 100):
    account(request, account_id, "GetCashTransactions")
    return {"cashHistory": [], "pagination": {"currentLimit": limit, "currentOffset": offset,
                                              "totalRecords": 0}}


# ---------------------------------------------------------------------------- locates
def _locate_account(b: Broker, given: str) -> None:
    if given != b.account_id:
        raise TZError(errors.bad_request(errors.ENTITLEMENT, None))


def _quantity(body: dict) -> int:
    q = body.get("quantity")
    if not isinstance(q, int) or isinstance(q, bool) or not 100 <= q <= 2147483647:
        raise TZError(JSONResponse(status_code=400, content={
            "errors": {"Quantity": ["The field Quantity must be between 100 and 2147483647."]},
            "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            "title": "One or more validation errors occurred.", "status": 400,
            "traceId": "00-mock-00"}))
    return q


def _dup() -> Response:
    return errors.text_400(["R114: Invalid duplicate UserOrderId"])


@router.post("/accounts/locates/quote")
async def locate_quote(request: Request):
    b, body = broker(request), await json_body(request)
    require(body, "account", "symbol", "quantity", "quoteReqID")
    _locate_account(b, body["account"])
    ok = b.locate_quote(body["symbol"], _quantity(body), str(body["quoteReqID"]))
    return {"locateQuoteSent": "true"} if ok else _dup()


@router.post("/accounts/locates/accept")
async def locate_accept(request: Request):
    b, body = broker(request), await json_body(request)
    require(body, "accountId", "quoteReqID")
    _locate_account(b, body["accountId"])
    b.locate_accept(str(body["quoteReqID"]))
    return {"locateAcceptSent": "true"}


@router.post("/accounts/locates/sell")
async def locate_sell(request: Request):
    b, body = broker(request), await json_body(request)
    require(body, "account", "symbol", "quantity", "locateType", "quoteReqID")
    if body["locateType"] not in LOCATE_TYPES:
        raise TZError(errors.text_400([
            '- locateType: locateType must be one of the following: "Unknown", "Locate", '
            '"IntraDay", "PreBorrow", "SingleUse"']))
    _locate_account(b, body["account"])
    ok = b.locate_sell(body["symbol"], _quantity(body), body["locateType"], str(body["quoteReqID"]))
    return {"locateSellbackSent": "true"} if ok else _dup()


@router.delete("/accounts/locates/cancel/accounts/{account_id}/quoteReqID/{quote_req_id}")
async def locate_cancel(request: Request, account_id: str, quote_req_id: str):
    b = broker(request)
    _locate_account(b, account_id)
    b.locate_cancel(quote_req_id)
    return {"locateCancelSent": "true"}


@router.get("/accounts/{account_id}/locates/history")
async def locate_history(request: Request, account_id: str):
    return {"locateHistory": account(request, account_id, "GetLocateHistory").locate_history()}


@router.get("/accounts/{account_id}/locates/inventory")
async def locate_inventory(request: Request, account_id: str):
    return {"locateInventory": account(request, account_id, "GetLocateInventory").locate_inventory()}
