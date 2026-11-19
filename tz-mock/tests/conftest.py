import pytest
from fastapi.testclient import TestClient

from tz_mock.main import create_app


class TZ:
    """Small helper around TestClient: one instance = one session (API key)."""

    def __init__(self, client: TestClient, key: str):
        self.c = client
        self.h = {"TZ-API-KEY-ID": key, "TZ-API-SECRET-KEY": "whatever"}
        self.acc = self.get("/v1/api/accounts").json()["accounts"][0]["account"]

    def get(self, path, **kw):
        return self.c.get(path, headers=self.h, **kw)

    def post(self, path, **kw):
        return self.c.post(path, headers=self.h, **kw)

    def delete(self, path, **kw):
        return self.c.request("DELETE", path, headers=self.h, **kw)

    def scenario(self, name, **params):
        r = self.post("/mock/scenario", json={"name": name, "params": params})
        assert r.status_code == 200, r.text
        return r.json()

    def advance(self, to):
        r = self.post("/mock/time", json={"to": to})
        assert r.status_code == 200, r.text
        return r.json()

    def order(self, **fields):
        body = {"securityType": "Stock", "side": "Buy", "openClose": "Open", "orderType": "Market",
                "orderQuantity": 10, "timeInForce": "Day"} | fields
        return self.post(f"/v1/api/accounts/{self.acc}/order", json=body)

    def get_order(self, cid):
        return self.get(f"/v1/api/accounts/{self.acc}/order/{cid}").json()

    def positions(self):
        return self.get(f"/v1/api/accounts/{self.acc}/positions").json()["positions"]

    def pnl(self):
        return self.get(f"/v1/api/accounts/{self.acc}/pnl").json()

    def broker(self):
        return self.c.app.state.sessions.get(self.h["TZ-API-KEY-ID"]).broker


@pytest.fixture
def client():
    return TestClient(create_app())


@pytest.fixture
def tz(client):
    return TZ(client, "test-key")
