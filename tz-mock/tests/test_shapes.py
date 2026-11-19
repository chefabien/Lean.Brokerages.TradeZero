"""Response key sets must match what the real paper API returned (docs/api-notes.md)."""

ACCOUNT = {"account", "accountStatus", "accountType", "availableCash", "availableCashEMS", "buyingPower",
           "equity", "firmEntity", "firmId", "fractional", "isFutureAccount", "leverage",
           "maintenanceDeficit", "marginDeficit", "marginRatio", "marginRequirement", "marginType",
           "optContractsTraded", "optLevel", "optionCashTotalBalance", "optionTradingLevel", "overnightBp",
           "realized", "sharesTraded", "sodEquity", "totalCommissions", "totalLocateCosts",
           "tradingSuspended", "unrealized", "usedLeverage"}
PNL = {"accountValue", "allowedLeverage", "availableCash", "contractsTraded", "dayPnl", "dayRealized",
       "dayUnrealized", "equityRatio", "exposure", "initialMargin", "intradayMargin", "maintenanceMargin",
       "marginExcess", "optionCashUsed", "pnl", "sharesTraded", "totalUnrealized", "usedLeverage"}
PNL_ROW = {"dayPctPnLMove", "dayRealizedPnl", "dayUnrealizedPnL", "exposure", "pctPnLMove", "positionId",
           "realizedPnl", "symbol", "unrealizedPnL"}
ORDER = {"accountId", "canceledQuantity", "clientOrderId", "executed", "lastPrice", "lastQuantity",
         "lastUpdated", "leavesQuantity", "legCount", "legs", "limitPrice", "maintenanceRequirement",
         "marginRequirement", "maxDisplayQuantity", "openClose", "orderQuantity", "orderStatus", "orderType",
         "pegDifference", "pegOffsetType", "priceAvg", "priceStop", "route", "securityType", "side",
         "startTime", "strikePrice", "symbol", "text", "timeInForce", "tradedSymbol"}
POSITION = {"accountId", "createdDate", "dayOvernight", "initialRequirement", "maintenanceRequirement",
            "marginRequirement", "positionId", "priceAvg", "priceClose", "priceOpen", "priceStrike", "putCall",
            "rootSymbol", "securityType", "shares", "side", "symbol", "tradedSymbol", "updatedDate"}
CLOSED = POSITION | {"realized", "sharesIn", "sharesOut"}


def test_shapes(tz):
    a = tz.acc
    assert set(tz.get("/v1/api/accounts").json()["accounts"][0]) == ACCOUNT
    assert set(tz.get(f"/v1/api/account/{a}").json()) == ACCOUNT - {"buyingPower"} | {"bp"}
    row = tz.order(symbol="AAPL", clientOrderId="B").json()
    assert set(row) == ORDER
    tz.order(symbol="AAPL", side="Sell", openClose="Close")
    pnl = tz.pnl()
    assert set(pnl) == PNL and set(pnl["pnl"][0]) == PNL_ROW
    assert set(tz.positions()[0]) == POSITION
    assert set(tz.get(f"/v1/api/accounts/{a}/positions/closed").json()["closedPositions"][0]) == CLOSED
    assert list(row) == sorted(row)  # keys come back alphabetically, like the real API
