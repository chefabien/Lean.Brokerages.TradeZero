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


# ---------------------------------------------------------------------------- locate types, Reg SHO, mock controls
def inventory(tz):
    rows = tz.get(f"/v1/api/accounts/{tz.acc}/locates/inventory").json()["locateInventory"]
    return {(r["symbol"], r["locateType"]): r for r in rows}


def history_rows(tz):
    return tz.get(f"/v1/api/accounts/{tz.acc}/locates/history").json()["locateHistory"]


def quote(tz, symbol, qty, qid):
    return locate(tz, "quote", account=tz.acc, symbol=symbol, quantity=qty, quoteReqID=qid)


def accept(tz, qid):
    return locate(tz, "accept", accountId=tz.acc, quoteReqID=qid)


def sell(tz, symbol, qty, qid, locate_type="Locate"):
    return locate(tz, "sell", account=tz.acc, symbol=symbol, quantity=qty, locateType=locate_type, quoteReqID=qid)


def mock(tz, endpoint, **body):
    r = tz.post(f"/mock/{endpoint}", json=body)
    assert r.status_code == 200, r.text
    return r.json()


def test_quote_shows_quoting_for_n_reads(tz):
    tz.scenario("short_htb", locate_quote_polls=2)
    quote(tz, "HTBX", 100, "q1")
    for i in range(2):
        [row] = history_rows(tz)
        assert (row["locateStatus"], row["locatePrice"], row["locateError"], row["text"]) == (81, 0.0, 0, "")
        if i == 0:
            accept(tz, "q1")  # sent while still quoting: ignored
    [row] = history_rows(tz)
    assert (row["locateStatus"], row["locatePrice"]) == (65, 0.03)
    assert inventory(tz) == {}
    accept(tz, "q1")
    assert history(tz)["q1"]["locateStatus"] == 50


def test_quoting_counts_as_pending(tz):
    tz.scenario("short_htb", locate_quote_polls=1)
    quote(tz, "HTBX", 100, "q1")
    quote(tz, "HTBX", 100, "q2")
    assert history(tz)["q2"]["locateStatus"] == 81
    assert history(tz)["q2"]["text"] == "14 Locate is already pending."


def test_limited_supply(tz):
    tz.scenario("short_htb", locate_supply={"HTBX": 300})
    quote(tz, "HTBX", 400, "big")
    assert (history(tz)["big"]["locateStatus"], history(tz)["big"]["locateError"]) == (56, 11)
    quote(tz, "HTBX", 200, "q1")
    accept(tz, "q1")
    quote(tz, "HTBX", 200, "q2")
    assert history(tz)["q2"]["locateError"] == 11
    quote(tz, "HTBX", 100, "q3")
    assert history(tz)["q3"]["locateStatus"] == 65


def test_quote_not_in_lots_of_100(short):
    assert quote(short, "HTBX", 150, "q1").json() == {"locateQuoteSent": "true"}
    row = history(short)["q1"]
    assert (row["locateStatus"], row["locateError"], row["locateType"]) == (56, 67, 0)
    assert row["text"] == "R67: You can only offer for credit in lots of 100"


def test_refused_quote_has_no_pool(short):
    quote(short, "NOLOC", 100, "q1")
    assert history(short)["q1"]["locateType"] == 0


def test_locate_shares_are_reused_after_cover(short):
    quote(short, "HTBX", 200, "q1")
    accept(short, "q1")
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S1")
    inv = inventory(short)[("HTBX", 1)]
    assert (inv["available"], inv["unavailable"]) == (100, 100)
    short.order(symbol="HTBX", side="Buy", openClose="Close", orderQuantity=100, clientOrderId="C1")
    inv = inventory(short)[("HTBX", 1)]
    assert (inv["available"], inv["unavailable"]) == (200, 0)
    short.order(symbol="HTBX", side="Sell", orderQuantity=200, clientOrderId="S2")
    assert short.get_order("S2")["orderStatus"] == "Filled"


