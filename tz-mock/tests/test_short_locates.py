import pytest


def locate(tz, path, **body):
    return tz.post(f"/v1/api/accounts/locates/{path}", json=body)


def history(tz):
    return {r["quoteReqID"]: r for r in tz.get(f"/v1/api/accounts/{tz.acc}/locates/history").json()["locateHistory"]}


@pytest.fixture
def short(tz):
    tz.scenario("short_htb")
    return tz


def test_easy_to_borrow(short):
    etb = lambda s: short.get(f"/v1/api/accounts/{short.acc}/is-easy-to-borrow/symbol/{s}").json()  # noqa: E731
    assert etb("AAPL") == {"isEasyToBorrow": True}
    assert etb("HTBX") == {"isEasyToBorrow": False}


def test_short_etb_is_allowed(short):
    short.order(symbol="AAPL", side="Sell", orderQuantity=10, clientOrderId="S")
    got = short.get_order("S")
    assert got["orderStatus"] == "Filled" and got["side"] == "SellShort"
    [pos] = short.positions()
    assert pos["side"] == "Short" and pos["shares"] == -10.0


def test_htb_short_needs_locate(short):
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S1")
    assert short.get_order("S1")["orderStatus"] == "Rejected"

    assert locate(short, "quote", account=short.acc, symbol="HTBX", quantity=200, quoteReqID="q1").json() \
        == {"locateQuoteSent": "true"}
    row = history(short)["q1"]
    assert (row["locateStatus"], row["locatePrice"], row["text"]) == (65, 0.03, "")
    assert locate(short, "accept", accountId=short.acc, quoteReqID="q1").json() == {"locateAcceptSent": "true"}
    assert history(short)["q1"]["locateStatus"] == 50
    [inv] = short.get(f"/v1/api/accounts/{short.acc}/locates/inventory").json()["locateInventory"]
    assert inv["available"] == 200 and inv["symbol"] == "HTBX"
    assert short.get(f"/v1/api/account/{short.acc}").json()["totalLocateCosts"] == 6.0

    short.order(symbol="HTBX", side="Sell", orderQuantity=150, clientOrderId="S2")
    got = short.get_order("S2")
    assert got["orderStatus"] == "Filled" and got["side"] == "SellShort"
    [inv] = short.get(f"/v1/api/accounts/{short.acc}/locates/inventory").json()["locateInventory"]
    assert inv["available"] == 50
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S3")
    assert short.get_order("S3")["orderStatus"] == "Rejected"
    short.order(symbol="HTBX", side="Buy", openClose="Close", orderQuantity=150, clientOrderId="C")
    assert short.get_order("C")["side"] == "BuyToCover"


def test_locate_offer_expires(short):
    locate(short, "quote", account=short.acc, symbol="HTBX", quantity=100, quoteReqID="q1")
    short.advance("2026-01-05T14:31:30Z")
    row = history(short)["q1"]
    assert (row["locateStatus"], row["text"]) == (67, "Quote expired.")
    locate(short, "accept", accountId=short.acc, quoteReqID="q1")
    assert short.get(f"/v1/api/accounts/{short.acc}/locates/inventory").json()["locateInventory"] == []


def test_locate_rejections(short):
    for qid, sym in [("e", "AAPL"), ("n", "NOLOC"), ("p", "SUBP")]:
        locate(short, "quote", account=short.acc, symbol=sym, quantity=100, quoteReqID=qid)
    locate(short, "quote", account=short.acc, symbol="HTBX", quantity=100, quoteReqID="h1")
    locate(short, "quote", account=short.acc, symbol="HTBX", quantity=100, quoteReqID="h2")
    h = history(short)
    assert (h["e"]["locateStatus"], h["e"]["locateError"]) == (56, 56)
    assert h["n"]["text"] == "11 No shares available. Please try again later."
    assert h["p"]["locateError"] == 40
    assert h["h2"]["text"] == "14 Locate is already pending."
    r = locate(short, "quote", account=short.acc, symbol="HTBX", quantity=50, quoteReqID="x")
    assert r.status_code == 400 and r.json()["title"] == "One or more validation errors occurred."
    r = locate(short, "accept", quoteReqID="h1")
    assert r.status_code == 400 and r.text == "- (root): accountId is required"


def test_cancel_and_sell_back(short):
    locate(short, "quote", account=short.acc, symbol="HTBX", quantity=300, quoteReqID="q1")
    locate(short, "accept", accountId=short.acc, quoteReqID="q1")
    locate(short, "quote", account=short.acc, symbol="HTBX", quantity=100, quoteReqID="q2")
    short.delete(f"/v1/api/accounts/locates/cancel/accounts/{short.acc}/quoteReqID/q2")
    assert history(short)["q2"]["locateStatus"] == 52

    r = locate(short, "sell", account=short.acc, symbol="HTBX", quantity=200, locateType="Locate", quoteReqID="s1")
    assert r.json() == {"locateSellbackSent": "true"}
    assert history(short)["s1"]["locateStatus"] == 48
    [inv] = short.get(f"/v1/api/accounts/{short.acc}/locates/inventory").json()["locateInventory"]
    assert (inv["available"], inv["toBeSold"]) == (100, 200)
    short.advance("2026-01-05T14:32:00Z")
    assert history(short)["s1"]["locateStatus"] == 50
    [inv] = short.get(f"/v1/api/accounts/{short.acc}/locates/inventory").json()["locateInventory"]
    assert (inv["available"], inv["toBeSold"], inv["sold"]) == (100, 0, 200)
    locate(short, "sell", account=short.acc, symbol="HTBX", quantity=500, locateType="Locate", quoteReqID="s2")
    assert history(short)["s2"]["text"] == "16 Insufficient shares."
