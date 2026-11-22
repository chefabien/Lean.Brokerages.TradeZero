"""Per-session virtual broker: orders, fills against bars, portfolio, P&L and locates.

Behaviour notes and their sources live in docs/api-notes.md.
"""

from __future__ import annotations

import copy
from datetime import date, datetime, time
from zoneinfo import ZoneInfo

from .models import (
    TERMINAL, Fill, Inventory, LocateRow, Order, PendingSell, Position, money, ts,
)
from .scenarios import Scenario

ET = ZoneInfo("America/New_York")
RTH_OPEN, RTH_CLOSE = time(9, 30), time(16, 0)
PERSISTENT_TIF = {"GoodTillCancel", "GTC_Plus", "GoodTillCrossing"}

R130 = ("R130: Cancel Request Rejected: Original order not found or cancelable"
        "Original order not found or cancelable")
HTB_REJECT = "MOCK: Short sale of hard-to-borrow symbol requires a locate"
# `/sell` locateType names -> the locateType codes they cover (1 and 2 are the same product from two pools)
LOCATE_TYPES = {"Unknown": {0}, "Locate": {1, 2}, "IntraDay": {1, 2}, "PreBorrow": {3}, "SingleUse": {4}}
TYPE_NAMES = {1: "Locate", 2: "Locate", 3: "PreBorrow", 4: "SingleUse"}
SINGLE_USE = 4
NO_SHARES = "11 No shares available. Please try again later."
SINGLE_USE_USED = "MOCK: SingleUse locate shares were used to open a short and cannot be credited back"


