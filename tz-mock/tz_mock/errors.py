"""TradeZero-shaped error responses (see docs/api-notes.md, "Errors")."""

from fastapi.responses import JSONResponse, Response

ENTITLEMENT = "Account for User was not found, or User doesn't have entitlements."


def not_found() -> Response:
    return Response(content="Not found", status_code=404, media_type=None)


def bad_request(message: str, detail: str | None) -> JSONResponse:
    return JSONResponse({"statusCode": "BadRequest", "message": message, "detail": detail}, status_code=400)


def wrong_account(operation: str, own: str, given: str) -> JSONResponse:
    return bad_request(operation, f"{own} - {given} - ASLib {ENTITLEMENT}")


def text_400(lines: list[str]) -> Response:
    return Response(content="\n".join(lines), status_code=400, media_type="text/plain; charset=utf-8")


def unauthorized_json() -> JSONResponse:
    return JSONResponse({"statusCode": "Unauthorized", "message": "Token not provided", "detail": None},
                        status_code=401)


class TZError(Exception):
    """Raise from anywhere in a request to return `response` as-is."""

    def __init__(self, response: Response):
        self.response = response
