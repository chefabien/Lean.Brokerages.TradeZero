"""Generate the deterministic 1-minute OHLCV CSVs under data/bars.

    python scripts/make_bars.py

Each file: ticker,datetime,open,high,low,close,volume — datetime is the bar *end* time in UTC.
Regular session 09:30-16:00 ET (2026-01-05/06 are EST, UTC-5): bars stamped 14:31Z .. 21:00Z.
"""

import csv
import random
from datetime import UTC, datetime, timedelta
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "data" / "bars"
DAYS = [datetime(2026, 1, 5, 14, 30, tzinfo=UTC), datetime(2026, 1, 6, 14, 30, tzinfo=UTC)]

# ticker: (start price, per-minute volatility, volume range, days)
SPECS = {
    "AAPL": (250.0, 0.0008, (20_000, 120_000), 2),
    "MSFT": (420.0, 0.0007, (10_000, 80_000), 2),
    "THIN": (12.0, 0.0015, (100, 500), 1),
    "HTBX": (8.0, 0.002, (5_000, 20_000), 1),
    "NOLOC": (3.0, 0.002, (5_000, 20_000), 1),
    "SUBP": (0.40, 0.002, (50_000, 200_000), 1),
}


def bars(ticker: str, price: float, vol: float, vrange: tuple[int, int], days: int):
    rng = random.Random(ticker)
    for day in DAYS[:days]:
        for minute in range(1, 391):
            o = price
            path = [o * (1 + rng.gauss(0, vol)) for _ in range(4)]
            c = path[-1]
            h, lo = max(o, *path), min(o, *path)
            yield [ticker, (day + timedelta(minutes=minute)).strftime("%Y-%m-%dT%H:%M:%SZ"),
                   f"{o:.2f}", f"{h:.2f}", f"{lo:.2f}", f"{c:.2f}", rng.randint(*vrange)]
            price = round(c, 2)
        price = round(price * (1 + rng.gauss(0, vol * 10)), 2)  # overnight gap


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for ticker, (price, vol, vrange, days) in SPECS.items():
        path = OUT / f"{ticker}_1min.csv"
        with open(path, "w", newline="") as fh:
            w = csv.writer(fh)
            w.writerow(["ticker", "datetime", "open", "high", "low", "close", "volume"])
            w.writerows(bars(ticker, price, vol, vrange, days))
        print("wrote", path)


if __name__ == "__main__":
    main()
