def test_partial_fills_across_bars_then_cancel(tz):
    tz.scenario("partial_fills")
    b = tz.broker()
    first = b.md.last_bar("THIN", b.now)
    tz.order(symbol="THIN", orderQuantity=200, clientOrderId="P")
    got = tz.get_order("P")
    assert got["orderStatus"] == "PartiallyFilled"
    assert got["executed"] == int(first.volume * 0.1)
    tz.advance("2026-01-05T14:33:00Z")  # two more bars
    got = tz.get_order("P")
    assert got["orderStatus"] == "PartiallyFilled" and 0 < got["executed"] < 200
    assert tz.positions()[0]["shares"] == got["executed"]
    row = tz.delete(f"/v1/api/accounts/{tz.acc}/orders/P").json()
    assert row["orderStatus"] == "Canceled"
    assert row["canceledQuantity"] == 200 - row["executed"] and row["leavesQuantity"] == 0


def test_partial_eventually_fills(tz):
    tz.scenario("partial_fills")
    tz.order(symbol="THIN", orderQuantity=200, clientOrderId="P")
    tz.advance("2026-01-05T15:30:00Z")
    got = tz.get_order("P")
    assert got["orderStatus"] == "Filled" and got["executed"] == 200
