"""Compare response key sets between the real paper API and a running tz-mock (read-only GETs).

    export TZ_API_KEY_ID=... TZ_API_SECRET_KEY=...      # paper keys
    python scripts/compare_paper.py [--mock http://127.0.0.1:8000]

Lists, for each endpoint, keys present only on one side. Empty lists on either side are skipped
for row-level comparison (place an order on both sides first to compare order/position rows).
"""

import argparse
import json
import os
import time
import urllib.error
import urllib.request

PAPER = "https://webapi.tradezero.com"


def get(base: str, path: str, key: str, secret: str):
    req = urllib.request.Request(base + path, headers={
        "TZ-API-KEY-ID": key, "TZ-API-SECRET-KEY": secret, "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:200]


def shape(obj):
    """Nested key structure: dict -> {key: shape}, list -> [shape of first item]."""
    if isinstance(obj, dict):
        return {k: shape(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [shape(obj[0])] if obj else []
    return type(obj).__name__


def diff(a, b, path=""):
    out = []
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k not in b:
                out.append(f"{path}.{k}: only on paper")
            elif k not in a:
                out.append(f"{path}.{k}: only on mock")
            else:
                out += diff(a[k], b[k], f"{path}.{k}")
    elif isinstance(a, list) and isinstance(b, list) and a and b:
        out += diff(a[0], b[0], path + "[]")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mock", default="http://127.0.0.1:8000")
    args = ap.parse_args()
    key, secret = os.environ["TZ_API_KEY_ID"], os.environ["TZ_API_SECRET_KEY"]

    _, accts = get(PAPER, "/v1/api/accounts", key, secret)
    _, mock_accts = get(args.mock, "/v1/api/accounts", key, secret)
    pa, ma = accts["accounts"][0]["account"], mock_accts["accounts"][0]["account"]
    paths = ["/v1/api/accounts", "/v1/api/account/{a}", "/v1/api/accounts/{a}/pnl",
             "/v1/api/accounts/{a}/positions", "/v1/api/accounts/{a}/positions/closed",
             "/v1/api/accounts/{a}/orders", "/v1/api/accounts/{a}/routes",
             "/v1/api/accounts/{a}/locates/history", "/v1/api/accounts/{a}/locates/inventory",
             "/v1/api/accounts/{a}/is-easy-to-borrow/symbol/AAPL"]
    for p in paths:
        ps, pj = get(PAPER, p.format(a=pa), key, secret)
        ms, mj = get(args.mock, p.format(a=ma), key, secret)
        problems = ([f"status paper={ps} mock={ms}"] if ps != ms else []) + diff(shape(pj), shape(mj))
        print(("OK  " if not problems else "DIFF") + f" {p}")
        for line in problems:
            print("      " + line)
        time.sleep(1.1)  # stay under the paper rate limits


if __name__ == "__main__":
    main()