def test_single_use_is_used_up_by_the_first_short(short):
    quote(short, "REGSHO", 100, "q1")
    accept(short, "q1.SU")
    short.order(symbol="REGSHO", side="Sell", orderQuantity=100, clientOrderId="S1")
    inv = inventory(short)[("REGSHO", 4)]
    assert (inv["available"], inv["unavailable"]) == (0, 100)
    short.order(symbol="REGSHO", side="Buy", openClose="Close", orderQuantity=100, clientOrderId="C1")
    inv = inventory(short)[("REGSHO", 4)]
    assert (inv["available"], inv["unavailable"]) == (0, 0)
    short.order(symbol="REGSHO", side="Sell", orderQuantity=100, clientOrderId="S2")
    assert short.get_order("S2")["orderStatus"] == "Rejected"


def test_locates_are_cleared_at_the_day_roll(short):
    quote(short, "HTBX", 200, "q1")
    accept(short, "q1")
    quote(short, "REGSHO", 100, "q2")
    short.advance("2026-01-06T14:31:00Z")
    assert inventory(short) == {}
    assert history_rows(short) == []
    assert quote(short, "HTBX", 200, "q1").json() == {"locateQuoteSent": "true"}


def test_seeded_inventory(tz):
    tz.scenario("short_htb", locate_inventory={"HTBX": 200, "REGSHO": {"PreBorrow": 100, "SingleUse": 100}})
    inv = inventory(tz)
    assert {k: v["available"] for k, v in inv.items()} == {("HTBX", 1): 200, ("REGSHO", 3): 100, ("REGSHO", 4): 100}
    tz.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S1")
    assert tz.get_order("S1")["orderStatus"] == "Filled"
    assert tz.get(f"/v1/api/account/{tz.acc}").json()["totalLocateCosts"] == 0.0


def test_set_inventory_endpoint(short):
    assert mock(short, "locates/inventory", symbol="HTBX", available=300)["available"] == 300
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S1")
    assert short.get_order("S1")["orderStatus"] == "Filled"
    mock(short, "locates/inventory", symbol="HTBX", available=0)  # someone else used the shares
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S2")
    assert short.get_order("S2")["orderStatus"] == "Rejected"
    r = short.post("/mock/locates/inventory", json={"symbol": "HTBX", "available": 1, "locateType": "Nope"})
    assert r.status_code == 400


def test_next_error_injection(short):
    mock(short, "locates/next-error", symbol="HTBX", code=56, text="R56: This symbol is Easy to Borrow.")
    quote(short, "HTBX", 100, "q1")
    row = history(short)["q1"]
    assert (row["locateStatus"], row["locateError"], row["locateType"]) == (56, 56, 0)
    quote(short, "HTBX", 100, "q2")
    assert history(short)["q2"]["locateStatus"] == 65


def test_accept_ignored(tz):
    tz.scenario("short_htb", locate_accept_ignored=["HTBX"])
    quote(tz, "HTBX", 100, "q1")
    assert accept(tz, "q1").json() == {"locateAcceptSent": "true"}
    assert history(tz)["q1"]["locateStatus"] == 65
    assert inventory(tz) == {}
    tz.advance("2026-01-05T14:31:30Z")
    assert history(tz)["q1"]["locateStatus"] == 67


def test_sell_back_credit_ratio(tz):
    tz.scenario("short_htb", locate_sellback_credit=0.5)
    quote(tz, "HTBX", 200, "q1")
    accept(tz, "q1")
    sell(tz, "HTBX", 200, "s1")
    tz.advance("2026-01-05T14:32:00Z")
    assert tz.get(f"/v1/api/account/{tz.acc}").json()["availableCash"] == 1_000_000 - 6.0 + 3.0


def test_fault_before_is_not_processed(short):
    quote(short, "HTBX", 100, "q1")
    mock(short, "faults", method="POST", path="/v1/api/accounts/locates/accept", status=500)
    r = accept(short, "q1")
    assert (r.status_code, r.text) == (500, "")
    assert history(short)["q1"]["locateStatus"] == 65
    assert accept(short, "q1").status_code == 200  # one-shot
    assert history(short)["q1"]["locateStatus"] == 50


