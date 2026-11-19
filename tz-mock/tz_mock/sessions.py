"""Sessions keyed by the TZ-API-KEY-ID header: each key has its own scenario, clock and portfolio.

All endpoints are `async def` and do no I/O, so requests run one at a time on the event loop
and the state needs no locking.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

from . import scenarios
from .broker import Broker


def account_id_for(key: str) -> str:
    return "TZP" + hashlib.sha1(key.encode()).hexdigest()[:5].upper()


@dataclass
class Session:
    key: str
    broker: Broker


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

    def drop(self, key: str) -> None:
        self._sessions.pop(key, None)
