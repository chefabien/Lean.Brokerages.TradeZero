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
    locate_price: dict[str, float] = {}  # per-share locate offer; absent -> "No shares available"
    locate_offer_seconds: int = 30

    def __init__(self, **params: Any):
        for key, value in params.items():
            if not hasattr(type(self), key) or key.startswith("_"):
                raise ValueError(f"unknown scenario param: {key}")
            if key == "start" and isinstance(value, str):
                value = parse_dt(value)
            if key == "htb":
                value = set(value)
            setattr(self, key, value)

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