def test_fault_after_is_processed(short):
    quote(short, "HTBX", 100, "q1")
    mock(short, "faults", method="POST", path="*/locates/accept", status=502, when="after", body="Bad Gateway")
    r = accept(short, "q1")
    assert (r.status_code, r.text) == (502, "Bad Gateway")
    assert history(short)["q1"]["locateStatus"] == 50


def test_fault_list_count_and_clear(short):
    mock(short, "faults", method="GET", path="*/locates/history", status=429, count=2)
    assert short.get("/mock/faults").json()["faults"][0]["count"] == 2
    assert short.get(f"/v1/api/accounts/{short.acc}/locates/history").status_code == 429
    assert short.get("/mock/faults").json()["faults"][0]["count"] == 1
    assert short.get("/mock/state").status_code == 200  # /mock is never faulted
    assert short.delete("/mock/faults").json() == {"faults": []}
    assert short.get(f"/v1/api/accounts/{short.acc}/locates/history").status_code == 200
    r = short.post("/mock/faults", json={"method": "GET", "path": "*", "when": "never"})
    assert r.status_code == 400


def test_state_shows_locates(short):
    quote(short, "HTBX", 200, "q1")
    accept(short, "q1")
    short.order(symbol="HTBX", side="Sell", orderQuantity=100, clientOrderId="S1")
    quote(short, "REGSHO", 100, "q2")
    loc = short.get("/mock/state").json()["locates"]
    assert loc["openQuotes"] == ["q2", "q2.SU"]
    assert loc["inUse"] == {"HTBX": [[1, 100]]}
    assert loc["locateCosts"] == 6.0
    assert loc["inventory"][0]["used"] is True


def test_reg_sho_quote_offers_pre_borrow_and_single_use(short):
    quote(short, "REGSHO", 200, "q1")
    h = history(short)
    assert set(h) == {"q1", "q1.SU"}
    base, su = h["q1"], h["q1.SU"]
    assert (base["locateStatus"], base["locateType"], base["locatePrice"], base["preBorrow"]) == (65, 3, 0.03, True)
    assert (su["locateStatus"], su["locateType"], su["locatePrice"], su["preBorrow"]) == (65, 4, 0.01, False)
    assert base["locateShares"] == su["locateShares"] == 200


def test_reg_sho_accept_single_use_expires_pre_borrow(short):
    quote(short, "REGSHO", 200, "q1")
    accept(short, "q1.SU")
    h = history(short)
    assert (h["q1.SU"]["locateStatus"], h["q1.SU"]["filledShares"]) == (50, 200)
    assert (h["q1"]["locateStatus"], h["q1"]["text"]) == (67, "Quote expired.")
    assert inventory(short)[("REGSHO", 4)]["available"] == 200
    assert ("REGSHO", 3) not in inventory(short)
    assert short.get(f"/v1/api/account/{short.acc}").json()["totalLocateCosts"] == 2.0


def test_reg_sho_accept_pre_borrow_expires_single_use(short):
    quote(short, "REGSHO", 100, "q1")
    accept(short, "q1")
    h = history(short)
    assert (h["q1"]["locateStatus"], h["q1.SU"]["locateStatus"]) == (50, 67)
    assert inventory(short)[("REGSHO", 3)]["available"] == 100


def test_reg_sho_one_side_refused(tz):
    tz.scenario("short_htb", locate_price={"REGSHO": {"SingleUse": 0.01}})
    quote(tz, "REGSHO", 100, "q1")
    h = history(tz)
    assert (h["q1"]["locateStatus"], h["q1"]["locateError"], h["q1"]["locateType"]) == (54, 11, 3)
    assert h["q1.SU"]["locateStatus"] == 65


def test_reg_sho_late_sibling(tz):
    tz.scenario("short_htb", locate_sibling_polls=1)
    quote(tz, "REGSHO", 100, "q1")
    assert [r["quoteReqID"] for r in history_rows(tz)] == ["q1"]
    assert [r["quoteReqID"] for r in history_rows(tz)] == ["q1", "q1.SU"]


