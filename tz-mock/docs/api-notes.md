# TradeZero API: observed behaviour and mock decisions

This file is the reference for what tz-mock does and why. Each item comes from one of these sources:
- **[paper]**: observed on the real paper API (`https://webapi.tradezero.com/v1/api`, account type `Paper`). The first probe was on 2026-11-19.
- **[docs]**: taken from developer.tradezero.com.
- **[mock]**: a mock-only choice where no reference exists. Verify it against the real API when you can.

When the real API and the mock disagree, fix the mock and update this file.

## General
- [paper] JSON keys in responses come back in alphabetical order. Orders use integers for whole numbers (`"limitPrice":1`), while positions, accounts and P&L use floats (`0.0`).
- [paper] Timestamps are UTC ISO-8601 strings with 7 fractional digits, e.g. `2026-11-19T15:30:22.9461086Z`.
- [paper] A missing or invalid auth header returns 404 `Not found` with no content type. The same bare 404 comes back for an unknown route or a non-JSON order body.
- [docs] DELETE of a single order without auth returns 401 JSON `{"statusCode":"Unauthorized","message":"Token not provided","detail":null}`.
- [mock] Any `TZ-API-KEY-ID` value is valid, and the secret is ignored. Each key value is its own isolated session. The account id is `TZP` + the first 5 hex characters of sha1(key), uppercased.
- [mock] Rate limits are not enforced. The real API returns a bare 429 with no body or headers.
- [paper] A second `GET /positions` 140 ms after the first returned 429, although the docs give `/positions` and `/pnl` 3/s with a burst of 3 (2026-11-20). The connector spaces `/pnl` and `/positions` calls by 350 ms and retries 429s after 1.5 s.

## Errors
- [paper] Wrong account in a path returns 400 JSON `{"statusCode":"BadRequest","message":"<Operation>","detail":"<acc> - <wrong> - ASLib Account for User was not found, or User doesn't have entitlements."}`. The `<Operation>` value seen is `GetPositions`. The mock derives it from the endpoint (e.g. `GetAccount`, `GetPnl`, `GetOrders`).
- [paper] `GET /order/{unknown}` returns 400 JSON with `message:"GetOrder"` and `detail:"<acc> - <acc> - ASLib Unable to find specified order. Ok"`. The docs say 404.
- [paper] `DELETE /orders/{unknown}` returns 404 `Not found` as text.
- [paper] Cancel-all with a wrong `account` returns 400 `{"statusCode":"BadRequest","message":"ASLib Account for User was not found, or User doesn't have entitlements.","detail":null}`.

## Accounts
- [paper] `GET /accounts` returns `{"accounts":[row]}`. `GET /account/{id}` returns the same row bare, with `bp` in place of `buyingPower`.
- [paper] Row fields: `account, accountStatus:"Active", accountType:"Paper", availableCash, availableCashEMS, buyingPower|bp, equity, firmEntity:"TZG", firmId:62, fractional:false, isFutureAccount, leverage:1.0, maintenanceDeficit, marginDeficit, marginRatio:100.0, marginRequirement, marginType:"M", optContractsTraded, optLevel, optionCashTotalBalance, optionTradingLevel, overnightBp, realized, sharesTraded, sodEquity, totalCommissions, totalLocateCosts, tradingSuspended, unrealized, usedLeverage`.
- [paper] Live during the day: `availableCash`, `buyingPower`/`bp` and `marginRequirement` change after fills.
- [paper] Start-of-day snapshots that do **not** move intraday: `equity`, `realized`, `unrealized`, `sharesTraded`, `usedLeverage` and `overnightBp`. Read live values from `/pnl`. The mock refreshes these snapshots on each new ET trading day.
- [paper] After buying 1 AAPL at 348.86: `availableCash` = 1,000,000 − 348.86 − 1.00, and `marginRequirement` = 349.86. After selling it back, `availableCash` was 999,999.0 and `marginRequirement` 1.0.
  - The extra 1.00 looks like a fee or reserve. **The mock does not reproduce it.** It uses `availableCash = initial cash + realized − locate costs − Σ|shares|·avgPrice` and `marginRequirement = Σ|shares|·avgPrice`.
- [mock] `bp` = `buyingPower` = `availableCash × leverage`.

