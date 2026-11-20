# Lean.Brokerages.TradeZero

TradeZero brokerage plugin for [QuantConnect LEAN](https://github.com/QuantConnect/Lean), built from
[Lean.Brokerages.Template](https://github.com/QuantConnect/Lean.Brokerages.Template).

| | |
|---|---|
| API | TradeZero REST API only (`https://webapi.tradezero.com/v1/api`). The WebSocket API is beta, so it is not used. Orders, fills and account figures are polled. |
| Assets | US stocks, long and short. Short selling only for **easy-to-borrow** symbols (locates are not supported). Whole shares only. |
| Orders | Market, Limit, StopMarket, StopLimit, MarketOnOpen. Time in force: Day and GoodTilCanceled, plus TradeZero-only values through `TradeZeroOrderProperties`. |
| Updates | Cancel-then-replace, because TradeZero has no modify endpoint. |
| Safety | `tradezero-environment` defaults to `paper`. The brokerage refuses to connect if the account's `accountType` does not match. |
| Debugging | Optional JSON-lines log of every API exchange, with the API key and secret masked. |
| Market data | None: TradeZero has no market-data API. Choose a LEAN data feed at runtime. |

## Order mapping

| Lean | TradeZero `orderType` / `timeInForce` | Notes |
|---|---|---|
| `MarketOrder` | `Market` / `Day` | TradeZero only accepts Day on market orders (R23). Lean's default GTC is sent as Day. |
| `LimitOrder` | `Limit` / `Day` or `GoodTillCancel` | |
| `StopMarketOrder` | `Stop` | |
| `StopLimitOrder` | `StopLimit` | |
| `MarketOnOpenOrder` | `Market` / `AtTheOpening` | Accepted 04:00–09:25 ET |
| `OutsideRegularTradingHours = true` | `Day_Plus` / `GTC_Plus` | Limit and StopLimit only (04:00–20:00 ET) |
| `TimeInForceOverride` | `ImmediateOrCancel`, `FillOrKill`, `GoodTillCrossing`, `AtTheOpening` | Paper does not support IOC/FOK |
| `GoodTilDate`, MarketOnClose, TrailingStop, LimitIfTouched, combos, options | rejected before sending | |

`Buy`/`Sell` and `Open`/`Close` are derived from the current position:
- An order that reduces a position closes it (`Sell/Close`, `Buy/Close`).
- An order on a flat or same-side position opens one. `Sell/Open` is a short sale.
- An order that **crosses zero** (long 10, sell 25) is sent as a closing order. The opening order for the rest is placed when the first one is filled. Lean sees one order.

**Updates** cancel the current TradeZero order and wait for it to be final. They then apply the fills it got, and place a new order for `new quantity - filled` (re-planned from the current position). Each TradeZero order gets a new `clientOrderId` (`LN<run>.<leanId>.<n>`), and all of them are listed in `Order.BrokerId`. If the replacement is rejected, the Lean order ends `Canceled`. Nothing works at the broker between the cancel and the replacement.

## Margin and account figures from Python

Lean's own figures work as usual: `self.portfolio.margin_remaining`, `total_margin_used`, `get_buying_power(...)`. On a margin account the TradeZero model uses Lean's pattern-day-trading margin model: 4x intraday and 2x overnight by default, set with `tradezero-intraday-leverage` / `tradezero-overnight-leverage`. **Check your account's allowed leverage:** paper reports `allowedLeverage: 1.0`. The brokerage warns at connect when the two differ.

The broker's own numbers (what TradeZero uses to accept or reject an order) are in `TradeZeroAccount`. They are refreshed every 5 s from `/pnl`, and from `/account` every 30 s:

```python
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.Brokerages.TradeZero")
from QuantConnect.Brokerages.TradeZero import TradeZeroAccount, TradeZeroOrderProperties, TradeZeroTimeInForce

account = TradeZeroAccount.get_current()      # None in backtests / before connecting
if account is not None:
    self.debug(f"value={account.account_value} bp={account.buying_power} overnight bp={account.overnight_buying_power} "
               f"leverage {account.used_leverage}/{account.allowed_leverage} maint={account.maintenance_margin} "
               f"excess={account.margin_excess}")

props = TradeZeroOrderProperties()
props.outside_regular_trading_hours = True
self.limit_order("AAPL", 10, 180.0, order_properties=props)
```

Other fields: `available_cash`, `exposure`, `initial_margin`, `intraday_margin`, `margin_requirement`, `equity_ratio`, `day_pnl`, `day_realized`, `day_unrealized`, `total_unrealized`, `start_of_day_equity`, `trading_suspended`, `is_paper`, `pnl_updated_utc`, `account_updated_utc`.

- The same object is also available as `TradeZeroBrokerageModel().account`.
- `self.shortable(symbol, -qty)` uses TradeZero's easy-to-borrow check. Don't use `self.shortable_quantity(symbol)`: Lean reports "no limit" as 0 there.

Lean's cash is set to `accountValue − Σ position market value`, so short-sale proceeds count as cash and Lean's total portfolio value equals TradeZero's account value. TradeZero's `availableCash` does not credit short proceeds.

## API exchange log

Set `tradezero-api-log-file`. Every line is one JSON object:
- `kind: "http"`: method, path, status, ms, and the request and response bodies.
- `kind: "lean"`: how a Lean order was translated, fills, status changes, cancel/replace steps.
- `kind: "state"`: connect, disconnect, reconnect.

Headers are never logged, and every line is scrubbed for the key and secret values. Identical consecutive poll responses are collapsed: the next different response carries `"unchangedBefore": N`. Files rotate at `tradezero-api-log-max-mb`. Send the file back for troubleshooting.

## Configuration

| Key | Default | |
|---|---|---|
| `tradezero-api-key-id`, `tradezero-api-secret-key` | env `TZ_API_KEY_ID`, `TZ_API_SECRET_KEY` | |
| `tradezero-environment` | `paper` | `paper` connects only to an account of type Paper. `live` connects only to a non-paper account. |
| `tradezero-api-url` | `https://webapi.tradezero.com` | Point it at tz-mock for tests |
| `tradezero-account-id` | | Required when the keys see several accounts |
| `tradezero-route` | | Paper: none. Live: `SMART` (or the first stock route) |
| `tradezero-api-log-file`, `-max-mb`, `-bodies` | off, 50, true | |
| `tradezero-poll-active-ms` / `-idle-ms` / `-account-ms` | 1000 / 5000 / 5000 | Within the documented rate limits |
| `tradezero-reconcile-seconds` | 30 | Warns when positions differ from Lean's holdings |
| `tradezero-account-type` | `margin` | or `cash` |
| `tradezero-intraday-leverage` / `-overnight-leverage` | 4 / 2 | |
| `tradezero-fee-per-order` / `-per-share` | 0 / 0 | The API reports no per-fill commissions |

## Build and test (dev container)

The projects reference a Lean checkout next to this repository (`../Lean`). The dev container mounts the parent folder and clones Lean there if it is missing. It runs on `quantconnect/lean` (the same .NET and Python as production), next to a running **tz-mock** (`http://tzmock:8000`).

- **VS Code:** *Reopen in Container*.
- **Without VS Code:**

```bash
docker compose -f .devcontainer/docker-compose.yml up -d --build
```

```bash
docker compose -f .devcontainer/docker-compose.yml exec dev dotnet build QuantConnect.TradeZeroBrokerage.sln
```

```bash
docker compose -f .devcontainer/docker-compose.yml exec dev dotnet test QuantConnect.TradeZeroBrokerage.Tests --filter "TestCategory!=Paper"
```

The tests:
- **Unit:** mapping, settings, the paper/live guard, cash, the model, and the logger (secrets never written).
- **`TzMock`:** fills, partial fills, cancel/replace, crossing zero, easy- and hard-to-borrow, expiry, rejections, restart recovery, Python access, and Lean's standard `BrokerageTests` suite.
- **`Paper`:** explicit, against the real paper API, and protected by the guard. `ReadsPaperAccount` is read-only. `PlacesAndCancelsFarLimitOrder` also needs `TZ_PAPER_ALLOW_ORDERS=1`. Run them with:

```bash
docker compose -f .devcontainer/docker-compose.yml exec -e TZ_API_KEY_ID -e TZ_API_SECRET_KEY dev dotnet test QuantConnect.TradeZeroBrokerage.Tests --filter TestCategory=Paper
```

## Docker image

```bash
docker build -t lean-tradezero .
```

- The image is `quantconnect/lean` plus `QuantConnect.Brokerages.TradeZero.dll`, compiled against the image's own Lean binaries. Pin a version with `--build-arg LEAN_IMAGE=quantconnect/lean:<tag>`.
- **No data feed is embedded:** you choose it at runtime. Put a data-feed plugin's DLLs in a folder mounted at `/Lean/plugins`, then name its class in `data-queue-handler` (see [docker/config.tradezero.json](docker/config.tradezero.json)):

```bash
docker run --rm -it \
  -v $PWD/docker/config.tradezero.json:/config/config.json:ro \
  -v $PWD/MyAlgorithm:/Algorithm:ro \
  -v $PWD/my-data-feed-dlls:/Lean/plugins:ro \
  -v $PWD/results:/Results \
  -e TZ_API_KEY_ID -e TZ_API_SECRET_KEY \
  lean-tradezero --config /config/config.json
```

`docker/smoke-test.sh` runs the image end to end against tz-mock, with Lean's fake data feed: connect, read the account from Python, place and cancel an order.

## CI/CD (GitHub Actions)

[.github/workflows/test-build-publish.yml](.github/workflows/test-build-publish.yml) runs three jobs, each starting only if the previous one passed:

| Job | Runs on | What it does |
|---|---|---|
| **test** | every push and PR | Checks out LEAN next to the repo (same branch name if LEAN has it, else `master`) and starts tz-mock. It then compiles the solution in the `quantconnect/lean` image and runs every test except `Paper`: unit, tz-mock, LEAN's brokerage suite and Python. The `.trx` results are uploaded as an artifact. |
| **build** | every push and PR | Builds the Docker image and runs `docker/smoke-test.sh` on it. Outside PRs it pushes the tested image as `ghcr.io/<owner>/lean-tradezero:sha-<commit>`. |
| **publish** | `main` and `v*` tags | Promotes that exact image to `:latest`, `:lean-<lean version>` and `:<tag>` with a registry-side retag. Nothing is rebuilt. |

- **No TradeZero credentials in CI:** the paper-API tests (category `Paper`) are run locally only (see above).
- **Image access:** the first push creates a private package. Make it public, or grant access, under *Packages* on GitHub.
- **Manual runs** (*Run workflow*) can pick another LEAN git ref and base image.
- **Disk space:** the LEAN image is about 27 GB, so each job first frees disk space on the hosted runner.

## Not supported (yet)

Locates and hard-to-borrow shorts, options, WebSocket streams, trailing stops, MarketOnClose, GoodTilDate, fractional shares, and selecting this brokerage from lean-cli (`lean live` would need a lean-cli PR).
