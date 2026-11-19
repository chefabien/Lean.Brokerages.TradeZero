"""OHLCV bars loaded from CSV files (ticker,datetime,open,high,low,close,volume).

A bar timestamped `ts` describes what happened in the interval *ending* at `ts`:
it becomes visible once the virtual clock reaches `ts`.
"""

from __future__ import annotations

import bisect
import csv
import os
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

DATA_DIR = Path(os.environ.get("TZ_MOCK_DATA", Path(__file__).resolve().parents[1] / "data" / "bars"))


def parse_dt(value: str) -> datetime:
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        raise ValueError(f"datetime must carry a timezone: {value!r}")
    return dt


@dataclass(frozen=True)
class Bar:
    symbol: str
    ts: datetime
    open: float
    high: float
    low: float
    close: float
    volume: int


class MarketData:
    def __init__(self, bars: list[Bar]):
        self._by_symbol: dict[str, list[Bar]] = {}
        for bar in sorted(bars, key=lambda b: (b.ts, b.symbol)):
            self._by_symbol.setdefault(bar.symbol, []).append(bar)
        self._ts = {s: [b.ts for b in bs] for s, bs in self._by_symbol.items()}

    @classmethod
    def from_csv(cls, files: list[str | Path]) -> MarketData:
        bars = []
        for f in files:
            path = Path(f) if Path(f).is_absolute() else DATA_DIR / f
            with open(path, newline="") as fh:
                for row in csv.DictReader(fh):
                    bars.append(Bar(
                        symbol=row["ticker"], ts=parse_dt(row["datetime"]),
                        open=float(row["open"]), high=float(row["high"]),
                        low=float(row["low"]), close=float(row["close"]),
                        volume=int(float(row["volume"])),
                    ))
        return cls(bars)

    @property
    def symbols(self) -> list[str]:
        return sorted(self._by_symbol)

    def has(self, symbol: str) -> bool:
        return symbol in self._by_symbol

    def last_bar(self, symbol: str, now: datetime) -> Bar | None:
        ts = self._ts.get(symbol)
        if not ts:
            return None
        i = bisect.bisect_right(ts, now)
        return self._by_symbol[symbol][i - 1] if i else None

    def price(self, symbol: str, now: datetime) -> float | None:
        bar = self.last_bar(symbol, now)
        return bar.close if bar else None

    def bars_between(self, start: datetime, end: datetime) -> list[Bar]:
        """All bars with start < ts <= end, in time order."""
        out = []
        for symbol, ts in self._ts.items():
            lo, hi = bisect.bisect_right(ts, start), bisect.bisect_right(ts, end)
            out.extend(self._by_symbol[symbol][lo:hi])
        return sorted(out, key=lambda b: (b.ts, b.symbol))
