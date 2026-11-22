# Design: locate support in the TradeZero connector

Status: **implemented** (tz-mock, then the connector; live checklist §9 still open). Date: 2026-11-22.

What changed during the implementation: the settings are named `tradezero-locate-timeout-seconds` and `tradezero-locate-poll-ms`; `TradeZeroLocates.GetHistory()` returns `TradeZeroLocateRecord` rows; a 54 row **with** a `locateError` counts as a refusal (only 54 without an error, and 81, are "still working"); a handler that throws or returns a foreign object also emits `LocateHandlerError`; the zero-crossing opening leg is checked against the inventory and ends `Canceled` "short part was not placed" when it falls short.

## 1. Where we are today

- `TradeZeroShortableProvider` returns `0` for a hard-to-borrow (HTB) symbol, so Lean blocks the short before it reaches the brokerage.
- `TradeZeroBrokerage.Orders.cs` has three more HTB guards: `PlaceOrder`, `UpdateOrder` (replacement leg) and `PlacePendingLeg` (the opening leg of a zero-crossing order). Each one refuses with "locates not supported".
- `TradeZeroRestClient` only calls `GET /is-easy-to-borrow`. It has no locate endpoints.
- tz-mock already follows the **live** locate workflow: quote, offer (65), accept (50), cancel (52), expiry (67), sell-back (48 → 50), inventory and history, and an HTB short that needs inventory (see `tz-mock/docs/api-notes.md`). The gaps are listed in §7.
- Paper treats every symbol as easy to borrow and has no locate inventory. Locates can only be checked end to end on **live** or on **tz-mock**.

## 2. Goals

1. Short an HTB symbol by accepting a locate offer through a **callback on `TradeZeroOrderProperties`**.
2. **No callback**: a short that needs a locate the account does not already hold is **refused** (Lean `Invalid`).
3. **HTB short not a multiple of 100**: emit a **warning** and go ahead anyway. A property turns the warning off. It is shown by default.
4. An algorithm API to **read the located shares available but not yet shorted**, and to **sell locates back** to the broker.
5. Every scenario can be tested against **tz-mock**.

6. When TradeZero offers **several locate types** for one quote (Reg SHO threshold symbols: PreBorrow and SingleUse), the algorithm **chooses which one** to accept.

Out of scope: automatic sell-back at the close, locates for options.

### 2.1 How TradeZero offers locates (Short Locates guide, developer.tradezero.com/docs/documentation/locates)

- One `POST /locates/quote` produces **one or two history rows**:
  - an ordinary HTB symbol gives one row, `locateType` 1 or 2 (**Locate**);
  - a **Reg SHO threshold** symbol gives two rows: `quoteReqID` with `locateType: 3` (**PreBorrow**) and `quoteReqID + ".SU"` with `locateType: 4` (**SingleUse**). Each has its own price and status. One side can be refused while the other is offered: the base row stays at 54/11 "No shares available" while `.SU` reaches 65.
- **Accept takes only `quoteReqID`.** You choose by sending the base id (PreBorrow) or `<id>.SU` (SingleUse). Accepting one row moves the other to 67 "Quote expired.". An offer lasts 30 s.
- The types behave differently:

| Type | Codes | Reusable after a cover | Sell-back |
|---|---|---|---|
| Locate | 1, 2 | yes, the same day | the unused part, even after covers |
| PreBorrow | 3 | yes, the same day | the unused part, even after covers |
| SingleUse | 4 | **no**, used up by the first short | **only if it was never used** |

- Inventory has one row **per (symbol, type)**, and `/sell` needs a `locateType`.
- Quote quantities must be **multiples of 100**. 150 gets a 56 row with `R67`, and below 100 returns a 400.

So the choice is between offers that differ in **price** and in **what you can do with them later**. A one-row yes/no handler cannot express that.

## 3. Order-level API (`TradeZeroOrderProperties`)

```csharp
public class TradeZeroOrderProperties : OrderProperties
{
    // ... existing members ...

    /// Called when this order needs a locate the account does not hold yet, with every offer TradeZero made
    /// for the quote (1 offer, or 2 for a Reg SHO threshold symbol: PreBorrow and SingleUse).
    /// Return the offer to accept (its cost is charged right away), or null to decline them all.
    /// Null handler: a short that needs a new locate is refused.
    [JsonIgnore]
    public Func<TradeZeroLocateQuote, TradeZeroLocateOffer> LocateHandler { get; set; }

    /// Don't warn when an HTB short is not a multiple of 100 shares (default false: warn)
    public bool SuppressOddLotLocateWarning { get; set; }
}
```

- `[JsonIgnore]`: Lean serializes order properties into results and the order store, and a delegate must not go there. After a restart, a recovered open order has no handler. That causes no problem, because recovered orders are already placed.
- `Clone()` (MemberwiseClone) shares the delegate. That is the behaviour we want.

#### Worked example (used in every field description below)

The account holds **50 located PreBorrow shares** of the Reg SHO symbol `REGSHO` and is **long 100**. The algorithm sends `market_order("REGSHO", -350)`:
- 100 shares close the long, and **250 are sold short**;
- 50 of the 250 are covered by the inventory, so **200 more** are needed (already a multiple of 100);
- the connector requests a quote for 200 shares with id `LQ1122093000.7`;
- TradeZero answers with two history rows: `LQ1122093000.7` (PreBorrow, $0.03/share, status 65) and `LQ1122093000.7.SU` (SingleUse, $0.01/share, status 65).