## P&L (`GET /accounts/{id}/pnl`)
- [paper] Fields: `accountValue, allowedLeverage, availableCash, contractsTraded, dayPnl, dayRealized, dayUnrealized, equityRatio, exposure, initialMargin, intradayMargin, maintenanceMargin, marginExcess, optionCashUsed, pnl[], sharesTraded, totalUnrealized, usedLeverage`.
- [paper] `pnl[]` rows: `dayPctPnLMove, dayRealizedPnl, dayUnrealizedPnL, exposure, pctPnLMove, positionId (string), realizedPnl, symbol, unrealizedPnL`. A closed lot stays in the list with `exposure: 0`.
- [paper] The P&L values are live, e.g. `sharesTraded` counts both legs (2.0 after a buy and a sell).
- [mock] Exposure per row is signed `shares × price`. The aggregate `exposure` is Σ|row exposure|. `pctPnLMove` = (realized + unrealized) / (cost basis) × 100.

## Positions
- [paper] `GET /positions` returns `{"positions":[...]}` with these fields: `accountId, createdDate, dayOvernight, initialRequirement, maintenanceRequirement, marginRequirement, positionId (string), priceAvg, priceClose, priceOpen, priceStrike, putCall:"None", rootSymbol:null, securityType:"Stock", shares, side:"Long"|"Short", symbol, tradedSymbol:null, updatedDate`.
- [paper] **A position that goes flat stays in `/positions`** with `shares: 0.0` and `priceClose` set, for the rest of the day. The mock drops flat positions at the next ET day roll.
- [paper] `GET /positions/closed` returns `{"closedPositions":[...]}`. Rows have the same fields plus `realized, sharesIn, sharesOut`. **`positionId` is a JSON number here** and a string in `/positions`.
- [paper] `GET /positions/historical` without `date` returns 400 `{"statusCode":"BadRequest","message":"GetHistoricalPositions","detail":"<acc> - <acc> - Date is required."}`. With `?date=YYYY-MM-DD` it returns `{"historicalPositions":[]}`. The mock always returns an empty list.
- [mock] One active position per symbol, with a weighted average cost. A fill that crosses zero closes the position and opens a new one with a new `positionId`.

## Orders
- [paper] POST returns the full order row with `orderStatus:"PendingNew"`, `text:null` and `route:"PAPER"`.
  - A later GET shows `New` with `text:"TRAFIX_SIM"`, and fills keep `text:"TRAFIX_SIM"`.
  - A market order is `Filled` in under 300 ms. On a filled Market order, `limitPrice` is set to the fill price, or about +0.02 on buys. The mock sets it to the fill price.
- [paper] Order row fields: `accountId, canceledQuantity, clientOrderId, executed, lastPrice, lastQuantity, lastUpdated, leavesQuantity, legCount:0, legs:null, limitPrice, maintenanceRequirement:0, marginRequirement:0, maxDisplayQuantity:0, openClose, orderQuantity, orderStatus, orderType, pegDifference:0, pegOffsetType:"Price", priceAvg, priceStop, route, securityType, side, startTime, strikePrice:0, symbol, text, timeInForce, tradedSymbol`.
- [paper] A client-chosen `clientOrderId` such as `LN1120141028.1.1` (letters, digits, dots) is accepted and echoed back (2026-11-20).
- [paper] Without a `clientOrderId`, the server generates one like `1119033049007.249`. The mock uses `MMDDhhmmssfff.<seq>` from the virtual clock, with a 12-hour `hh`.
- [paper] If `openClose` is missing, it defaults to `"Open"`. `orderQuantity` may be sent as a numeric string.
- [paper] A rejected order has `canceledQuantity = orderQuantity` and `leavesQuantity = 0`.

### Rejections (HTTP 200, `orderStatus:"Rejected"`)
Rejections marked "sync" come back in the POST response. The others return `PendingNew` in the POST response and show as `Rejected` on the next GET.

