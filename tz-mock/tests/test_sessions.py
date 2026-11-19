from conftest import TZ


def test_keys_are_isolated(client):
    a, b = TZ(client, "alice"), TZ(client, "bob")
    assert a.acc != b.acc and a.acc.startswith("TZP")
    assert a.order(symbol="AAPL", clientOrderId="x1").status_code == 200
    assert len(a.positions()) == 1
    assert b.positions() == []
    assert b.order(symbol="AAPL", clientOrderId="x1").json()["orderStatus"] == "PendingNew"  # no R114


def test_scenario_switch_resets_portfolio(tz):
    tz.order(symbol="AAPL")
    assert tz.positions()
    out = tz.scenario("basic_fills")
    assert out["now"] == "2026-01-05T14:31:00.0000000Z"
    assert tz.positions() == []
    assert tz.pnl()["availableCash"] == 1_000_000.0


def test_scenario_params_override(tz):
    tz.scenario("basic_fills", cash=50_000, start="2026-01-05T15:00:00Z")
    assert tz.pnl()["availableCash"] == 50_000
    assert tz.get("/mock/state").json()["now"] == "2026-01-05T15:00:00.0000000Z"


def test_unknown_scenario(tz):
    r = tz.post("/mock/scenario", json={"name": "nope"})
    assert r.status_code == 404
    assert "basic_fills" in r.json()["available"]


def test_list_scenarios(client):
    names = {s["name"] for s in client.get("/mock/scenarios").json()}
    assert {"basic_fills", "partial_fills", "short_htb"} <= names


def test_time_only_moves_forward(tz):
    tz.advance("2026-01-05T14:40:00Z")
    r = tz.post("/mock/time", json={"to": "2026-01-05T14:35:00Z"})
    assert r.status_code == 400
    r = tz.post("/mock/time", json={"to": "2026-01-05T14:40:00Z"})
    assert r.status_code == 400


def test_auth_is_not_checked_but_key_is_required(client):
    r = client.get("/v1/api/accounts")
    assert r.status_code == 404 and r.text == "Not found"
    r = client.request("DELETE", "/v1/api/accounts/X/orders/1")
    assert r.status_code == 401 and r.json()["message"] == "Token not provided"


def test_wrong_account(tz):
    r = tz.get("/v1/api/accounts/WRONG/positions")
    assert r.status_code == 400
    assert r.json() == {"statusCode": "BadRequest", "message": "GetPositions",
                        "detail": f"{tz.acc} - WRONG - ASLib Account for User was not found, "
                                  "or User doesn't have entitlements."}
