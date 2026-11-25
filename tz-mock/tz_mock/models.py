"""In-memory records and their TradeZero JSON shapes (see docs/api-notes.md)."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime

# DoneForDay: a GTC order parked between sessions; it comes back as New (see docs/api-notes.md)
WORKING = {"PendingNew", "New", "PartiallyFilled", "PendingCancel", "Accepted", "DoneForDay"}
TERMINAL = {"Filled", "Canceled", "Rejected", "Expired"}


def ts(dt: datetime) -> str:
    """TZ timestamp format: UTC, 7 fractional digits, trailing Z."""
    return f"{dt:%Y-%m-%dT%H:%M:%S}.{dt.microsecond:06d}0Z"


def num(x: float) -> int | float:
    """Order rows carry whole numbers as JSON integers (`"limitPrice":1`)."""
    x = round(float(x), 4)
    return int(x) if x.is_integer() else x


def money(x: float) -> float:
    return round(float(x) + 0.0, 2)


@dataclass
class Order:
    account_id: str
    client_order_id: str
    symbol: str
    security_type: str
    side: str  # Buy | Sell (as sent)
    open_close: str
    order_type: str
    quantity: int
    time_in_force: str
    limit_price: float
    stop_price: float
    route: str
    start_time: datetime
    status: str = "PendingNew"
    text: str | None = None
    executed: int = 0
    canceled: int = 0
    avg_price: float = 0.0
    last_price: float = 0.0
    last_qty: int = 0
    last_updated: datetime | None = None
    triggered: bool = False
    short_qty: int = 0  # part of the order that opens/increases a short
    locate_reserved: dict[int, int] = field(default_factory=dict)  # locateType -> HTB locate shares reserved
    enriched_side: str | None = None  # SellShort / BuyToCover, shown on reads

    @property
    def leaves(self) -> int:
        return 0 if self.status in TERMINAL else self.quantity - self.executed - self.canceled

    @property
    def working(self) -> bool:
        return self.status in WORKING

    def to_json(self, enrich: bool = True) -> dict:
        d = {
            "accountId": self.account_id,
            "canceledQuantity": num(self.canceled),
            "clientOrderId": self.client_order_id,
            "executed": num(self.executed),
            "lastPrice": num(self.last_price),
            "lastQuantity": num(self.last_qty),
            "lastUpdated": ts(self.last_updated or self.start_time),
            "leavesQuantity": num(self.leaves),
            "legCount": 0,
            "legs": None,
            "limitPrice": num(self.limit_price),
            "maintenanceRequirement": 0,
            "marginRequirement": 0,
            "maxDisplayQuantity": 0,
            "openClose": self.open_close,
            "orderQuantity": num(self.quantity),
            "orderStatus": self.status,
            "orderType": self.order_type,
            "pegDifference": 0,
            "pegOffsetType": "Price",
            "priceAvg": num(self.avg_price),
            "priceStop": num(self.stop_price),
            "route": self.route,
            "securityType": self.security_type,
            "side": (self.enriched_side or self.side) if enrich else self.side,
            "startTime": ts(self.start_time),
            "strikePrice": 0,
            "symbol": self.symbol,
            "text": self.text,
            "timeInForce": self.time_in_force,
            "tradedSymbol": self.symbol,
        }
        return d


@dataclass
class Position:
    account_id: str
    position_id: str
    symbol: str
    side: str  # Long | Short
    created: datetime
    updated: datetime
    shares: float = 0.0  # signed
    price_avg: float = 0.0
    price_open: float = 0.0
    price_close: float = 0.0
    day_overnight: str = "Day"
    shares_in: float = 0.0
    shares_out: float = 0.0
    realized: float = 0.0
    day_realized: float = 0.0
    sod_price: float | None = None  # reference price for day P&L on overnight positions

    def _base(self) -> dict:
        return {
            "accountId": self.account_id,
            "createdDate": ts(self.created),
            "dayOvernight": self.day_overnight,
            "initialRequirement": 0.0,
            "maintenanceRequirement": 0.0,
            "marginRequirement": 0.0,
            "positionId": self.position_id,
            "priceAvg": round(self.price_avg, 4),
            "priceClose": round(self.price_close, 4),
            "priceOpen": round(self.price_open, 4),
            "priceStrike": 0.0,
            "putCall": "None",
            "rootSymbol": None,
            "securityType": "Stock",
            "shares": float(self.shares),
            "side": self.side,
            "symbol": self.symbol,
            "tradedSymbol": None,
            "updatedDate": ts(self.updated),
        }

    def to_json(self) -> dict:
        return self._base()

    def to_closed_json(self) -> dict:
        d = self._base()
        d["positionId"] = int(self.position_id)  # a JSON number on /positions/closed
        d["realized"] = money(self.realized)
        d["sharesIn"] = float(self.shares_in)
        d["sharesOut"] = float(self.shares_out)
        return dict(sorted(d.items()))


@dataclass
class LocateRow:
    account_id: str
    symbol: str
    quote_req_id: str
    locate_shares: int
    status: int
    created: datetime
    updated: datetime
    locate_type: int = 1
    price: float = 0.0
    filled_shares: int = 0
    error: int = 0
    text: str | None = None
    offered_at: datetime | None = None
    quoting_reads: int = 0  # history reads still showing 81 (Quoting); `status` is the outcome after that
    hidden_reads: int = 0  # history reads before the row appears at all (a late ".SU" sibling)

    @property
    def quoting(self) -> bool:
        return self.quoting_reads > 0 or self.hidden_reads > 0

    def to_json(self) -> dict:
        quoting = self.quoting_reads > 0
        return {
            "accountId": self.account_id,
            "createdDate": ts(self.created),
            "filledShares": self.filled_shares,
            "locateError": 0 if quoting else self.error,
            "locatePrice": 0.0 if quoting else self.price,
            "locateShares": self.locate_shares,
            "locateStatus": 81 if quoting else self.status,
            "locateType": self.locate_type,
            "preBorrow": self.locate_type == 3,
            "quoteReqID": self.quote_req_id,
            "symbol": self.symbol,
            "text": "" if quoting else self.text,
            "updatedDate": ts(self.updated),
        }


@dataclass
class Inventory:
    account_id: str
    symbol: str
    locate_type: int = 1
    available: int = 0
    sold: int = 0
    to_be_sold: int = 0
    unavailable: int = 0
    price: float = 0.0  # per-share cost of the last accepted locate (refunded on sell-back)
    used: bool = False  # a share was used to open a short (SingleUse can then no longer be sold back)

    def to_json(self) -> dict:
        return {
            "accountId": self.account_id,
            "available": self.available,
            "locateType": self.locate_type,
            "sold": self.sold,
            "symbol": self.symbol,
            "toBeSold": self.to_be_sold,
            "unavailable": self.unavailable,
        }


@dataclass
class Fill:
    client_order_id: str
    symbol: str
    side: str
    qty: int
    price: float
    time: datetime

    def to_json(self) -> dict:
        return {"clientOrderId": self.client_order_id, "symbol": self.symbol, "side": self.side,
                "qty": self.qty, "price": self.price, "time": ts(self.time)}


@dataclass
class PendingSell:
    """A locate sell-back waiting for the next clock tick to settle."""
    row: LocateRow
    inventory: Inventory
    qty: int