| Text | Case | When |
|---|---|---|
| `R114: Invalid duplicate UserOrderId` | reused clientOrderId (also after cancel). Full row with `openClose:"Unknown"`, `route:"<no value>"`. The original order is untouched. | sync [paper] |
| `R145: Negative Price Is Not Allowed` | negative limit/stop | sync [paper] |
| `R72: Limit orders cannot have a price of 0.00` | Limit/StopLimit without `limitPrice` | async [paper] |
| `R24: Your order cannot have a STOP price of Zero` | Stop/StopLimit without `stopPrice` | async [paper] |
| `R23: You can only use DAY expiration on Market orders` | Market with non-Day TIF | async [paper] |
| `R100: Order would exceed max notional value` | 100000 × $500. The mock applies it when an opening notional > buying power | async [paper] |
| `R146: Validation Order Error: Symbol Is Missing` | `securityType:"Option"` with a plain ticker. The mock rejects every Option/Mleg order with this text | async [paper] |
| `R78: Market orders are not allowed at this time` / `R79: Stop orders…` / `R80: Stop Limit orders…` | outside 09:30–16:00 ET | [docs], async in mock |
| `MOCK: Short sale of hard-to-borrow symbol requires a locate` | short on HTB without inventory. **Text unverified (live-only behaviour).** | async [mock] |

- [paper] An unknown symbol (e.g. `ZZZZQQ`) with a Limit order is accepted as `New`. In the mock, symbols with no bars accept orders that never fill.
- [paper] `ImmediateOrCancel` on paper is accepted and then `Canceled` when not fillable. The mock fills IOC/FOK immediately where possible and cancels the remainder.

### Validation errors (HTTP 400 `text/plain`, one line per error)
- [paper] Observed order of the lines: symbol, orderQuantity, securityType, orderType, side, then timeInForce (the mock adds openClose last).
  ```
  - at '/symbol': 'AA PL' does not match pattern '^[a-zA-Z0-9._-]+$'
  - at '/symbol': '<no value>' does not match pattern '^[a-zA-Z0-9._-]+$'
  - at '/orderQuantity': minimum: got 0, want 1×10⁻⁰⁶
  - at '/orderQuantity': maximum: got 2×10⁰⁶, want 1×10⁰⁶
  - at '/securityType': value must be one of 'Stock', 'Option', 'Mleg', 'Future', 'Prediction'
  - at '/orderType': value must be one of 'Limit', 'Market', 'Stop', 'StopLimit'
  - at '/side': value must be one of 'Buy', 'Sell'
  - at '/timeInForce': value must be one of 'Day', 'GoodTillCancel', 'AtTheOpening', 'ImmediateOrCancel', 'FillOrKill', 'GoodTillCrossing', 'Day_Plus', 'GTC_Plus'
  ```
- [paper] A fractional quantity passes the schema check but returns 400 JSON `{"statusCode":"BadRequest","message":"PlaceOrderWithResponse","detail":"ASLib Order quantity invalid, fractional shares not enabled."}`.
- [paper] The docs' message formats (`- orderQuantity: Invalid type...`) are outdated. The format above is what the server sends today.

### Cancel
- [paper] `DELETE /accounts/{id}/orders/{cid}` on a working order returns 200 with the row: `Canceled`, `canceledQuantity` = leaves, `leavesQuantity` 0.
- [paper] Cancel on a terminal order (Canceled, Filled or Rejected) returns 200 with the row unchanged except `text` = `R130: Cancel Request Rejected: Original order not found or cancelableOriginal order not found or cancelable`. The duplicated phrase is how the server sends it.
- [paper] Cancel-all is `DELETE /accounts/orders` with a form-encoded `account` field and optional `?symbol=`. It returns 200 `{"message":"Cancel Request Submitted Successfully"}`. A JSON body returns 404 `Not found`.
- [mock] Cancel-all is applied synchronously.

### Lifecycle and the virtual clock (mock)
- [mock] A Market order fills right away at the current price, which is the close of the last bar with `ts <= now`. If no bar exists yet, it fills at the open of the next bar.
- [mock] Fill rules for later bars:
  - A marketable Limit fills right away at the current price.
  - A resting Limit fills on a later bar when that bar's range crosses it, at `min(limit, open)` for buys or `max(limit, open)` for sells.
  - A Stop triggers on `high >= stop` for buys or `low <= stop` for sells, and fills at `max(stop, open)` or `min(stop, open)`.
  - A StopLimit triggers like a Stop, then behaves as a Limit.
- [mock] Fill quantity per bar and symbol is capped at `floor(volume × scenario.participation)`. The leftover quantity gives `PartiallyFilled`.
- [mock] At 16:00 ET, working `Day` orders become `Expired`. This is unverified; it could be `DoneForDay`.
- [mock] On a new ET date: the start-of-day snapshots are refreshed, positions become `Overnight`, flat positions are dropped, and `GET /orders` keeps only working orders from earlier days.
- [mock] No commissions or fees.

