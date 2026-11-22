"""Scenario registry.

A scenario is a plain Python class: market data (CSV files), a start time, account settings
and optional hooks. To add one, drop a module in this package with a `@register` subclass:

    @register
    class MyScenario(Scenario):
        name = "my_scenario"
        description = "..."
        bars = ["AAPL_2026-01-05.csv"]
        start = datetime(2026, 1, 5, 14, 31, tzinfo=UTC)

Any class attribute can be overridden per run with `POST /mock/scenario {"name":..., "params":{...}}`.
"""

from __future__ import annotations

import importlib
import pkgutil
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any

from ..marketdata import Bar, MarketData, parse_dt

if TYPE_CHECKING:
    from ..broker import Broker
    from ..models import Order

REGISTRY: dict[str, type[Scenario]] = {}
DEFAULT = "basic_fills"


class Scenario:
    name: str = ""
    description: str = ""
    bars: list[str] = []
    start: datetime = datetime(2026, 1, 5, 14, 31, tzinfo=UTC)
    cash: float = 1_000_000.0
    leverage: float = 1.0
    participation: float = 1.0  # max fraction of a bar's volume that can fill
    htb: set[str] = set()  # hard-to-borrow symbols (everything else is easy to borrow)
    # Reg SHO threshold symbols: hard to borrow, and a quote gives two rows, PreBorrow (base id, locateType 3)
    # and SingleUse (id + ".SU", locateType 4)
    regsho: set[str] = set()
    # per-share locate offer. A float prices every type; a dict prices types by name
    # ("Locate", "PreBorrow", "SingleUse") and a missing type is refused (54/11). No entry -> "No shares available"
    locate_price: dict[str, float | dict[str, float]] = {}
    locate_offer_seconds: int = 30
    locate_quote_polls: int = 0  # history reads a new quote row shows 81 (Quoting) before its outcome
    locate_sibling_polls: int = 0  # history reads before the ".SU" row of a Reg SHO quote appears
    locate_supply: dict[str, int] = {}  # shares that can still be located today; absent -> unlimited
    # inventory at the start: shares of the ordinary type, or {"PreBorrow": 200, "SingleUse": 100}
    locate_inventory: dict[str, int | dict[str, int]] = {}
    locate_accept_ignored: set[str] = set()  # accepts for these symbols are acknowledged but never applied
    locate_sellback_credit: float = 1.0  # fraction of the locate price credited back on a sell-back
    locate_pool_code: dict[str, int] = {}  # locateType of ordinary locates: 1 (default) or 2
    locate_use_order: list[int] = [1, 2, 3, 4]  # which inventory a short uses first, by locateType

    def __init__(self, **params: Any):
        for key, value in params.items():
            if not hasattr(type(self), key) or key.startswith("_"):
                raise ValueError(f"unknown scenario param: {key}")
            if key == "start" and isinstance(value, str):
                value = parse_dt(value)
            if isinstance(getattr(type(self), key), set):
                value = set(value)
            setattr(self, key, value)

    def hard_to_borrow(self, symbol: str) -> bool:
        return symbol in self.htb or symbol in self.regsho

    def market_data(self) -> MarketData:
        return MarketData.from_csv(self.bars)

    # --- hooks -----------------------------------------------------------------------------
    def on_order(self, broker: Broker, order: Order) -> str | None:
        """Return a rejection text to reject `order` (after the built-in checks)."""
        return None

    def on_bar(self, broker: Broker, bar: Bar) -> None:
        """Called for every bar as the clock moves forward, after orders are matched."""


def register(cls: type[Scenario]) -> type[Scenario]:
    REGISTRY[cls.name] = cls
    return cls


def load_all() -> None:
    for mod in pkgutil.iter_modules(__path__):
        importlib.import_module(f"{__name__}.{mod.name}")


def create(name: str, params: dict | None = None) -> Scenario:
    load_all()
    return REGISTRY[name](**(params or {}))