The handler is called with:

| Field | Value |
|---|---|
| `Symbol` | REGSHO |
| `LeanOrderId` | the order's id, e.g. 12 |
| `OrderQuantity` | -350 |
| `ShortQuantity` | 250 |
| `AlreadyLocated` | 50 |
| `RequestedQuantity` | 200 |
| `QuoteReqId` | `LQ1122093000.7` |
| `Offers[0]` | SingleUse, id `LQ1122093000.7.SU`, 200 @ 0.01 = $2.00, covers the order, not reusable, no sell-back after use |
| `Offers[1]` | PreBorrow, id `LQ1122093000.7`, 200 @ 0.03 = $6.00, covers the order, reusable, sell-back after use |

#### Types

```csharp
/// <summary>
/// Kind of locate, from the history and inventory field <c>locateType</c>. It decides what you can do with the shares
/// after a short is covered, and whether they can be sold back.
/// </summary>
public enum TradeZeroLocateType
{
    /// <summary>
    /// <c>locateType</c> 0. Only on refused rows, where TradeZero picked no pool, so you only see it in
    /// <c>TradeZeroLocates.GetHistory()</c>. Never on an offer or in inventory.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// <c>locateType</c> 1 or 2 (the same product from two TradeZero pools; the connector maps both here).
    /// The only type offered for an ordinary hard-to-borrow symbol, which is not on the Reg SHO threshold list.
    /// Reusable for the rest of the day: covering a short gives the shares back to <c>available</c>.
    /// The unused part can be sold back, even after covers. <c>/sell</c> string: "Locate".
    /// </summary>
    Locate = 1,

    /// <summary>
    /// <c>locateType</c> 3. Offered for Reg SHO threshold symbols, on the quote's base id.
    /// It behaves like <see cref="Locate"/>: reusable for the day, and the unused part can be sold back.
    /// It usually costs more than SingleUse. <c>/sell</c> string: "PreBorrow".
    /// </summary>
    PreBorrow = 3,

    /// <summary>
    /// <c>locateType</c> 4. Offered for Reg SHO threshold symbols, on the id <c>QuoteReqId + ".SU"</c>.
    /// Used up by the first short sale: covering does NOT give the shares back, so shorting again needs a new locate.
    /// Can only be sold back if no share was ever used to open a short. <c>/sell</c> string: "SingleUse".
    /// </summary>
    SingleUse = 4
}

/// <summary>
/// One locate quote request and every answer TradeZero gave to it. This is what <c>LocateHandler</c> receives.
/// It is built once all rows of the quote have settled (§4.2) and never changes afterwards.
/// </summary>
public sealed class TradeZeroLocateQuote
{
    /// <summary>Lean symbol the locate is for (REGSHO in the example)</summary>
    public Symbol Symbol { get; }

    /// <summary>
    /// Id of the Lean order that needs the locate (12 in the example).
    /// Null when the quote comes from <c>TradeZeroLocates.Locate</c>, which places no order.
    /// </summary>
    public int? LeanOrderId { get; }

    /// <summary>
    /// The Lean order's full quantity, signed as in Lean (negative = sell), including any part that closes a long:
    /// -350 in the example. 0 when there is no order.
    /// </summary>
    public decimal OrderQuantity { get; }

    /// <summary>
    /// Shares of this order that open or increase a short position, i.e. the part that needs located shares:
    /// 250 in the example (the other 100 close the long). Always positive. 0 when there is no order.
    /// For an update this is the short part of the replacement only.
    /// </summary>
    public long ShortQuantity { get; }

    /// <summary>
    /// Located shares the account already held and could short with, read just before the quote:
    /// the sum of <c>available</c> over every type for this symbol (50 PreBorrow in the example).
    /// It excludes shares in use by shorts and shares being sold back.
    /// The connector uses these first. <c>RequestedQuantity</c> covers only the rest.
    /// </summary>
    public long AlreadyLocated { get; }

    /// <summary>
    /// Shares the connector asked TradeZero to locate (200 in the example).
    /// With an order: ShortQuantity − AlreadyLocated, rounded UP to a multiple of 100, because TradeZero only quotes
    /// lots of 100. The extra shares from rounding are paid for and remain unused (the odd-lot warning).
    /// Without an order: the quantity passed to <c>TradeZeroLocates.Locate</c>, which must be a multiple of 100.
    /// </summary>
    public long RequestedQuantity { get; }

    /// <summary>
    /// The quote's base id (the TradeZero field <c>quoteReqID</c>), generated by the connector: "LQ{run}.{seq}".
    /// Every history row of this quote has this id (Locate or PreBorrow row) or this id + ".SU" (SingleUse row).
    /// Use it to find the quote in <c>TradeZeroLocates.GetHistory()</c> and in the API log.
    /// To accept, return an offer: don't build an id yourself.
    /// </summary>
    public string QuoteReqId { get; }

    /// <summary>
    /// The offers you can accept: rows in status 65 (Offered). One for an ordinary hard-to-borrow symbol, one or two
    /// for a Reg SHO symbol. Never empty (the handler is not called without an offer).
    /// Sorted by PricePerShare, cheapest first. On equal prices, reusable types (Locate, PreBorrow) come before SingleUse.
    /// Return one of these objects from the handler to accept it, or null to decline them all.
    /// </summary>
    public IReadOnlyList<TradeZeroLocateOffer> Offers { get; }
}

/// <summary>
/// One offer TradeZero made for the quote: a history row in status 65 (Offered).
/// Accepting it charges <see cref="TotalCost"/> immediately and adds <see cref="Quantity"/> shares of
/// <see cref="Type"/> to the inventory.
/// </summary>
public sealed class TradeZeroLocateOffer
{
    /// <summary>
    /// Kind of locate offered: Locate, PreBorrow or SingleUse (never Unknown). It decides the two flags below.
    /// </summary>
    public TradeZeroLocateType Type { get; }

    /// <summary>
    /// Id of this row (history field <c>quoteReqID</c>), the one the connector sends to <c>/accept</c> if you return
    /// this offer: the quote's base id for Locate/PreBorrow, base id + ".SU" for SingleUse.
    /// Informational: you never send it yourself.
    /// </summary>
    public string QuoteReqId { get; }

    /// <summary>
    /// Shares offered (history field <c>locateShares</c>). Normally equal to RequestedQuantity.
    /// It could be smaller if TradeZero ever makes partial offers (not documented, §9).
    /// </summary>
    public long Quantity { get; }

    /// <summary>
    /// True when AlreadyLocated + Quantity ≥ ShortQuantity: accepting it lets the order be sent.
    /// If you accept an offer where this is false, the order is still refused after the accept ("not enough shares
    /// located"), and the shares you bought stay in the inventory. Always true for <c>TradeZeroLocates.Locate</c>.
    /// </summary>
    public bool CoversOrder { get; }

    /// <summary>
    /// Price per located share in USD (history field <c>locatePrice</c>): 0.01 for SingleUse in the example
    /// </summary>
    public decimal PricePerShare { get; }

    /// <summary>
    /// What accepting costs, in USD: PricePerShare × Quantity ($2.00 for SingleUse in the example).
    /// TradeZero deducts it from cash at the accept, even if the order is later canceled or never fills.
    /// The connector adds it to the fee of the order's first fill (§4.6).
    /// </summary>
    public decimal TotalCost { get; }

    /// <summary>
    /// True for Locate and PreBorrow: after you cover the short, the shares go back to <c>available</c> and can be
    /// shorted again the same day at no extra cost. False for SingleUse: one short sale uses them up.
    /// Prefer true if the algorithm may short this symbol again today.
    /// </summary>
    public bool ReusableAfterCover { get; }

    /// <summary>
    /// True for Locate and PreBorrow: the unused shares can be sold back for credit (<c>TradeZeroLocates.SellBack</c>),
    /// even after some were shorted and covered.
    /// False for SingleUse: it can only be sold back if no share was ever used to open a short.
    /// </summary>
    public bool SellBackAfterUse { get; }

    /// <summary>
    /// When the row reached status 65, in UTC (history field <c>updatedDate</c>; the local clock when it is missing)
    /// </summary>
    public DateTime OfferedUtc { get; }

    /// <summary>
    /// OfferedUtc + 30 s, when TradeZero expires the offer (status 67). The handler must return before then: an accept
    /// sent later fails and the order is refused "locate offer expired". Computed from TradeZero's 30 s rule, so it may
    /// be off by the clock difference between this machine and TradeZero.
    /// </summary>
    public DateTime ExpiresUtc { get; }
}
```