class Broker:
    def __init__(self, account_id: str, scenario: Scenario):
        self.account_id = account_id
        self.scenario = scenario
        self.md = scenario.market_data()
        self.now: datetime = scenario.start
        self.initial_cash = scenario.cash
        self.realized = 0.0
        self.day_realized = 0.0
        self.shares_traded = 0
        self.locate_costs = 0.0
        self.locate_credits = 0.0
        self.orders: dict[str, Order] = {}
        self.positions: list[Position] = []
        self.closed: list[Position] = []
        self.locates: dict[str, LocateRow] = {}
        self.inventory: dict[tuple[str, int], Inventory] = {}
        self.pending_sells: list[PendingSell] = []
        self.locate_in_use: dict[str, list[list[int]]] = {}  # symbol -> [[locateType, shares]] held by shorts
        self.locate_supply: dict[str, int] = dict(scenario.locate_supply)
        self.next_locate_errors: dict[str, list[tuple[int, int, str]]] = {}  # symbol -> [(status, error, text)]
        self.fills: list[Fill] = []
        self._seq = 0
        self._pos_seq = 0
        self._used: dict[tuple[str, datetime], int] = {}
        self.session_date: date = self.now.astimezone(ET).date()
        self._expired_on: date | None = None
        self.sod: dict = {}
        self._snapshot_sod()
        for symbol, seed in scenario.locate_inventory.items():
            for name, shares in (seed.items() if isinstance(seed, dict) else [("Locate", seed)]):
                self.set_inventory(symbol, name, int(shares))

    # ------------------------------------------------------------------ market / portfolio
    def price(self, symbol: str) -> float | None:
        return self.md.price(symbol, self.now)

    def mark(self, p: Position) -> float:
        px = self.price(p.symbol)
        return p.price_avg if px is None else px

    def active_position(self, symbol: str) -> Position | None:
        for p in reversed(self.positions):
            if p.symbol == symbol and p.shares != 0:
                return p
        return None

    def cost_basis(self) -> float:
        return sum(abs(p.shares) * p.price_avg for p in self.positions)

    def unrealized(self) -> float:
        return sum((self.mark(p) - p.price_avg) * p.shares for p in self.positions)

    def available_cash(self) -> float:
        return (self.initial_cash + self.realized - self.locate_costs + self.locate_credits
                - self.cost_basis())

    def equity(self) -> float:
        return self.available_cash() + self.cost_basis() + self.unrealized()

    def buying_power(self) -> float:
        return self.available_cash() * self.scenario.leverage

    def exposure(self) -> float:
        return sum(abs(p.shares * self.mark(p)) for p in self.positions)

    def _snapshot_sod(self) -> None:
        eq = self.equity()
        self.sod = {
            "equity": eq,
            "unrealized": self.unrealized(),
            "usedLeverage": self.exposure() / eq if eq else 0.0,
            "overnightBp": self.buying_power(),
        }

    # ------------------------------------------------------------------ orders
    def _gen_cid(self) -> str:
        self._seq += 1
        n = self.now
        return f"{n:%m%d%I%M%S}{n.microsecond // 1000:03d}.{self._seq}"

    def today_orders(self) -> list[Order]:
        return [o for o in self.orders.values()
                if o.working or o.start_time.astimezone(ET).date() == self.session_date]

    def place(self, body: dict) -> dict:
        """Place a validated order body. Returns the POST response row."""
        lp, sp = body.get("limitPrice"), body.get("stopPrice")
        o = Order(
            account_id=self.account_id,
            client_order_id=str(body.get("clientOrderId") or self._gen_cid()),
            symbol=body["symbol"],
            security_type=body["securityType"],
            side=body["side"],
            open_close=body.get("openClose") or "Open",
            order_type=body["orderType"],
            quantity=int(float(body["orderQuantity"])),
            time_in_force=body["timeInForce"],
            limit_price=float(lp) if lp is not None else 0.0,
            stop_price=float(sp) if sp is not None else 0.0,
            route=body.get("route") or "PAPER",
            start_time=self.now,
            last_updated=self.now,
        )
        if o.client_order_id in self.orders:
            dup = copy.copy(o)
            dup.open_close, dup.route = "Unknown", "<no value>"
            dup.status, dup.text, dup.canceled = "Rejected", "R114: Invalid duplicate UserOrderId", o.quantity
            return dup.to_json(enrich=False)
        self.orders[o.client_order_id] = o

        if o.limit_price < 0 or o.stop_price < 0:  # synchronous reject
            self._reject(o, "R145: Negative Price Is Not Allowed")
            return o.to_json(enrich=False)

        response = o.to_json(enrich=False)  # PendingNew snapshot, like paper
        text = self._check(o)
        if text:
            self._reject(o, text)
        else:
            o.status, o.text = "New", "TRAFIX_SIM"
            self._match_now(o)
        return response

    def _in_rth(self) -> bool:
        et = self.now.astimezone(ET)
        return et.weekday() < 5 and RTH_OPEN <= et.time() < RTH_CLOSE

    def _check(self, o: Order) -> str | None:
        if o.security_type != "Stock":
            return "R146: Validation Order Error: Symbol Is Missing"
        if o.order_type in ("Limit", "StopLimit") and o.limit_price == 0:
            return "R72: Limit orders cannot have a price of 0.00"
        if o.order_type in ("Stop", "StopLimit") and o.stop_price == 0:
            return "R24: Your order cannot have a STOP price of Zero"
        if o.order_type == "Market" and o.time_in_force != "Day":
            return "R23: You can only use DAY expiration on Market orders"
        if o.time_in_force == "Day" and not self._in_rth():
            msg = {"Market": "R78: Market orders are not allowed at this time",
                   "Stop": "R79: Stop orders are not allowed at this time",
                   "StopLimit": "R80: Stop Limit orders are not allowed at this time"}
            if o.order_type in msg:
                return msg[o.order_type]

        pos = self.active_position(o.symbol)
        held = pos.shares if pos else 0.0
        if o.side == "Sell":
            o.short_qty = int(max(0.0, o.quantity - max(held, 0.0)))
            opening = o.short_qty
            if o.short_qty:
                o.enriched_side = "SellShort"
        else:
            opening = int(max(0.0, o.quantity - max(-held, 0.0)))
            if held < 0:
                o.enriched_side = "BuyToCover"

        ref = (o.limit_price if o.order_type in ("Limit", "StopLimit")
               else o.stop_price if o.order_type == "Stop" else self.price(o.symbol)) or 0.0
        if opening and opening * ref > self.buying_power():
            return "R100: Order would exceed max notional value"

        if o.short_qty and self.scenario.hard_to_borrow(o.symbol):
            invs = self._inventories(o.symbol)
            if sum(i.available for i in invs) < o.short_qty:
                return HTB_REJECT
            need = o.short_qty
            for inv in invs:
                take = min(need, inv.available)
                if take:
                    inv.available -= take
                    o.locate_reserved[inv.locate_type] = o.locate_reserved.get(inv.locate_type, 0) + take
                    need -= take

        return self.scenario.on_order(self, o)

    def _reject(self, o: Order, text: str) -> None:
        o.canceled = o.quantity - o.executed
        o.status, o.text, o.last_updated = "Rejected", text, self.now
        self._release_locate(o)

    def _release_locate(self, o: Order) -> None:
        """Give the reserved locate shares the order did not use back to `available`."""
        for code, shares in o.locate_reserved.items():
            inv = self.inventory.get((o.symbol, code))
            if inv is not None and shares > 0:
                inv.available += shares
        o.locate_reserved.clear()

    def _finish(self, o: Order, status: str) -> None:
        """Cancel/expire the remaining quantity."""
        o.canceled = o.quantity - o.executed
        o.status, o.last_updated = status, self.now
        self._release_locate(o)

    def cancel(self, cid: str) -> dict | None:
        o = self.orders.get(cid)
        if o is None:
            return None
        if o.working:
            self._finish(o, "Canceled")
        else:
            o.text, o.last_updated = R130, self.now
        return o.to_json()

    def cancel_all(self, symbol: str | None = None) -> None:
        for o in list(self.orders.values()):
            if o.working and (symbol is None or o.symbol == symbol):
                self._finish(o, "Canceled")

    # ------------------------------------------------------------------ matching
    @staticmethod
    def _fill_price(o: Order, op: float, hi: float, lo: float) -> float | None:
        buy = o.side == "Buy"
        if o.order_type in ("Stop", "StopLimit") and not o.triggered:
            if not (hi >= o.stop_price if buy else lo <= o.stop_price):
                return None
            o.triggered = True
            trigger = max(o.stop_price, op) if buy else min(o.stop_price, op)
            if o.order_type == "Stop":
                return trigger
            op = trigger
        if o.order_type in ("Market", "Stop"):
            return op
        lim = o.limit_price
        if buy:
            return min(lim, op) if lo <= lim else None
        return max(lim, op) if hi >= lim else None

    def _capacity(self, symbol: str, bar_ts: datetime, volume: int) -> int:
        cap = int(volume * self.scenario.participation)
        return max(0, cap - self._used.get((symbol, bar_ts), 0))

    def _match_now(self, o: Order) -> None:
        bar = self.md.last_bar(o.symbol, self.now)
        px = qty = None
        if bar is not None:
            p = bar.close
            px = self._fill_price(o, p, p, p)
            if px is not None:
                qty = min(o.leaves, self._capacity(o.symbol, bar.ts, bar.volume))
        if o.time_in_force == "FillOrKill" and (not qty or qty < o.leaves):
            self._finish(o, "Canceled")
            return
        if px is not None and qty:
            self._fill(o, qty, px, bar.ts)
        if o.time_in_force in ("ImmediateOrCancel", "FillOrKill") and o.working:
            self._finish(o, "Canceled")

    def _fill(self, o: Order, qty: int, px: float, bar_ts: datetime) -> None:
        prev = o.executed
        o.executed += qty
        o.avg_price = (o.avg_price * prev + px * qty) / o.executed
        o.last_price, o.last_qty, o.last_updated = px, qty, self.now
        o.status = "Filled" if o.executed >= o.quantity - o.canceled else "PartiallyFilled"
        if o.order_type == "Market":
            o.limit_price = px
        self.shares_traded += qty
        self._used[(o.symbol, bar_ts)] = self._used.get((o.symbol, bar_ts), 0) + qty
        pos = self.active_position(o.symbol)
        held = pos.shares if pos else 0.0
        if o.side == "Sell":
            self._use_locates(o, int(max(0.0, qty - max(held, 0.0))))
        else:
            self._return_locates(o.symbol, int(min(qty, max(-held, 0.0))))
        self._apply_position(o.symbol, qty if o.side == "Buy" else -qty, px)
        self.fills.append(Fill(o.client_order_id, o.symbol, o.side, qty, px, self.now))

    def _open_position(self, symbol: str, q: float, px: float) -> None:
        self._pos_seq += 1
        pid = f"{126}{self.now:%m%d%H%M%S}{self._pos_seq:06d}"
        self.positions.append(Position(
            account_id=self.account_id, position_id=pid, symbol=symbol,
            side="Long" if q > 0 else "Short", created=self.now, updated=self.now,
            shares=q, price_avg=px, price_open=px, shares_in=abs(q)))

    def _apply_position(self, symbol: str, q: float, px: float) -> None:
        pos = self.active_position(symbol)
        if pos is None:
            self._open_position(symbol, q, px)
            return
        pos.updated = self.now
        if (pos.shares > 0) == (q > 0):
            new = pos.shares + q
            pos.price_avg = (pos.price_avg * abs(pos.shares) + px * abs(q)) / abs(new)
            pos.shares, pos.shares_in = new, pos.shares_in + abs(q)
            return
        closing = min(abs(q), abs(pos.shares))
        sign = 1 if pos.shares > 0 else -1
        pnl = (px - pos.price_avg) * closing * sign
        pos.realized += pnl
        pos.day_realized += pnl
        self.realized += pnl
        self.day_realized += pnl
        pos.shares -= sign * closing
        pos.shares_out += closing
        if pos.shares == 0:
            pos.price_close = px
            self.closed.append(copy.copy(pos))
        rest = abs(q) - closing
        if rest:
            self._open_position(symbol, rest if q > 0 else -rest, px)

    # ------------------------------------------------------------------ clock
    def advance_to(self, t: datetime) -> list[Fill]:
        if t <= self.now:
            raise ValueError(f"time must move forward: {ts(t)} <= {ts(self.now)}")
        start = len(self.fills)
        self._settle_sells()
        for bar in self.md.bars_between(self.now, t):
            self._roll_to(bar.ts)
            self.now = bar.ts
            for o in list(self.orders.values()):
                if o.symbol != bar.symbol or o.status not in ("New", "PartiallyFilled"):
                    continue
                px = self._fill_price(o, bar.open, bar.high, bar.low)
                if px is None:
                    continue
                qty = min(o.leaves, self._capacity(bar.symbol, bar.ts, bar.volume))
                if qty > 0:
                    self._fill(o, qty, px, bar.ts)
            self.scenario.on_bar(self, bar)
        self._roll_to(t)
        self.now = t
        self._expire_locates()
        return self.fills[start:]

    def _roll_to(self, t: datetime) -> None:
        for _ in range(2):  # close of the current session, then possibly a new day
            close = datetime.combine(self.session_date, RTH_CLOSE, tzinfo=ET)
            if t >= close and self._expired_on != self.session_date:
                self.now = max(self.now, close)
                for o in self.orders.values():
                    if o.working and o.time_in_force not in PERSISTENT_TIF:
                        self._finish(o, "Expired")
                self._expired_on = self.session_date
            day = t.astimezone(ET).date()
            if day <= self.session_date:
                return
            self.session_date = day
            self._day_roll()

    def _day_roll(self) -> None:
        self.positions = [p for p in self.positions if p.shares != 0]
        for p in self.positions:
            p.day_overnight = "Overnight"
            p.sod_price = self.mark(p)
            p.day_realized = 0.0
        self.day_realized = 0.0
        self.shares_traded = 0
        self._snapshot_sod()
        self._locate_day_roll()

    # ------------------------------------------------------------------ JSON views
    def account_json(self, detail: bool) -> dict:
        d = {
            "account": self.account_id,
            "accountStatus": "Active",
            "accountType": "Paper",
            "availableCash": money(self.available_cash()),
            "availableCashEMS": 0.0,
            ("bp" if detail else "buyingPower"): money(self.buying_power()),
            "equity": money(self.sod["equity"]),
            "firmEntity": "TZG",
            "firmId": 62,
            "fractional": False,
            "isFutureAccount": False,
            "leverage": float(self.scenario.leverage),
            "maintenanceDeficit": 0.0,
            "marginDeficit": 0.0,
            "marginRatio": 100.0,
            "marginRequirement": money(self.cost_basis()),
            "marginType": "M",
            "optContractsTraded": 0,
            "optLevel": 0,
            "optionCashTotalBalance": 0.0,
            "optionTradingLevel": 0,
            "overnightBp": money(self.sod["overnightBp"]),
            "realized": 0.0,
            "sharesTraded": 0,
            "sodEquity": money(self.sod["equity"]),
            "totalCommissions": 0.0,
            "totalLocateCosts": money(self.locate_costs),
            "tradingSuspended": False,
            "unrealized": money(self.sod["unrealized"]),
            "usedLeverage": round(self.sod["usedLeverage"], 2),
        }
        return dict(sorted(d.items()))

    def _pnl_row(self, p: Position) -> dict:
        px = self.mark(p)
        unreal = (px - p.price_avg) * p.shares
        ref = p.sod_price if (p.day_overnight == "Overnight" and p.sod_price) else p.price_avg
        day_unreal = (px - ref) * p.shares
        basis = abs(p.shares) * p.price_avg or p.shares_in * p.price_open
        pct = lambda v: round(v / basis * 100, 2) if basis else 0.0  # noqa: E731
        return {
            "dayPctPnLMove": pct(p.day_realized + day_unreal),
            "dayRealizedPnl": money(p.day_realized),
            "dayUnrealizedPnL": money(day_unreal),
            "exposure": money(p.shares * px),
            "pctPnLMove": pct(p.realized + unreal),
            "positionId": p.position_id,
            "realizedPnl": money(p.realized),
            "symbol": p.symbol,
            "unrealizedPnL": money(unreal),
        }

    def pnl_json(self) -> dict:
        rows = [self._pnl_row(p) for p in self.positions]
        day_unreal = sum(r["dayUnrealizedPnL"] for r in rows)
        eq = self.equity()
        exposure = self.exposure()
        return {
            "accountValue": money(eq),
            "allowedLeverage": float(self.scenario.leverage),
            "availableCash": money(self.available_cash()),
            "contractsTraded": 0.0,
            "dayPnl": money(self.day_realized + day_unreal),
            "dayRealized": money(self.day_realized),
            "dayUnrealized": money(day_unreal),
            "equityRatio": 1.0,
            "exposure": money(exposure),
            "initialMargin": 0.0,
            "intradayMargin": 0.0,
            "maintenanceMargin": 0.0,
            "marginExcess": 0.0,
            "optionCashUsed": 0.0,
            "pnl": rows,
            "sharesTraded": float(self.shares_traded),
            "totalUnrealized": money(self.unrealized()),
            "usedLeverage": round(exposure / eq, 2) if eq else 0.0,
        }

    # ------------------------------------------------------------------ locates
    def _pool_code(self, symbol: str) -> int:
        return self.scenario.locate_pool_code.get(symbol, 1)

    def _inventories(self, symbol: str) -> list[Inventory]:
        """The symbol's inventory rows, in the order a short uses them (scenario.locate_use_order)."""
        order = self.scenario.locate_use_order
        rows = [i for (sym, _), i in self.inventory.items() if sym == symbol]
        return sorted(rows, key=lambda i: order.index(i.locate_type) if i.locate_type in order else len(order))

    def _inventory_row(self, symbol: str, code: int) -> Inventory:
        return self.inventory.setdefault((symbol, code), Inventory(self.account_id, symbol, code))

    def set_inventory(self, symbol: str, type_name: str, available: int) -> Inventory:
        """Mock control: set the `available` shares of one inventory row (no quote, no cost)."""
        if type_name not in ("Locate", "PreBorrow", "SingleUse"):
            raise ValueError(f"unknown locateType {type_name!r}: use Locate, PreBorrow or SingleUse")
        code = self._pool_code(symbol) if type_name == "Locate" else min(LOCATE_TYPES[type_name])
        inv = self._inventory_row(symbol, code)
        inv.available = available
        return inv

    def _use_locates(self, o: Order, opened: int) -> None:
        """A short fill: move reserved shares to `unavailable`, remembering their type for the cover."""
        for code in list(o.locate_reserved):
            if opened <= 0:
                break
            take = min(opened, o.locate_reserved[code])
            if take <= 0:
                continue
            o.locate_reserved[code] -= take
            opened -= take
            inv = self.inventory.get((o.symbol, code))
            if inv is None:
                continue
            inv.unavailable += take
            inv.used = True
            self.locate_in_use.setdefault(o.symbol, []).append([code, take])

    def _return_locates(self, symbol: str, covered: int) -> None:
        """A cover: Locate/PreBorrow shares go back to `available` (reusable), SingleUse shares are used up."""
        lots = self.locate_in_use.get(symbol, [])
        while covered > 0 and lots:
            code, shares = lots[0]
            take = min(covered, shares)
            covered -= take
            if take == shares:
                lots.pop(0)
            else:
                lots[0][1] -= take
            inv = self.inventory.get((symbol, code))
            if inv is not None:
                inv.unavailable -= take
                if code != SINGLE_USE:
                    inv.available += take

    def _locate_day_roll(self) -> None:
        """Locates are valid for one day: history, offers, inventory and pending sell-backs start empty."""
        self.locates.clear()
        self.inventory.clear()
        self.pending_sells.clear()
        self.locate_in_use.clear()
        self.locate_supply = dict(self.scenario.locate_supply)
        for o in self.orders.values():
            o.locate_reserved.clear()

    def _expire_locates(self) -> None:
        for row in self.locates.values():
            if row.status == 65 and row.offered_at is not None and not row.quoting and \
                    (self.now - row.offered_at).total_seconds() >= self.scenario.locate_offer_seconds:
                row.status, row.text, row.updated = 67, "Quote expired.", self.now

    def _settle_sells(self) -> None:
        for ps in self.pending_sells:
            ps.row.status, ps.row.filled_shares, ps.row.updated = 50, -ps.qty, self.now
            ps.inventory.to_be_sold -= ps.qty
            ps.inventory.sold += ps.qty
            self.locate_credits += ps.inventory.price * ps.qty * self.scenario.locate_sellback_credit
        self.pending_sells.clear()

    @staticmethod
    def _sibling_id(qid: str) -> str:
        return qid[:-3] if qid.endswith(".SU") else qid + ".SU"

    def _offered(self, row: LocateRow) -> bool:
        return row.status == 65 and not row.quoting

    def _quote_refusal(self, symbol: str, qty: int) -> tuple[int, int, str] | None:
        """(status, locateError, text) when the whole quote is refused before a pool is picked."""
        queued = self.next_locate_errors.get(symbol)
        if queued:
            return queued.pop(0)
        if qty % 100:
            return 56, 67, "R67: You can only offer for credit in lots of 100"
        if not self.scenario.hard_to_borrow(symbol):
            return 56, 56, "R56: This symbol is Easy to Borrow."
        px = self.price(symbol)
        if px is not None and px < 0.5:
            return 56, 40, "R40: Shorting stocks under $0.500000 is not allowed"
        if any(r.symbol == symbol and (r.quoting or r.status == 65) for r in self.locates.values()):
            return 56, 14, "14 Locate is already pending."
        if symbol not in self.scenario.locate_price:
            return 56, 11, NO_SHARES
        supply = self.locate_supply.get(symbol)
        if supply is not None and qty > supply:
            return 56, 11, NO_SHARES
        return None

    def _type_price(self, symbol: str, code: int) -> float | None:
        price = self.scenario.locate_price[symbol]
        if isinstance(price, dict):
            price = price.get(TYPE_NAMES[code])
        return None if price is None else float(price)

    def locate_quote(self, symbol: str, qty: int, qid: str) -> bool:
        """Returns False when quoteReqID was already used."""
        if qid in self.locates or self._sibling_id(qid) in self.locates:
            return False
        self._expire_locates()
        refusal = self._quote_refusal(symbol, qty)
        if refusal:
            row = LocateRow(self.account_id, symbol, qid, qty, status=refusal[0], created=self.now,
                            updated=self.now, locate_type=0, error=refusal[1], text=refusal[2])
            rows = [row]
        elif symbol in self.scenario.regsho:
            rows = [self._quote_row(symbol, qty, qid, 3), self._quote_row(symbol, qty, qid + ".SU", SINGLE_USE)]
            rows[1].hidden_reads = self.scenario.locate_sibling_polls
        else:
            rows = [self._quote_row(symbol, qty, qid, self._pool_code(symbol))]
        for row in rows:
            row.quoting_reads = self.scenario.locate_quote_polls
            if self._offered(row):
                row.offered_at = self.now
            self.locates[row.quote_req_id] = row
        return True

    def _quote_row(self, symbol: str, qty: int, qid: str, code: int) -> LocateRow:
        row = LocateRow(self.account_id, symbol, qid, qty, status=54, created=self.now, updated=self.now,
                        locate_type=code, error=11, text=NO_SHARES)
        price = self._type_price(symbol, code)
        if price is not None:
            row.status, row.error, row.text, row.price = 65, 0, "", price
        return row

    def locate_accept(self, qid: str) -> None:
        self._expire_locates()
        row = self.locates.get(qid)
        if row is None or not self._offered(row) or row.symbol in self.scenario.locate_accept_ignored:
            return
        row.status, row.filled_shares, row.updated = 50, row.locate_shares, self.now
        inv = self._inventory_row(row.symbol, row.locate_type)
        inv.available += row.locate_shares
        inv.price = row.price
        self.locate_costs += row.price * row.locate_shares
        if row.symbol in self.locate_supply:
            self.locate_supply[row.symbol] -= row.locate_shares
        sibling = self.locates.get(self._sibling_id(qid))
        if sibling is not None and sibling.status == 65:
            sibling.status, sibling.text, sibling.updated = 67, "Quote expired.", self.now
            sibling.quoting_reads = sibling.hidden_reads = 0

    def locate_cancel(self, qid: str) -> None:
        self._expire_locates()
        row = self.locates.get(qid)
        if row is None:
            return
        if row.status == 65:
            row.status, row.error, row.text, row.updated = 52, 0, "Quote canceled.", self.now
            row.quoting_reads = row.hidden_reads = 0
        for ps in list(self.pending_sells):
            if ps.row is row:
                row.status, row.updated = 52, self.now
                ps.inventory.to_be_sold -= ps.qty
                ps.inventory.available += ps.qty
                self.pending_sells.remove(ps)

    def locate_sell(self, symbol: str, qty: int, locate_type: str, qid: str) -> bool:
        if qid in self.locates:
            return False
        codes = LOCATE_TYPES[locate_type]
        inv = next((i for i in self._inventories(symbol) if i.locate_type in codes), None)
        row = LocateRow(self.account_id, symbol, qid, -qty, status=48, created=self.now, updated=self.now,
                        locate_type=inv.locate_type if inv else min(codes))
        if qty % 100:
            row.status, row.error, row.text = 56, 67, "R67: You can only offer for credit in lots of 100"
        elif inv is None or inv.available < qty:
            row.status, row.error, row.text = 56, 16, "16 Insufficient shares."
        elif inv.locate_type == SINGLE_USE and inv.used:
            row.status, row.error, row.text = 56, 16, SINGLE_USE_USED
        else:
            inv.available -= qty
            inv.to_be_sold += qty
            self.pending_sells.append(PendingSell(row, inv, qty))
        self.locates[qid] = row
        return True

    def locate_history(self) -> list[dict]:
        self._expire_locates()
        out = []
        for row in self.locates.values():
            if row.hidden_reads:
                row.hidden_reads -= 1
                continue
            out.append(row.to_json())
            if row.quoting_reads:
                row.quoting_reads -= 1
                if not row.quoting_reads and row.status == 65:
                    row.offered_at = row.updated = self.now
        return out

    def locate_inventory(self) -> list[dict]:
        return [i.to_json() for i in self.inventory.values()]

    def locate_state(self) -> dict:
        return {
            "inventory": [i.to_json() | {"used": i.used} for i in self.inventory.values()],
            "openQuotes": [r.quote_req_id for r in self.locates.values() if r.quoting or r.status == 65],
            "inUse": {s: lots for s, lots in self.locate_in_use.items() if lots},
            "supply": self.locate_supply,
            "pendingSellBacks": [ps.row.quote_req_id for ps in self.pending_sells],
            "nextErrors": {s: q for s, q in self.next_locate_errors.items() if q},
            "locateCosts": money(self.locate_costs),
            "locateCredits": money(self.locate_credits),
        }

    # ------------------------------------------------------------------ debug
    def state_json(self) -> dict:
        return {
            "accountId": self.account_id,
            "scenario": self.scenario.name,
            "now": ts(self.now),
            "prices": {s: self.md.price(s, self.now) for s in self.md.symbols},
            "workingOrders": [o.client_order_id for o in self.orders.values() if o.working],
            "terminalOrders": sum(o.status in TERMINAL for o in self.orders.values()),
            "locates": self.locate_state(),
        }
