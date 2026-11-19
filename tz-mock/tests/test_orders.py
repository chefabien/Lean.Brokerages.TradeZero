def test_post_returns_pending_new_then_new(tz):
    r = tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="L1")
    assert r.status_code == 200
    row = r.json()
    assert row["orderStatus"] == "PendingNew" and row["text"] is None and row["route"] == "PAPER"
    got = tz.get_order("L1")
    assert got["orderStatus"] == "New" and got["text"] == "TRAFIX_SIM"
    assert [o["clientOrderId"] for o in tz.get(f"/v1/api/accounts/{tz.acc}/orders").json()["orders"]] == ["L1"]


def test_generated_client_order_id(tz):
    cid = tz.order(symbol="AAPL").json()["clientOrderId"]
    assert cid.startswith("0105023100000.")  # MMDDhhmmssfff.seq from the virtual clock


def test_validation_text(tz):
    r = tz.order(securityType="stock", symbol="AA PL", side="SellShort", orderType="StopMarket",
                 orderQuantity=0)
    assert r.status_code == 400 and r.headers["content-type"].startswith("text/plain")
    assert r.text.splitlines() == [
        "- at '/symbol': 'AA PL' does not match pattern '^[a-zA-Z0-9._-]+$'",
        "- at '/orderQuantity': minimum: got 0, want 1×10⁻⁰⁶",
        "- at '/securityType': value must be one of 'Stock', 'Option', 'Mleg', 'Future', 'Prediction'",
        "- at '/orderType': value must be one of 'Limit', 'Market', 'Stop', 'StopLimit'",
        "- at '/side': value must be one of 'Buy', 'Sell'",
    ]
    r = tz.order(symbol="AAPL", orderQuantity=2_000_000)
    assert r.text == "- at '/orderQuantity': maximum: got 2×10⁰⁶, want 1×10⁰⁶"


def test_fractional_quantity(tz):
    r = tz.order(symbol="AAPL", orderQuantity=1.5)
    assert r.status_code == 400
    assert r.json()["detail"] == "ASLib Order quantity invalid, fractional shares not enabled."


def test_non_json_body_is_404(tz):
    r = tz.post(f"/v1/api/accounts/{tz.acc}/order", content=b"not json")
    assert r.status_code == 404 and r.text == "Not found"


def test_duplicate_client_order_id(tz):
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="D1")
    row = tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="D1").json()
    assert row["orderStatus"] == "Rejected" and row["text"] == "R114: Invalid duplicate UserOrderId"
    assert row["openClose"] == "Unknown" and row["route"] == "<no value>"
    assert tz.get_order("D1")["orderStatus"] == "New"  # original untouched


def test_sync_and_async_rejections(tz):
    neg = tz.order(symbol="AAPL", orderType="Limit", limitPrice=-1).json()
    assert neg["orderStatus"] == "Rejected" and neg["text"] == "R145: Negative Price Is Not Allowed"
    cases = {
        "R72: Limit orders cannot have a price of 0.00": dict(orderType="Limit"),
        "R24: Your order cannot have a STOP price of Zero": dict(orderType="Stop"),
        "R23: You can only use DAY expiration on Market orders": dict(timeInForce="GoodTillCancel"),
        "R100: Order would exceed max notional value": dict(orderType="Limit", limitPrice=500,
                                                            orderQuantity=100_000),
        "R146: Validation Order Error: Symbol Is Missing": dict(securityType="Option", orderType="Limit",
                                                                 limitPrice=1),
    }
    for text, fields in cases.items():
        row = tz.order(symbol="AAPL", **fields).json()
        assert row["orderStatus"] == "PendingNew"
        got = tz.get_order(row["clientOrderId"])
        assert (got["orderStatus"], got["text"]) == ("Rejected", text)
        assert got["canceledQuantity"] == got["orderQuantity"] and got["leavesQuantity"] == 0


def test_market_outside_hours(tz):
    tz.scenario("basic_fills", start="2026-01-05T13:00:00Z")  # 08:00 ET
    row = tz.order(symbol="AAPL").json()
    assert tz.get_order(row["clientOrderId"])["text"] == "R78: Market orders are not allowed at this time"
    row = tz.order(symbol="AAPL", orderType="Stop", stopPrice=300).json()
    assert tz.get_order(row["clientOrderId"])["text"] == "R79: Stop orders are not allowed at this time"
    row = tz.order(symbol="AAPL", orderType="Limit", limitPrice=1).json()
    assert tz.get_order(row["clientOrderId"])["orderStatus"] == "New"


def test_cancel(tz):
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="C1")
    row = tz.delete(f"/v1/api/accounts/{tz.acc}/orders/C1").json()
    assert row["orderStatus"] == "Canceled" and row["canceledQuantity"] == 10 and row["leavesQuantity"] == 0
    again = tz.delete(f"/v1/api/accounts/{tz.acc}/orders/C1").json()
    assert again["orderStatus"] == "Canceled" and again["text"].startswith("R130: Cancel Request Rejected")
    r = tz.delete(f"/v1/api/accounts/{tz.acc}/orders/unknown")
    assert r.status_code == 404 and r.text == "Not found"


def test_unknown_order(tz):
    r = tz.get(f"/v1/api/accounts/{tz.acc}/order/nope")
    assert r.status_code == 400 and r.json()["message"] == "GetOrder"


def test_cancel_all(tz):
    tz.order(symbol="AAPL", orderType="Limit", limitPrice=1, clientOrderId="A1")
    tz.order(symbol="MSFT", orderType="Limit", limitPrice=1, clientOrderId="M1")
    assert tz.delete("/v1/api/accounts/orders", json={"account": tz.acc}).status_code == 404
    r = tz.delete("/v1/api/accounts/orders?symbol=MSFT", data={"account": tz.acc})
    assert r.json() == {"message": "Cancel Request Submitted Successfully"}
    assert tz.get_order("M1")["orderStatus"] == "Canceled"
    assert tz.get_order("A1")["orderStatus"] == "New"
    tz.delete("/v1/api/accounts/orders", data={"account": tz.acc})
    assert tz.get_order("A1")["orderStatus"] == "Canceled"
    r = tz.delete("/v1/api/accounts/orders", data={"account": "WRONG"})
    assert r.status_code == 400


def test_static_endpoints(tz):
    a = tz.acc
    assert [r["routeName"] for r in tz.get(f"/v1/api/accounts/{a}/routes").json()["routes"]] == ["PAPER", "PAPERM"]
    assert tz.get(f"/v1/api/accounts/{a}/positions/historical").status_code == 400
    assert tz.get(f"/v1/api/accounts/{a}/positions/historical?date=2026-01-05").json() == {"historicalPositions": []}
    r = tz.get(f"/v1/api/accounts/{a}/orders/start-date/2026-01-01")
    assert r.status_code == 401
    assert tz.get(f"/v1/api/accounts/{a}/orders-with-pagination/start-date/2026-01-01").json()["tradingHistory"] == []
    assert tz.get(f"/v1/api/accounts/{a}/cash-transactions/start-date/2026-01-01").json()["cashHistory"] == []