The handler is **called only when at least one offer exists**, and it only sees offers. Rows TradeZero refused (e.g. PreBorrow refused with "11 No shares available" while SingleUse is offered) are not passed to it. Their texts go into a `LocateRefused` message and the API log, and stay readable in `TradeZeroLocates.GetHistory()`. When every row is refused, the handler is never called, and the order is `Invalid` with the broker texts.

Python:

```python
from QuantConnect.Brokerages.TradeZero import TradeZeroOrderProperties, TradeZeroLocateType

def choose_locate(quote):
    for o in quote.offers:
        self.debug(f"{quote.symbol} {o.type} {o.quantity} @ {o.price_per_share} = {o.total_cost}")
    # day trader who may re-short after a cover: prefer reusable offers, then the cheapest
    reusable = [o for o in quote.offers if o.reusable_after_cover and o.price_per_share <= 0.05]
    if reusable:
        return reusable[0]
    cheap = [o for o in quote.offers if o.price_per_share <= 0.02]
    return cheap[0] if cheap else None          # None = decline everything

props = TradeZeroOrderProperties()
props.locate_handler = choose_locate              # pythonnet converts the callable into Func<,>
props.suppress_odd_lot_locate_warning = False     # default
self.market_order("HTBX", -150, order_properties=props)

# simplest handlers
props.locate_handler = lambda q: q.offers[0]                                                   # cheapest, whatever the type
props.locate_handler = lambda q: next((o for o in q.offers if o.type == TradeZeroLocateType.PRE_BORROW), None)
```

Handler contract (it goes in the XML doc and the README):
- It runs on a **connector thread**, not on the algorithm thread. pythonnet takes the GIL. Keep it short and pure: read the offer, compare it with a limit, return. It must not place orders.
- Offers expire 30 s after they reach 65. The connector waits for the handler with no timeout of its own, but an accept that arrives after expiry fails (§4.1, "offer expired").
- The handler must return **one of the objects in `quote.offers`** or null. An exception, or an object that is not in the list, counts as **decline** and is logged with an error message.
- The handler is called once per quote. There is no "try SingleUse if PreBorrow fails": once one row is accepted TradeZero expires the other, so falling back would need a new quote.

