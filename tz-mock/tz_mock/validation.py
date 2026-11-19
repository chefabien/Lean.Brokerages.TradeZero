"""Schema validation of POST /order bodies, reproducing the paper API's 400 text."""

from __future__ import annotations

import re

from . import errors

SYMBOL_RE = re.compile(r"^[a-zA-Z0-9._-]+$")
ENUMS = {
    "securityType": ["Stock", "Option", "Mleg", "Future", "Prediction"],
    "orderType": ["Limit", "Market", "Stop", "StopLimit"],
    "side": ["Buy", "Sell"],
    "timeInForce": ["Day", "GoodTillCancel", "AtTheOpening", "ImmediateOrCancel", "FillOrKill",
                    "GoodTillCrossing", "Day_Plus", "GTC_Plus"],
    "openClose": ["Open", "Close"],
}
SUPERSCRIPT = str.maketrans("0123456789-", "⁰¹²³⁴⁵⁶⁷⁸⁹⁻")


def sci(x: float) -> str:
    """Format like the server's JSON-schema validator: 2000000 -> 2×10⁰⁶, 1e-06 -> 1×10⁻⁰⁶."""
    if x == 0:
        return "0"
    mantissa, exp = f"{x:e}".split("e")
    mantissa = mantissa.rstrip("0").rstrip(".")
    e = int(exp)
    power = (("-" if e < 0 else "") + f"{abs(e):02d}").translate(SUPERSCRIPT)
    return f"{mantissa}×10{power}"


def _enum_msg(field: str) -> str:
    values = ", ".join(f"'{v}'" for v in ENUMS[field])
    return f"- at '/{field}': value must be one of {values}"


def validate_order(body: dict) -> dict:
    """Return the body (quantity coerced to int) or raise TZError with the TZ error response."""
    lines: list[str] = []
    symbol = body.get("symbol")
    if not isinstance(symbol, str) or not SYMBOL_RE.match(symbol):
        shown = "<no value>" if symbol is None else symbol
        lines.append(f"- at '/symbol': '{shown}' does not match pattern '^[a-zA-Z0-9._-]+$'")

    qty_raw = body.get("orderQuantity")
    qty: float | None
    try:
        qty = float(qty_raw) if qty_raw is not None and not isinstance(qty_raw, bool) else None
    except (TypeError, ValueError):
        qty = None
    if qty is None:
        lines.append("- at '/orderQuantity': minimum: got 0, want 1×10⁻⁰⁶")
    elif qty < 1e-6:
        lines.append(f"- at '/orderQuantity': minimum: got {sci(qty)}, want 1×10⁻⁰⁶")
    elif qty > 1e6:
        lines.append(f"- at '/orderQuantity': maximum: got {sci(qty)}, want 1×10⁰⁶")

    for field in ("securityType", "orderType", "side", "timeInForce"):
        if body.get(field) not in ENUMS[field]:
            lines.append(_enum_msg(field))
    if body.get("openClose") is not None and body["openClose"] not in ENUMS["openClose"]:
        lines.append(_enum_msg("openClose"))
    for field in ("limitPrice", "stopPrice"):
        v = body.get(field)
        if v is not None and (isinstance(v, bool) or not isinstance(v, (int, float))):
            lines.append(f"- at '/{field}': got {type(v).__name__}, want number")

    if lines:
        raise errors.TZError(errors.text_400(lines))
    if not float(qty).is_integer():
        raise errors.TZError(errors.bad_request(
            "PlaceOrderWithResponse", "ASLib Order quantity invalid, fractional shares not enabled."))
    return {**body, "orderQuantity": int(qty)}