## Routes
- [paper] `{"routes":[{orderTypes:["Market","Limit","Stop","StopLimit","RangeOrder"], routeName:"PAPER", securityTypes:["Stock","Option"], timesInForce:["Day","GoodTillCancel","GoodTillCrossing"], useDisplayQty:false}, {… routeName:"PAPERM", securityTypes:["MLEG"], timesInForce:["Day"] …}]}`

## History endpoints (always empty in the mock)
- [paper] `GET /orders/start-date/{d}` returns **401 text/plain** `Authorization header is required for master controller.`
- [paper] `GET /orders-with-pagination/start-date/{d}` returns `{"pagination":{"currentLimit":100,"currentOffset":0,"specifiedSymbol":null,"totalRecords":0},"tradingHistory":[]}`.
- [paper] `GET /cash-transactions/start-date/{d}` returns `{"cashHistory":[],"pagination":{"currentLimit":100,"currentOffset":0,"totalRecords":0}}`.

## Locates and short selling: live is the reference
Paper treats every symbol as easy to borrow and produces no locate inventory (api-keys page, "Get keys for a paper account"). For this area the mock follows the **live** workflow from the Short Locates guide [docs]:
- **ETB check:** `GET /is-easy-to-borrow/symbol/{sym}` returns `{"isEasyToBorrow": bool}`. It is false for symbols in `scenario.htb` or `scenario.regsho`. The value does not flip after a locate.
- **Quote:** `POST /accounts/locates/quote` takes `{account, symbol, quantity, quoteReqID}` and returns `{"locateQuoteSent":"true"}`.
  - A quantity below 100 or above 2147483647 returns a ProblemDetails 400.
  - [docs] A quantity that is not a multiple of 100 returns 200, then a row 56 with `locateError:67`, `R67: You can only offer for credit in lots of 100`.
  - A reused `quoteReqID` returns 400 text `R114: Invalid duplicate UserOrderId`. The exact shape is unverified. [mock] `q` and `q.SU` count as the same id.
- **Quote rows** [docs]: an ordinary HTB symbol gets one row, `locateType` 1 or 2 (two pools of the same product; `scenario.locate_pool_code` picks 2). A **Reg SHO threshold** symbol (`scenario.regsho`) gets two rows: the base `quoteReqID` with `locateType:3` (PreBorrow, `preBorrow:true`) and `quoteReqID + ".SU"` with `locateType:4` (SingleUse), each with its own price (`locate_price` as `{"PreBorrow": p, "SingleUse": p}`).
- **Refusals before a pool is picked** give a single row, status 56, `locateType:0` [docs], checked in this order:
  - [mock] an error queued with `POST /mock/locates/next-error` (status, code and text as given);
  - not in lots of 100: `locateError:67` (above);
  - ETB symbol: `locateError:56`, `R56: This symbol is Easy to Borrow.`
  - current price below $0.50: `locateError:40`, `R40: Shorting stocks under $0.500000 is not allowed`.
  - an open quote already exists for the symbol (Offered or still Quoting): `locateError:14`, `14 Locate is already pending.`
  - no price for the symbol, or [mock] more than the remaining `scenario.locate_supply`: `locateError:11`, `11 No shares available. Please try again later.` (the guide shows one example with two spaces after the first period).
- **One side of a Reg SHO pair refused** [docs]: a type missing from the price dict gets its row at 54 with `locateError:11`, keeping its `locateType`, while the other row is offered.
- **Quoting** [docs: 81 exists]: [mock] with `scenario.locate_quote_polls = N`, each new row shows 81 (price 0, error 0, text `""`) for N history reads, then its outcome. Accept is ignored while 81. With `locate_sibling_polls = N` the `.SU` row is missing from the first N history reads. The defaults (0) give the outcome at once.
- **Expiry:** an offer expires 30 virtual seconds after it reaches 65 (`locate_offer_seconds`), moving to 67 with `Quote expired.`
- **Accept:** `POST /accounts/locates/accept` takes `{accountId, quoteReqID}` and always returns `{"locateAcceptSent":"true"}` [docs], even for an unknown, expired or still quoting id.
  - On an Offered row: it moves to 50 with `filledShares` set, the inventory row of its `locateType` grows, and `totalLocateCosts` rises by `locatePrice × qty`, which is also deducted from cash. [mock] The remaining `locate_supply` drops by `qty`.
  - [docs] Choosing between a Reg SHO pair is done by the id: the base id accepts PreBorrow, `<id>.SU` accepts SingleUse. The other row, if still Offered, moves to 67 `Quote expired.`
  - [mock] Symbols in `scenario.locate_accept_ignored` acknowledge the accept and change nothing (the offer then expires).