A possible later addition, not part of this proposal: a no-code policy such as `LocatePolicy = { MaxPricePerShare = 0.05m, PreferredTypes = [PreBorrow, Locate, SingleUse] }` for the most common handlers.

## 4. Connector behaviour

### 4.1 Decision flow for an order with a short part

`PlanLegs` already gives the short part (a `Sell`/`Open` leg). In `PlaceOrder`:

```
short part == 0 ─────────────────────────────────────────────► submit as today
ETB check
  ├─ true (ETB) ─────────────────────────────────────────────► submit as today
  ├─ null (check failed) ────────────────────────────────────► submit as today (the broker decides)
  └─ false (HTB)
       ├─ short % 100 != 0 and !SuppressOddLotLocateWarning ─► Warning "LocateOddLot" (continue)
       ├─ need = short − inventory.available   (fresh GET /locates/inventory)
       ├─ need <= 0 ─────────────────────────────────────────► submit (uses existing locates, no handler call)
       ├─ no LocateHandler ──────────────────────────────────► Invalid: "HTBX is hard to borrow: shorting 150
       │                                                         needs 100 more located shares (50 held).
       │                                                         Set TradeZeroOrderProperties.LocateHandler."
       └─ locate workflow (§4.2) for ceil(need/100)·100 shares → collect the rows (1, or base + ".SU")
            ├─ no row offered (all 54/56) ───────────────────► Invalid with every broker text (11, 14, 40, R06…)
            │     └─ R56 "is Easy to Borrow" (flipped) ──────► invalidate ETB cache; submit
            ├─ handler returns an offer → accept its quoteReqID
            │     ├─ chosen row filled (50) ─────────────────► submit (the sibling row goes to 67 by itself)
            │     └─ expired (67) / not filled in time ──────► Invalid "locate offer expired"
            ├─ handler returns null (declined) ──────────────► cancel every offered row; Invalid "locate declined (…)"
            └─ timeout / HTTP error ─────────────────────────► cancel the offered rows; Invalid
```

Odd-lot warning text (one per order): `HTBX: short of 150 shares is not a multiple of 100. Locates are sold, and can only be sold back, in lots of 100, so 50 located shares will stay unused. Set SuppressOddLotLocateWarning to hide this.` The warning follows the user's rule literally (HTB and an odd short quantity), even when existing inventory covers the order: the leftover odd shares cannot be sold back either.

### 4.2 Locate workflow (asynchronous)

A quote needs several round trips: POST quote, poll the history until 65, the handler, POST accept, poll until 50. Running them inside `PlaceOrder` would block Lean's transaction thread and hold `_orderLock`, which also stops the poll loop, for several seconds. Instead:

1. `PlaceOrder` checks the order, then creates the `TrackedOrder` with a new state **`AwaitingLocate`**. It emits `Submitted` ("TradeZero order waiting for a locate") and returns `true`.
2. A worker (a `Task`, one per order, serialized **per symbol** by a `SemaphoreSlim`) runs the workflow **without** holding `_orderLock`:
   - `quoteReqID` = `LQ{runTag}.{seq}` (unique per run, so no R114);
   - `POST /accounts/locates/quote` → poll `GET /locates/history` (every 250 ms, rate-gated) for the rows `quoteReqID` **and** `quoteReqID.SU`;
   - **the quote is complete** when every row seen has left 81 (Quoting) and 54 (Pending), **and** one more poll has gone by after the first row settled, so a `.SU` sibling that appears a little later is not missed. Whether both rows appear together is to be checked on live (§9). The settle poll costs about 250 ms;
   - at least one row at 65 → build `TradeZeroLocateQuote` → invoke the handler → `POST accept` with the chosen offer's `quoteReqID` → poll until that row is 50 → re-read the inventory;
   - accept always answers `locateAcceptSent`, even for an unknown id, so the history row is the only proof. Re-sending an accept is documented as safe, which allows one retry after an HTTP error;
   - deadline: the new setting `tradezero-locate-timeout-seconds` (default 20 s) for the whole workflow, not counting the time spent in the handler.
3. Then it takes `_orderLock` and, unless the order was canceled meanwhile, calls `SubmitLeg` for the first leg exactly as `PlaceOrder` does today. A failure ends the order `Invalid`, through `EmitEvent`, which runs after `Submitted`.

While the order is `AwaitingLocate`:
- **CancelOrder**: marks it Done and emits `Canceled`. If the quote is still Offered, the worker sends `DELETE …/cancel`. A locate that was **already accepted stays in inventory**: the connector reports it with a `LocateUnused` message and never sells it back on its own.
- **UpdateOrder**: refused (`UpdateRejected: waiting for a locate`).
- **Restart**: nothing has reached the broker yet, so nothing needs recovering. A locate bought before the crash is visible in the inventory.

### 4.3 Locate quantity

