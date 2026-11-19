from datetime import UTC, datetime

import pytest


def bar_after(tz, symbol, pred):
    """First bar after `now` matching pred(bar)."""
    b = tz.broker()
    for bar in b.md.bars_between(b.now, datetime(2100, 1, 1, tzinfo=UTC)):
        if bar.symbol == symbol and pred(bar):
            return bar
    raise AssertionError("no such bar")


def test_market_fills_at_current_price(tz):
    price = tz.broker().price("AAPL")
    row = tz.order(symbol="AAPL", clientOrderId="M").json()
    assert row["orderStatus"] == "PendingNew"
    got = tz.get_order("M")
    assert got["orderStatus"] == "Filled" and got["priceAvg"] == price and got["executed"] == 10
    [pos] = tz.positions()
    assert pos["shares"] == 10.0 and pos["side"] == "Long" and pos["priceAvg"] == price
    assert tz.pnl()["availableCash"] == pytest.approx(1_000_000 - 10 * price)


def test_limit_rests_then_fills_on_advance(tz):
    price = tz.broker().price("AAPL")
    limit = round(price - 1.0, 2)
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=limit, clientOrderId="L")
    assert tz.get_order("L")["orderStatus"] == "New"
    bar = bar_after(tz, "AAPL", lambda b: b.low <= limit)
    out = tz.advance(bar.ts.isoformat())
    assert [f["clientOrderId"] for f in out["fills"]] == ["L"]
    got = tz.get_order("L")
    assert got["orderStatus"] == "Filled" and got["priceAvg"] == min(limit, bar.open)


def test_marketable_limit_fills_now(tz):
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=10_000, orderQuantity=1, clientOrderId="L")
    assert tz.get_order("L")["priceAvg"] == tz.broker().price("AAPL")


def test_sell_stop_triggers(tz):
    tz.order(symbol="AAPL", orderQuantity=10)
    price = tz.broker().price("AAPL")
    stop = round(price - 1.0, 2)
    tz.order(symbol="AAPL", side="Sell", openClose="Close", orderType="Stop", stopPrice=stop, clientOrderId="S")
    bar = bar_after(tz, "AAPL", lambda b: b.low <= stop)
    tz.advance(bar.ts.isoformat())
    got = tz.get_order("S")
    assert got["orderStatus"] == "Filled" and got["priceAvg"] == min(stop, bar.open)
    [pos] = tz.positions()  # flat position stays listed, like paper
    assert pos["shares"] == 0.0 and pos["priceClose"] == got["priceAvg"]
    [closed] = tz.get(f"/v1/api/accounts/{tz.acc}/positions/closed").json()["closedPositions"]
    assert isinstance(closed["positionId"], int)
    assert closed["realized"] == pytest.approx(round((got["priceAvg"] - pos["priceAvg"]) * 10, 2))
    assert closed["sharesIn"] == closed["sharesOut"] == 10.0


def test_round_trip_pnl(tz):
    tz.order(symbol="MSFT", orderQuantity=5, clientOrderId="B")
    tz.advance("2026-01-05T15:00:00Z")
    tz.order(symbol="MSFT", side="Sell", openClose="Close", orderQuantity=5, clientOrderId="S")
    buy, sell = tz.get_order("B")["priceAvg"], tz.get_order("S")["priceAvg"]
    pnl = tz.pnl()
    assert pnl["dayRealized"] == pytest.approx(round((sell - buy) * 5, 2))
    assert pnl["sharesTraded"] == 10.0 and pnl["exposure"] == 0
    assert pnl["pnl"][0]["exposure"] == 0


def test_day_order_expires_and_day_rolls(tz):
    tz.order(symbol="AAPL", orderQuantity=3)
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="DAY")
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, timeInForce="GoodTillCancel", clientOrderId="GTC")
    tz.advance("2026-01-05T21:30:00Z")  # after the close
    assert tz.get_order("DAY")["orderStatus"] == "Expired"
    assert tz.get_order("GTC")["orderStatus"] == "New"
    tz.advance("2026-01-06T14:35:00Z")  # next morning
    orders = tz.get(f"/v1/api/accounts/{tz.acc}/orders").json()["orders"]
    assert [o["clientOrderId"] for o in orders] == ["GTC"]
    [pos] = tz.positions()
    assert pos["dayOvernight"] == "Overnight"
    acct = tz.get(f"/v1/api/account/{tz.acc}").json()
    assert acct["sodEquity"] == pytest.approx(tz.pnl()["accountValue"] - tz.pnl()["dayUnrealized"], abs=0.02)


def test_ioc_cancels_remainder(tz):
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, timeInForce="ImmediateOrCancel", clientOrderId="I")
    got = tz.get_order("I")
    assert got["orderStatus"] == "Canceled" and got["executed"] == 0