- **Cancel:** `DELETE /accounts/locates/cancel/accounts/{acc}/quoteReqID/{q}` returns `{"locateCancelSent":"true"}`. On an Offered row it moves to 52 with `Quote canceled.` Otherwise there is no change. [mock] It cancels only the row named: the base id does not cancel `.SU` (unverified).
- **Sell-back:** `POST /accounts/locates/sell` takes `{account, symbol, quantity, locateType, quoteReqID}` and returns `{"locateSellbackSent":"true"}`. `locateType` is one of `Unknown`, `Locate`, `IntraDay`, `PreBorrow`, `SingleUse`. `Locate` and `IntraDay` select the type 1 or 2 row.
  - It adds a history row with status 48, negative `locateShares` and the row's `locateType`.
  - Inventory moves from `available` to `toBeSold`. On the next clock tick the row becomes 50 and the shares move to `sold`. [mock] The credit is `price × qty × scenario.locate_sellback_credit` (default 1.0; the real amount is undocumented).
  - A quantity that is not a multiple of 100 gets row 56 with `R67: You can only offer for credit in lots of 100`.
  - More than the type's `available`, or a type not held, gets row 56 with `16 Insufficient shares.`
  - [docs] SingleUse can only be sold back if no share was ever used to open a short. [mock] Otherwise: row 56, `locateError:16`, text `MOCK: SingleUse locate shares were used to open a short and cannot be credited back` (the real text is unknown).
- **History row fields:** `accountId, symbol, quoteReqID, locateShares, filledShares, locateStatus, locateType, locatePrice, preBorrow, locateError, text, createdDate, updatedDate`, wrapped as `{"locateHistory":[...]}`.
- **Inventory row fields:** `accountId, symbol, available, sold, toBeSold, unavailable, locateType`, wrapped as `{"locateInventory":[...]}`. The wrapper was observed on paper; the docs say a bare list. There is one row per (symbol, `locateType`).
- **Missing body fields** on accept or sell return 400 text `- (root): <field> is required`. Wrong account returns 400 `{"statusCode":"BadRequest","message":"Account for User was not found, or User doesn't have entitlements.","detail":null}`.
- **Short orders:** [mock] a short is a Sell for more shares than the current long position.
  - On an HTB symbol, the short part must be covered by the sum of `available` over the symbol's inventory rows. The shares are reserved when the order is placed, taken from the rows in `scenario.locate_use_order` (default Locate, PreBorrow, SingleUse; which row the real API uses first is undocumented), and given back if the order is rejected, canceled or expires.
  - When the short fills, its shares move from `available` to `unavailable`. On a cover [docs] Locate and PreBorrow shares go back to `available` (reusable the same day), and SingleUse shares are used up (they leave `unavailable` and do not come back).
  - Reads enrich `side` to `SellShort` or `BuyToCover` [docs]. The POST response keeps `Sell` or `Buy`.
- **Day roll:** [mock] locates are valid for one day. At the new ET date, history, open offers, inventory, pending sell-backs and order reservations are cleared, and `locate_supply` is reset.
- **Paper** [docs]: locates are live only. A paper quote returns 200 and the row is rejected with 56; accept and sell acknowledge and the inventory stays empty. The mock always follows the live behaviour.

## Fault injection (mock only)
`POST /mock/faults {"method","path","status"=500,"count"=1,"when"="before","delayMs"=0,"body"=""}` fails the next `count` matching `/v1/api` requests of the session. `path` is an fnmatch pattern on the URL path. `when:"before"` means the request is not processed; `when:"after"` means it is processed and only the response is lost (an "outcome unknown" case). The response has the given status and body (bare by default, like the real 429). `GET /mock/faults` lists the faults and `DELETE /mock/faults` clears them. `/mock` routes are never faulted.

## Not implemented
WebSocket streams, options and multi-leg orders, rate limits, commissions, `AtTheOpening`, `Day_Plus` and `GTC_Plus` session rules (`AtTheOpening`/`Day_Plus` behave like `Day`; `GoodTillCrossing`/`GTC_Plus` persist like `GoodTillCancel`), extended hours.