- `need = short − available`, rounded **up to a multiple of 100**. The guide confirms quotes must be multiples of 100, with a minimum of 100. The rounding is the reason for the odd-lot warning.
- `available` is the sum over every type's inventory row for the symbol. Which row TradeZero uses when a short could take shares from more than one (e.g. PreBorrow and SingleUse) is not documented (§9). The connector does not choose, because orders have no locate-type field.
- `available` comes from a fresh inventory read inside the per-symbol semaphore, so two orders for the same symbol never count the same shares. The broker reserves inventory when the order is placed (mock behaviour, to verify on live).

### 4.4 Other paths that place a short leg

| Path | Behaviour |
|---|---|
| **Zero-crossing order** (long 50, sell 250) | The locate is obtained **before the closing leg**, so the position is never closed and then the short refused. If another order or a manual trade uses the inventory between the two legs, the broker rejects the opening leg, and the existing "Position closed, but the opening part was rejected" path handles it. |
| **UpdateOrder** whose replacement needs more locate than held | Refused before anything is canceled (`UpdateRejected`): "needs N more located shares; cancel and re-place, or call `TradeZeroLocates.Locate` first". UpdateOrder is synchronous, so it cannot run the workflow. |
| **UpdateOrder** covered by inventory | As today. |
| `PlacePendingLeg` | Checks the inventory instead of ETB alone. If it is short, the order ends `Canceled` as today, with a locate message. |

### 4.5 Lean pre-checks: shortable provider

Lean's `Shortable()` only sees the symbol, not the order properties. If the provider keeps returning `0` for HTB symbols, the handler is never reached. Proposal:

- HTB → **`null`** (no limit at the Lean level; the connector decides, with the clear messages of §4.1). ETB → `null`, as today.
- Consequence: `self.shortable("HTBX", -100)` becomes `True`. The README must say so and point to `TradeZeroLocates.is_easy_to_borrow()` / `get_available()` for an exact check.
- Considered and rejected: returning the located inventory. Lean would then block every short above the inventory, which is exactly the case the handler is for.

### 4.6 Locate costs in Lean

TradeZero deducts `price × shares` from cash when the locate is accepted (`totalLocateCosts`). Lean should see it:
- **Recommended**: add the cost to the `OrderFee` of the order's **first fill** (in `EmitFill`, next to `FirstFillCharged`). It is a real cost of that trade, and Lean's P&L then matches TradeZero's.
- If the order never fills, the cost is reported in a `LocatePurchased` message only. Lean's cash catches up at the next cash sync, because Lean cash is computed from `accountValue`.
- Locates bought with `TradeZeroLocates.Locate` (§5) and sell-back credits: message only, plus the cash sync.

### 4.7 Messages and logging

`BrokerageMessageEvent` (Warning or Information): `LocateOddLot`, `LocatePurchased` (shares, price, cost, order), `LocateDeclined`, `LocateRefused` (one per refused row: type, `locateStatus` 54/56, `locateError` and text; codes seen in the guide: 1 system error, 4 restricted R06, 11 no shares, 14 quote already open, 40 under $0.50, 56 easy to borrow R56, 67 not in lots of 100 R67), `LocateUnused` (an accepted locate whose order was canceled), `LocateSoldBack`.
API log (`kind:"lean"`): `locate quote`, `locate offer`, `locate handler` (decision, ms), `locate accept`, `locate filled`, `locate sell-back`.

## 5. Locate management API (`TradeZeroLocates`)

A static facade on the connected brokerage, in the same pattern as `TradeZeroAccount` / `TradeZeroShortableProvider.SetResolver`. Without a connected brokerage (backtests, research), the reads return empty values and the actions return a `Rejected` result with "not connected".

```csharp
public static class TradeZeroLocates
{
    // ---- read
    /// Located shares available to short now, summed over all types: neither shorted, reserved by a working
    /// order, nor being sold back. With a type: that type's row only.
    public static long GetAvailable(Symbol symbol, TradeZeroLocateType? type = null);
    public static IReadOnlyList<TradeZeroLocateInventory> GetInventory(Symbol symbol);  // one row per type, empty when none
    public static IReadOnlyList<TradeZeroLocateInventory> GetInventory();               // all symbols, all types
    public static IReadOnlyList<TradeZeroLocateRecord> GetHistory();                    // today's quotes / accepts / sell-backs (incl. ".SU" rows)
    public static bool? IsEasyToBorrow(Symbol symbol);                                  // cached per ET day; null = unknown

    // ---- act (blocking, bounded by tradezero-locate-timeout-seconds)
    /// Sell located shares of one type back for credit. quantity: multiple of 100, at most GetAvailable(symbol, type).
    /// type may be omitted only when the symbol has a single inventory row; otherwise Rejected "several locate types held".
    public static TradeZeroLocateResult SellBack(Symbol symbol, long quantity, TradeZeroLocateType? type = null);
    /// For each type held: sell back floor(available / 100) · 100. One result per type (a SingleUse row that was
    /// used is skipped with a Rejected result, as TradeZero only credits SingleUse that was never used)
    public static IReadOnlyList<TradeZeroLocateResult> SellBackUnused(Symbol symbol);
    /// Buy a locate without an order (e.g. before the open, or ahead of an update). Same handler as §3
    public static TradeZeroLocateResult Locate(Symbol symbol, long quantity, Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler);
}

public sealed class TradeZeroLocateInventory
{
    public Symbol Symbol { get; }
    public TradeZeroLocateType Type { get; }   // also tells whether the shares are reusable / sellable after use
    public long Available { get; }     // located, not yet shorted
    public long Unavailable { get; }   // in use by short positions / working orders
    public long ToBeSold { get; }      // sell-back pending
    public long Sold { get; }          // sold back today
    public DateTime UpdatedUtc { get; }
}

public enum TradeZeroLocateStatus { Pending, Completed, Declined, Rejected, Expired, TimedOut }

public sealed class TradeZeroLocateResult
{
    public TradeZeroLocateStatus Status { get; }
    public TradeZeroLocateType Type { get; }  // type bought or sold back
    public long Quantity { get; }          // shares located, or sold back
    public decimal PricePerShare { get; }  // locate price (Locate() only)
    public string QuoteReqId { get; }
    public string Message { get; }         // broker text when rejected
    public bool IsSuccess => Status is TradeZeroLocateStatus.Completed or TradeZeroLocateStatus.Pending;
}
```

