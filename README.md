# Lean.Brokerages.TradeZero

TradeZero brokerage plugin for [QuantConnect LEAN](https://github.com/QuantConnect/Lean), built from
[Lean.Brokerages.Template](https://github.com/QuantConnect/Lean.Brokerages.Template).

| | |
|---|---|
| API | TradeZero REST API only (`https://webapi.tradezero.com/v1/api`). The WebSocket API is beta, so it is not used. Orders, fills and account figures are polled. |
| Assets | US stocks, long and short. Hard-to-borrow shorts through **locates** (see *Locates*). Whole shares only. |
| Orders | Market, Limit, StopMarket, StopLimit, MarketOnOpen. Time in force: Day and GoodTilCanceled, plus TradeZero-only values through `TradeZeroOrderProperties`. |
| Updates | Cancel-then-replace, because TradeZero has no modify endpoint. |
| Safety | `tradezero-environment` defaults to `paper`. The brokerage refuses to connect if the account's `accountType` does not match. |
| Debugging | Optional JSON-lines log of every API exchange, with the API key and secret masked. |
| Market data | None: TradeZero has no market-data API. The launcher adds a LEAN data feed at runtime (Polygon, Databento, Alpaca). |
| Distribution | A GitHub Release asset per LEAN version, run on the official `quantconnect/lean:<version>` image (see *Run live*). |

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

**Orders parked overnight.** At the end of the session, TradeZero gives a working GTC order the status `DoneForDay` and makes it `New` again at the next session (a `GTC_Plus` limit: 20:00 to 04:00 ET). `DoneForDay` is not final: Lean keeps the order working (no event), and its later fills, cancel or expiry come through as usual. A restart while an order is parked takes it over (`GetOpenOrders`). Polling slows down while every working order is parked.

**Symbols.** TradeZero knows a stock by its current ticker, Lean by its first ticker and listing date, read from the map files. ITP, first listed as ONP in 2009, is `ONP UIJ2Z3ZD225H` in Lean. For a TradeZero ticker (holdings, open orders, locates), the brokerage takes the algorithm's security of that ticker when there is one. Otherwise it builds the symbol from the map files. A ticker without a map file gets a one-time `UnmappedSymbol` warning: if it was renamed, its position or orders attach to a separate Lean security. Give LEAN recent map files through `LocalZipMapFileProvider` (see *Run live*). Every 30 s, `PositionMismatch` compares TradeZero's positions with Lean's holdings by current ticker, and names the Lean security it means.

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
- `self.shortable(symbol, -qty)` is always True: the brokerage decides when the order arrives, because a hard-to-borrow short may be possible through a locate. Use `TradeZeroLocates.is_easy_to_borrow(symbol)` and `TradeZeroLocates.get_available(symbol)` for an exact check (see *Locates*).

Lean's cash is set to `accountValue − Σ position market value`, so short-sale proceeds count as cash and Lean's total portfolio value equals TradeZero's account value. TradeZero's `availableCash` does not credit short proceeds.

## Locates (hard-to-borrow shorts)

Shorting a hard-to-borrow (HTB) symbol needs located shares. The brokerage checks TradeZero's easy-to-borrow answer (cached per day) before every order that opens or increases a short:

| Situation | What happens |
|---|---|
| Easy to borrow, or the check failed | Sent as usual |
| HTB, the account already holds enough located shares (any type) | Sent as usual, no new locate |
| HTB, not enough located shares, no `LocateHandler` | `Invalid`: "HTBX is hard to borrow: shorting 150 needs 100 more located shares (50 held). Set TradeZeroOrderProperties.LocateHandler…" |
| HTB, not enough located shares, a `LocateHandler` | `Submitted`, then the connector asks TradeZero for a quote of the missing shares (rounded up to a lot of 100), calls the handler with the offers, accepts the one it returns, and sends the order. Declined, refused, expired: `Invalid` with the reason |

```python
from QuantConnect.Brokerages.TradeZero import TradeZeroOrderProperties, TradeZeroLocates, TradeZeroLocateType

def choose_locate(quote):
    # quote.offers: 1 offer (ordinary HTB symbol) or 2 (Reg SHO symbol: PreBorrow and SingleUse), cheapest first
    reusable = [o for o in quote.offers if o.reusable_after_cover and o.price_per_share <= 0.05]
    return reusable[0] if reusable else None     # None declines every offer

props = TradeZeroOrderProperties()
props.locate_handler = choose_locate
self.market_order("HTBX", -200, order_properties=props)
```

- **The handler** runs on a connector thread: keep it short, never place orders from it, and return one of `quote.offers` or `None` (anything else, or an exception, declines). Offers expire 30 s after they are made. Useful fields: `quote.short_quantity`, `already_located`, `requested_quantity`; per offer `type`, `quantity`, `price_per_share`, `total_cost`, `covers_order`, `reusable_after_cover`, `sell_back_after_use`, `expires_utc`.
- **Locate types**: `LOCATE` (ordinary HTB symbols) and `PRE_BORROW` can be reused the same day after a cover, and their unused part can be sold back. `SINGLE_USE` is used up by the first short and can only be sold back if it was never used.
- **Cost**: TradeZero charges the locate at the accept. The connector adds it to the fee of the order's first fill. A locate bought for an order that is then canceled stays in the inventory (`LocateUnused` message).
- **Odd lots**: locates are sold in lots of 100. An HTB short that is not a multiple of 100 gets a `LocateOddLot` warning (set `suppress_odd_lot_locate_warning = True` to hide it) and goes ahead.
- **While a locate is being bought** the order can be canceled (open offers are canceled too) but not updated. An update whose new quantity needs more located shares than held is refused: cancel and re-place, or call `TradeZeroLocates.locate` first.
- **Messages** (`on_brokerage_message`): `LocateOddLot`, `LocatePurchased`, `LocateDeclined`, `LocateRefused` (broker text per refused offer), `LocateHandlerError`, `LocateUnused`, `LocateSoldBack`.

Managing located shares (reads always ask TradeZero; actions block for up to `tradezero-locate-timeout-seconds`):

```python
free = TradeZeroLocates.get_available(self.htbx)                 # all types; 0 in backtests
for inv in TradeZeroLocates.get_inventory(self.htbx):            # one row per type
    self.debug(f"{inv.type}: available={inv.available} in use={inv.unavailable} selling={inv.to_be_sold} sold={inv.sold}")
for r in TradeZeroLocates.sell_back_unused(self.htbx):           # whole lots of every type, e.g. at 15:50 ET
    self.debug(f"{r.type} {r.status} {r.quantity} {r.message}")
TradeZeroLocates.sell_back(self.htbx, 200, TradeZeroLocateType.PRE_BORROW)
TradeZeroLocates.locate(self.htbx, 300, lambda q: q.offers[0])  # buy ahead, without an order
TradeZeroLocates.get_history()                                   # today's quotes and sell-backs
```

Paper treats every symbol as easy to borrow and has no locate inventory: locates can only be exercised on live or on tz-mock (`short_htb` scenario).

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
| `tradezero-locate-timeout-seconds` | 20 | Longest a locate waits for TradeZero (quote, then the fill of the accepted offer), not counting the handler |
| `tradezero-locate-poll-ms` | 250 | How often the locate history is read while waiting |
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
- **`TzMock`:** fills, partial fills, cancel/replace, crossing zero, easy- and hard-to-borrow, locates (handler, Reg SHO offers, sell-backs, refusals, timeouts), expiry, rejections, restart recovery, Python access, and Lean's standard `BrokerageTests` suite.
- **`Paper`:** explicit, against the real paper API, and protected by the guard. `ReadsPaperAccount` is read-only. `PlacesAndCancelsFarLimitOrder` also needs `TZ_PAPER_ALLOW_ORDERS=1`. Run them with:

```bash
docker compose -f .devcontainer/docker-compose.yml exec -e TZ_API_KEY_ID -e TZ_API_SECRET_KEY dev dotnet test QuantConnect.TradeZeroBrokerage.Tests --filter TestCategory=Paper
```

## Run live (launcher)

The plugin is distributed as a GitHub Release asset, `tradezero-plugin-lean<V>.zip`. Each asset is built for one LEAN version `<V>`. It runs on the **official `quantconnect/lean:<V>` image**, at a fixed tag, never `latest`. [tradezero_live.py](tradezero_live.py) (Python 3, standard library only) assembles a run:

```bash
python3 tradezero_live.py --data-feed polygon --algorithm ./MyAlgorithm --workspace ~/qc_workspace
```

1. **LEAN version.** It reads the version of your local `quantconnect/lean` image and uses it if a plugin release asset exists for that version or an older one. If every asset is newer than your image, or you have no local image, it uses the newest LEAN version an asset exists for, and pulls that fixed tag.
2. **TradeZero plugin.** Like the data feeds, it takes the asset built for that LEAN version or the closest older one, and says so when it isn't an exact match. It downloads the asset if it isn't cached (`~/.cache/lean-tradezero`) and checks its `.sha256`.
3. **Data feed.** It fetches the feed from nuget.org, using the newest build not newer than that LEAN version. Only the files the LEAN image doesn't already contain are kept.
4. **Output.** It writes `tradezero-run/config.json` (mode 0600) and `tradezero-run/run.sh`, and prints the `docker run` command. The plugins are mounted at `/Lean/plugins`. Each start of `run.sh` writes LEAN's results (logs, charts, order events, `tradezero-api.jsonl`) to a new folder, `tradezero-run/results/<YYYY-MM-DD_HHMMSS>/` (local time), so runs don't mix.

| `--data-feed` | Package | Keys (environment variables, otherwise asked) |
|---|---|---|
| `polygon` | `QuantConnect.Lean.DataSource.Polygon` | `POLYGON_API_KEY`, `POLYGON_LICENSE_TYPE` (Individual/Business) |
| `databento` | `QuantConnect.Lean.DataSource.DataBento` | `DATABENTO_API_KEY` |
| `alpaca` | `QuantConnect.Brokerages.Alpaca` | `ALPACA_API_KEY`, `ALPACA_API_SECRET`, `ALPACA_ENVIRONMENT` (paper/live) |
| `fake` | built into LEAN | none; random prices, **testing only** |

- **QuantConnect account required for real feeds.** QuantConnect's feed modules check at startup, through the QuantConnect API, that your QuantConnect account is entitled to them, just as under lean-cli. The launcher reads your user id and API token from `QC_USER_ID` / `QC_API_ACCESS_TOKEN`, or from lean-cli's `~/.lean/credentials` (after `lean login`). It reads your organization id from `QC_ORGANIZATION_ID`, or from the `lean.json` of a lean-cli workspace given with `--workspace`.
- **TradeZero keys are never written to a file:** `run.sh` passes `TZ_API_KEY_ID` / `TZ_API_SECRET_KEY` from your environment. The feed's keys and the QuantConnect token go into `config.json`, which only you can read, because LEAN reads them only from its config.
- **Private repository:** the launcher finds the repository with `--repo owner/name` (or `TZ_PLUGIN_REPO`, or the `origin` remote). For a private repository it needs a token: `GITHUB_TOKEN`, or `gh auth login`.
- **Data folder (map and factor files):** with `--workspace`, the workspace's data folder (`data-folder` in its `lean.json`, `data/` by default) is mounted at `/Lean/Data`, as lean-cli does, so LEAN uses its map files, factor files and market-hours database. LEAN reads them with the `map-file-provider` and `factor-file-provider` the workspace's `lean.json` names (`LocalZipMapFileProvider` and `LocalZipFactorFileProvider` in a lean-cli workspace), which read the `map_files_YYYYMMDD.zip` and `factor_files_YYYYMMDD.zip` that `lean data download` writes (US Equity Security Master). The launcher stops if LEAN would find no zip from the last 30 days, and warns when the newest is over 7 days old: LEAN then knows no rename, split or delisting since. LEAN reloads the zips every day while running, so refreshing them is enough. Without these providers, LEAN reads only loose CSVs: most tickers then get no corporate actions, and a position held at TradeZero can resolve to another security than the one the algorithm trades. Without `--workspace`, LEAN uses the sample data inside the image, which covers few tickers and is dated.
- **Object store:** the `storage/` folder of the workspace (as under lean-cli), or `tradezero-run/storage/` without `--workspace`, is mounted read-write at `/Storage` (`object-store-root`), so what the algorithm saves to `ObjectStore` persists between runs.
- **Extra environment variables:** `--extra-env NAME[,NAME...]` (repeatable) passes these variables through to the LEAN container, for example a key your algorithm reads. `run.sh` holds only the names; the values come from the environment you start it in.
- **Other options:** `--lean-version`, `--plugin-version <tag>`, `--environment live`, `--set key=value` for any LEAN config value (for example `--set tradezero-intraday-leverage=1`), `--plugin-zip` (use a local build), and `--help`.

[smoke-test/smoke-test.sh](smoke-test/smoke-test.sh) checks a zip end to end against tz-mock, with the `fake` feed: it reads the account from Python, then places and cancels an order.

## CI/CD (GitHub Actions)

[.github/workflows/build-test-publish.yml](.github/workflows/build-test-publish.yml) is a single job pinned to one LEAN version (`LEAN_VERSION` in the workflow's `env`; bump it deliberately). Each stage runs only if the previous one passed:

| Stage | Runs on | What it does |
|---|---|---|
| **Build** | every push and PR | Compiles the solution in `quantconnect/lean:<V>` for the tests. Builds the plugin against that image's own LEAN binaries: `dist/tradezero-plugin-lean<V>.zip` + `.sha256`. |
| **Test** | every push and PR | Runs every test except `Paper`: unit, tz-mock, LEAN's brokerage suite and Python. Then the launcher's unit tests, and `smoke-test/smoke-test.sh` on the zip. Test results and the zip are uploaded as an artifact. |
| **Publish** | `v*` tags | Creates the GitHub Release with the zip and its checksum. |

- **No TradeZero credentials in CI:** the paper-API tests (category `Paper`) are run locally only (see above).
- **Supporting another LEAN version:** run the workflow manually with `lean_version` set to that version and `release_tag` set to an existing release. It builds and tests that tag's code against that LEAN version, then adds the asset to the release.
- **Disk space:** the LEAN image is about 27 GB, so the job first frees disk space on the hosted runner.

## Not supported (yet)

Automatic sell-back of locates at the close, options, WebSocket streams, trailing stops, MarketOnClose, GoodTilDate, fractional shares, and selecting this brokerage from lean-cli (`lean live` would need a lean-cli PR).
