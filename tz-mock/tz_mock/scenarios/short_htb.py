from datetime import UTC, datetime

from . import Scenario, register


@register
class ShortHTB(Scenario):
    name = "short_htb"
    description = ("Short selling with locates (live behaviour). AAPL is easy to borrow; HTBX is hard "
                   "to borrow with locates offered at $0.03/share; NOLOC is hard to borrow with no "
                   "locate supply; SUBP trades under $0.50 (locates rejected with R40).")
    bars = ["AAPL_1min.csv", "HTBX_1min.csv", "NOLOC_1min.csv", "SUBP_1min.csv"]
    start = datetime(2026, 1, 5, 14, 31, tzinfo=UTC)
    htb = {"HTBX", "NOLOC", "SUBP"}
    locate_price = {"HTBX": 0.03, "SUBP": 0.01}