Python:

```python
from QuantConnect.Brokerages.TradeZero import TradeZeroLocates

free = TradeZeroLocates.get_available(self.htbx)       # all types; 0 in backtests
for inv in TradeZeroLocates.get_inventory(self.htbx):
    self.debug(f"{inv.type}: avail={inv.available} in use={inv.unavailable} selling={inv.to_be_sold} sold={inv.sold}")

for r in TradeZeroLocates.sell_back_unused(self.htbx): # e.g. at 15:50 ET
    self.debug(f"{r.type} {r.status} {r.quantity} {r.message}")

TradeZeroLocates.sell_back(self.htbx, 200, TradeZeroLocateType.PRE_BORROW)
```

Behaviour:
- **Reads** always do a fresh `GET /locates/inventory` (rate-gated, about 0.5 s) and never use a cache. The value is used to decide trades, so a stale number would cause rejects. A short cache (e.g. 1 s) can be added if algorithms call it in a loop.
- **SellBack** checks the input before sending: a multiple of 100, at most that type's `available`, and a type given when several are held. These are the broker's R67 and "16 Insufficient shares" errors, raised early with a clear message. It sends `locateType` as the `/sell` string (`Locate`, `PreBorrow`, `SingleUse`), then polls history until 48 (→ `Pending`) or 56 (→ `Rejected`, broker text) and returns. Settlement 48 → 50 is asynchronous: `Pending` means it was accepted. `GetHistory` / `GetInventory().Sold` show the result.
- Every action takes the **same per-symbol semaphore** as the order workflow, so a sell-back cannot race a short that is about to use the same shares.
- **Paper**: inventory is empty, `IsEasyToBorrow` is always true, and SellBack is refused by the broker. Everything is documented.
- These are explicit algorithm calls, so they block the algorithm thread. That is acceptable, and the timeout bounds it.

## 6. Low-level additions

`TradeZeroRestClient` (new rate gate `"locates"`):

| Method | Endpoint |
|---|---|
| `RequestLocateQuote(account, symbol, qty, quoteReqId)` | `POST /accounts/locates/quote` |
| `AcceptLocate(account, quoteReqId)` | `POST /accounts/locates/accept` |
| `CancelLocate(account, quoteReqId)` | `DELETE /accounts/locates/cancel/accounts/{a}/quoteReqID/{q}` |
| `SellLocate(account, symbol, qty, locateType, quoteReqId)` | `POST /accounts/locates/sell` |
| `GetLocateHistory(account)` | `GET /accounts/{a}/locates/history` |
| `GetLocateInventory(account)` | `GET /accounts/{a}/locates/inventory` (accepts both `{"locateInventory":[…]}` and a bare list) |

Models: `TzLocateHistoryRow`, `TzLocateInventoryRow`, and `TzLocateStatus` constants (48 New/sell-back queued, 50 Filled, 52 Canceled, 54 Pending, 56 Rejected, 65 Offered, 67 Expired, 81 Quoting). `locateType` maps 1 and 2 → `Locate`, 3 → `PreBorrow`, 4 → `SingleUse`, 0 → `Unknown`. The `preBorrow` boolean is ignored, as the guide recommends. A history row belongs to quote `q` when its id is `q` or `q.SU`.
Settings: `tradezero-locate-timeout-seconds` (default 20) and `tradezero-locate-poll-ms` (default 250).
Internals: a `TradeZeroLocateManager` class owns the workflow, the per-symbol semaphores and the quote id sequence. The brokerage and `TradeZeroLocates` both use it, which keeps `TradeZeroBrokerage.Orders.cs` readable.

## 7. tz-mock changes

The mock already covers quote / offer / accept / cancel / expiry / sell-back, R40 / R56 / 11 / 14 / R67 / 16, and an HTB short that reserves inventory. What it still needs so that every connector path can be tested:

| # | Change | Why | Scenario param / endpoint |
|---|---|---|---|
| M1 | **Quoting delay**: the row stays at 81 `Quoting` for N history reads before moving to its outcome | The connector must poll; today 65 comes back instantly | `locate_quote_polls: int = 0` |
| M2 | **Limited supply**: a quote above the remaining supply returns 11; accepted shares reduce the supply | Test "partial" supply and "No shares" after an earlier locate | `locate_supply: dict[str,int] = {}` (absent = unlimited) |
| M3 | **Quote lot rule** (documented): a quantity that is not a multiple of 100 → 200, then a 56 row with `locateError: 67`, `R67: You can only offer for credit in lots of 100` | Guard the connector's rounding | always on |
| M4 | **Inventory states**: a short fill moves shares `available → unavailable`. A cover moves them back to `available` for Locate and PreBorrow, **not for SingleUse** (documented) | Test `GetInventory().Unavailable`, reuse after a cover, a used-up SingleUse | always on |
| M13 | **Reg SHO pairs**: for a threshold symbol, one quote creates the base row (`locateType` 3, PreBorrow) and `<id>.SU` (type 4, SingleUse), each with its own price. Either side can be refused alone (base 54/11 while `.SU` is 65). Accepting one moves the sibling to 67 `Quote expired.`. Cancel works per row. Inventory rows per (symbol, type) | The handler's choice, accept by `.SU` id, sibling expiry, per-type inventory | `regsho: set[str]`; `locate_price` accepts `{"SOBR": {"PreBorrow": 0.03, "SingleUse": 0.01}}` (a missing type = refused 54/11); `locate_sibling_polls: int = 0` makes `.SU` appear N history reads after the base row |
| M14 | **SingleUse sell-back rule**: `/sell` with `SingleUse` on a row that was used → 56 (`[mock]` text). Locate and PreBorrow credit the unused part even after covers | Test `SellBackUnused` skipping a used SingleUse | always on |
| M15 | **Ordinary locate pool code 2**: `locate_pool_code: dict[str,int]` returns `locateType: 2` instead of 1 | Make sure the connector maps 2 → `Locate` | param |
| M5 | **Day roll**: inventory, open offers and pending sell-backs are cleared at the new ET day | Locates are valid for one day; tests "yesterday's locate is gone" | always on |
| M6 | **Seed / change inventory** without a quote: initial inventory and a control endpoint | Test "existing locate, no handler", and a race where someone else uses the inventory | `locate_inventory: dict[str,int] = {}` + `POST /mock/locates/inventory {"symbol","available"}` |
| M7 | **Error injection**: the next quote for a symbol returns a given (code, text). Covers 14 pending, R56 flip, any new code | Each broker refusal path of §4.1 | `POST /mock/locates/next-error {"symbol","code","text"}` |
| M8 | **Lost accept**: accept returns `locateAcceptSent` but the row stays 65, then expires | Test the "accept not filled → expired" path | `locate_accept_ignored: set[str] = set()` |
| M9 | **Expiry while the handler runs**: already possible with `POST /mock/time` (+30 s) during the handler | Test a late accept | none (test drives the clock) |
| M10 | **Sell-back credit ratio** | Cash and messages after a sell-back | `locate_sellback_credit: float = 1.0` |
| M11 | **HTTP faults** on locate routes: one-shot 429 / 500 / timeout | Retry, and "outcome unknown" handling | `POST /mock/faults {"route","status","count"}` (generic, also useful for orders) |
| M12 | `/mock/state` shows inventory, open quotes and locate costs | Test assertions and debugging | none |

Mock unit tests: extend `tests/test_short_locates.py` with one test per row (M1–M15). Update `docs/api-notes.md`: M3, M4 and M13 become `[docs]` (from the Short Locates guide), and the rest stays `[mock]` until checked on live.
Scenarios: keep `short_htb` (AAPL ETB, HTBX offered at $0.03, NOLOC no supply, SUBP under $0.50) and drive the variants through params (`POST /mock/scenario {"name":"short_htb","params":{...}}`). For Reg SHO, add a symbol `REGSHO` to `short_htb` (one more bar file, a copy of HTBX with another ticker) that is in `regsho` with both prices by default. That way both kinds of quote can be tested in the same scenario.

## 8. Connector test plan (C#, category `TzMock`)

