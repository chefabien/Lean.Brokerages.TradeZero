"""Sessions keyed by the TZ-API-KEY-ID header: each key has its own scenario, clock and portfolio.

All endpoints are `async def` and do no I/O, so requests run one at a time on the event loop
and the state needs no locking.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from fnmatch import fnmatchcase

from . import scenarios
from .broker import Broker


def account_id_for(key: str) -> str:
    return "TZP" + hashlib.sha1(key.encode()).hexdigest()[:5].upper()


@dataclass
class Fault:
    """A one-shot HTTP failure injected with POST /mock/faults."""
    method: str
    path: str  # fnmatch pattern on the URL path, e.g. "/v1/api/accounts/locates/accept" or "*/locates/*"
    status: int = 500
    count: int = 1
    when: str = "before"  # "before": the request is not processed; "after": it is processed, the response lost
    delay_ms: int = 0
    body: str = ""

    def matches(self, method: str, path: str) -> bool:
        return self.count > 0 and self.method.upper() == method.upper() and fnmatchcase(path, self.path)

    def to_json(self) -> dict:
        return {"method": self.method, "path": self.path, "status": self.status, "count": self.count,
                "when": self.when, "delayMs": self.delay_ms, "body": self.body}


@dataclass
class Session:
    key: str
    broker: Broker
    faults: list[Fault] = field(default_factory=list)

    def take_fault(self, method: str, path: str) -> Fault | None:
        for fault in self.faults:
            if fault.matches(method, path):
                fault.count -= 1
                if fault.count == 0:
                    self.faults.remove(fault)
                return fault
        return None


class SessionStore:
    def __init__(self) -> None:
        self._sessions: dict[str, Session] = {}

    def get(self, key: str) -> Session:
        if key not in self._sessions:
            return self.load(key, scenarios.DEFAULT)
        return self._sessions[key]

    def load(self, key: str, name: str, params: dict | None = None) -> Session:
        """(Re)start the session on a scenario: everything is zeroed out."""
        scenario = scenarios.create(name, params)
        session = Session(key, Broker(account_id_for(key), scenario))
        self._sessions[key] = session
        return session

    def peek(self, key: str) -> Session | None:
        """The session if it exists, without creating one."""
        return self._sessions.get(key)

    def drop(self, key: str) -> None:
        self._sessions.pop(key, None)
