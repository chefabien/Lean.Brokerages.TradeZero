from datetime import UTC, datetime

from . import Scenario, register


@register
class PartialFills(Scenario):
    name = "partial_fills"
    description = ("THIN: a low-volume symbol (100-500 shares per 1-min bar) where at most 10% of each "
                   "bar's volume can fill, so larger orders go PartiallyFilled across several bars.")
    bars = ["THIN_1min.csv"]
    start = datetime(2026, 1, 5, 14, 31, tzinfo=UTC)
    participation = 0.1
