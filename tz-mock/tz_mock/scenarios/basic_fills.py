from datetime import UTC, datetime

from . import Scenario, register


@register
class BasicFills(Scenario):
    name = "basic_fills"
    description = ("AAPL and MSFT, 1-min bars over two regular sessions (2026-01-05/06). Starts at "
                   "09:31 ET. Market orders fill at once; limit/stop orders fill as time advances.")
    bars = ["AAPL_1min.csv", "MSFT_1min.csv"]
    start = datetime(2026, 1, 5, 14, 31, tzinfo=UTC)