| Test | Mock setup | Expected |
|---|---|---|
| HTB short, no handler, no inventory | `short_htb` | `Invalid`, message names `LocateHandler`; no order and no quote at the broker |
| HTB short, no handler, inventory covers it | `locate_inventory={"HTBX":200}` | Filled; handler not needed; inventory 200 → 100 |
| HTB short, no handler, partial inventory | inventory 100, short 200 | `Invalid` ("100 more") |
| Handler accepts | `short_htb` | `Submitted` → Filled; one offer, type Locate, 200 @ 0.03; first fill fee includes $6; `LocatePurchased` |
| Handler declines | `short_htb` | `Invalid`; quote row 52 (canceled) |
| Reg SHO: two offers | REGSHO | handler gets 2 offers, cheapest first (SingleUse 0.01, PreBorrow 0.03), with the right `ReusableAfterCover` / `SellBackAfterUse` |
| Reg SHO: choose PreBorrow | REGSHO, handler picks PreBorrow | accept sent with the base id; `.SU` row → 67; inventory row type PreBorrow; fee uses 0.03 |
| Reg SHO: choose SingleUse | REGSHO, handler picks SingleUse | accept sent with `<id>.SU`; base row → 67; inventory row type SingleUse |
| Reg SHO: decline both | REGSHO, handler returns None | both rows canceled (52); `Invalid` |
| Reg SHO: only SingleUse offered | REGSHO, `locate_price={"REGSHO":{"SingleUse":0.01}}` | handler gets 1 offer (SingleUse); a `LocateRefused` message for PreBorrow 54/11 |
| Reg SHO: `.SU` arrives late | `locate_sibling_polls=2` | handler still gets both offers (settle poll) |
| Handler returns a foreign object | REGSHO | counts as decline; error logged; both rows canceled |
| SingleUse not reusable | short 100 with SingleUse, cover, short 100 again, no handler | 2nd short `Invalid` (no inventory left) |
| PreBorrow reusable | same with PreBorrow | 2nd short filled with no new quote |
| Pool code 2 | M15 | offer type `Locate` |
| Handler throws | `short_htb` | `Invalid`; error logged; quote canceled |
| Quote delayed | `locate_quote_polls=3` | Filled; history polled ≥ 4 times |
| No supply | NOLOC | `Invalid` with "11 No shares available" |
| Price under $0.50 | SUBP | `Invalid` with R40 |
| Locate already pending | M7 code 14 | `Invalid` with "14 Locate is already pending." |
| Symbol became ETB | M7 R56 | ETB cache invalidated, order submitted |
| Offer expires before accept | handler advances the clock 31 s | `Invalid` "expired" |
| Lost accept | `locate_accept_ignored={"HTBX"}` | `Invalid` "expired", after the timeout |
| Odd lot warns | short 150 | `LocateOddLot` warning once; order proceeds |
| Odd lot suppressed | short 150 + `SuppressOddLotLocateWarning` | no warning |
| Round-lot short | short 200 | no warning |
| Cancel while awaiting the handler | handler blocks on an event; cancel | `Canceled`; quote canceled; no order sent |
| Cancel after accept, before submit | hook between accept and submit | `Canceled`; `LocateUnused`; inventory keeps the shares |
| Update while awaiting locate | | `UpdateRejected` |
| Update needs more locate | short 100 filled 0 (limit), update to 300 | `UpdateRejected`; original leg untouched |
| Zero-crossing order | long 50, sell 250, handler accepts | locate before the closing leg; both legs filled |
| Zero-crossing, inventory taken between legs | M6 sets available 0 after the first fill | `Canceled` "opening part was rejected" |
| Two concurrent HTB shorts | two orders of 100, handler accepts | one locate each (serialized); no double count |
| `GetAvailable` / `GetInventory` | after the accept and the short | available / unavailable as expected (M4) |
| `SellBack` ok | inventory 200 | `Pending`; next tick → `Sold`=200; credit message |
| `SellBack` 150 | | `Rejected` locally (multiple of 100), nothing sent |
| `SellBack` above available | | `Rejected` locally |
| `SellBack` without a type, two types held | PreBorrow + SingleUse rows | `Rejected` locally "several locate types held" |
| `SellBack` with a type | PreBorrow + SingleUse rows | `/sell` sent with `locateType: "PreBorrow"`; only that row changes |
| `SellBackUnused` | available 250 | sells 200 |
| `SellBackUnused`, used SingleUse | SingleUse used once + PreBorrow unused | PreBorrow sold; SingleUse result `Rejected` |
| `Locate()` without an order | | `Completed`; inventory grows |
| Not connected | no brokerage | reads empty; actions `Rejected` "not connected" |
| Day roll | advance to the next day | inventory empty (M5) |
| Python handler | `TradeZeroPythonTests`: lambda handler, `sell_back_unused` | Accept or decline from Python works; GIL OK from the connector thread |
| HTTP 500 on accept | M11 | the connector re-reads history (outcome unknown); no double accept |

Paper tests: only `IsEasyToBorrow` and an empty inventory. A manual **live** checklist (small HTB lot) answers the questions in §9.

## 9. Open questions (verify on live)

Answered by the Short Locates guide: quotes are multiples of 100; offers last 30 s; Locate and PreBorrow can be reused after a cover and SingleUse cannot; SingleUse can only be sold back if it was never used.

1. **Reg SHO pairs**: do the base and `.SU` rows appear in history **at the same time**? This sets how long the settle poll waits (§4.2, M13 `locate_sibling_polls`). Does cancelling the base id also cancel `.SU`? (The connector cancels both either way.)
2. Does `available` already exclude shares **reserved by working orders** and shares **in short positions**? What exactly does `unavailable` hold?
3. **Several types held for one symbol** (e.g. PreBorrow + SingleUse): which inventory does a short use first? This affects reuse after a cover.
4. **Sell-back credit**: the amount (full price or partial) and when it is credited. Neither is documented.
5. Can an offer cover **fewer shares than requested** (`locateShares` vs `filledShares`)?
6. The exact **reject text** for an HTB short without a locate (the mock uses a `MOCK:` text).
7. **Shortable provider**: OK to report HTB as `null` (§4.5)? The alternative is to keep `0` and require a setting `tradezero-locates-enabled=true` to switch it. The current proposal adds no setting.
8. **Locate cost** on the first fill fee (§4.6): wanted, or messages only?

## 10. Implementation order

1. tz-mock M1–M12 + mock tests + api-notes.
2. REST client + models + `TradeZeroLocateManager` (unit tests against the mock).
3. `TradeZeroLocates` facade (read + sell-back + `Locate`).
4. Order integration: properties, `AwaitingLocate` state, flow §4.1–4.4, shortable provider, fee.
5. C# test matrix §8, Python test, README (Locates section, remove "locates not supported" in README/remarks).
6. Live checklist §9, then fix the mock and the api-notes where live differs.