def test_reg_sho_cancel_is_per_row(short):
    quote(short, "REGSHO", 100, "q1")
    short.delete(f"/v1/api/accounts/locates/cancel/accounts/{short.acc}/quoteReqID/q1")
    h = history(short)
    assert (h["q1"]["locateStatus"], h["q1.SU"]["locateStatus"]) == (52, 65)


def test_reg_sho_duplicate_ids(short):
    quote(short, "REGSHO", 100, "q1")
    assert quote(short, "HTBX", 100, "q1.SU").status_code == 400
    assert quote(short, "HTBX", 100, "q1").status_code == 400


def test_single_use_sell_back_only_if_never_used(tz):
    tz.scenario("short_htb", locate_inventory={"REGSHO": {"SingleUse": 300, "PreBorrow": 200}},
                locate_use_order=[4, 3])
    tz.order(symbol="REGSHO", side="Sell", orderQuantity=100, clientOrderId="S1")
    tz.order(symbol="REGSHO", side="Buy", openClose="Close", orderQuantity=100, clientOrderId="C1")
    sell(tz, "REGSHO", 200, "s1", "SingleUse")
    row = history(tz)["s1"]
    assert (row["locateStatus"], row["locateError"], row["locateType"]) == (56, 16, 4)
    assert row["text"].startswith("MOCK: SingleUse")
    sell(tz, "REGSHO", 200, "s2", "PreBorrow")
    assert history(tz)["s2"]["locateStatus"] == 48


def test_unused_single_use_can_be_sold_back(tz):
    tz.scenario("short_htb", locate_inventory={"REGSHO": {"SingleUse": 200}})
    sell(tz, "REGSHO", 200, "s1", "SingleUse")
    assert history(tz)["s1"]["locateStatus"] == 48
    assert inventory(tz)[("REGSHO", 4)]["toBeSold"] == 200


def test_sell_back_wrong_type(tz):
    tz.scenario("short_htb", locate_inventory={"REGSHO": {"PreBorrow": 200}})
    sell(tz, "REGSHO", 100, "s1", "SingleUse")
    assert history(tz)["s1"]["text"] == "16 Insufficient shares."


def test_pool_code_2(tz):
    tz.scenario("short_htb", locate_pool_code={"HTBX": 2})
    quote(tz, "HTBX", 200, "q1")
    assert history(tz)["q1"]["locateType"] == 2
    accept(tz, "q1")
    assert inventory(tz)[("HTBX", 2)]["available"] == 200
    sell(tz, "HTBX", 100, "s1", "Locate")
    assert history(tz)["s1"]["locateStatus"] == 48


@pytest.mark.parametrize("order,expected", [([1, 2, 3, 4], (0, 50)), ([4, 3], (50, 0))])
def test_inventory_use_order(tz, order, expected):
    tz.scenario("short_htb", locate_inventory={"REGSHO": {"PreBorrow": 100, "SingleUse": 100}},
                locate_use_order=order)
    tz.order(symbol="REGSHO", side="Sell", orderQuantity=150, clientOrderId="S1")
    inv = inventory(tz)
    assert (inv[("REGSHO", 3)]["available"], inv[("REGSHO", 4)]["available"]) == expected


def test_cancel_releases_reserved_locates_of_every_type(tz):
    tz.scenario("short_htb", locate_inventory={"REGSHO": {"PreBorrow": 100, "SingleUse": 100}})
    tz.order(symbol="REGSHO", side="Sell", orderType="Limit", limitPrice=100, orderQuantity=150, clientOrderId="L1")
    inv = inventory(tz)
    assert inv[("REGSHO", 3)]["available"] + inv[("REGSHO", 4)]["available"] == 50
    tz.delete(f"/v1/api/accounts/{tz.acc}/orders/L1")
    inv = inventory(tz)
    assert (inv[("REGSHO", 3)]["available"], inv[("REGSHO", 4)]["available"]) == (100, 100)
