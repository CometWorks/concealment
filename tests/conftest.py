"""One session for the whole run: the server with the test world, player A on
client 1 (an administrator) and player B on client 2 (no rights).

Every test starts from the default plugin config (session.DEFAULT_CONFIG) with
every grid revealed and both players at home; a test changes the config it
needs through the probe (`configure`), so the server keeps running between
tests. Only test_restart.py restarts it.

CC_ATTACH=1 reuses a running server and clients (session.py start), CC_KEEP=1
leaves them running after the run.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import grids  # noqa: E402
import rig  # noqa: E402
import session  # noqa: E402
from session import CLIENTS, SERVER, parse, vector  # noqa: E402

ATTACH = os.environ.get("CC_ATTACH") == "1"
KEEP = os.environ.get("CC_KEEP") == "1"


class Game:
    def __init__(self):
        self.a, self.b = session.players()
        self.server = SERVER

    # --- config ------------------------------------------------------------

    def configure(self, **options) -> None:
        """Puts the default config with these options into the running plugin"""
        SERVER.write_plugin_config(
            rig.plugin_config(**{**session.DEFAULT_CONFIG, **options})
        )
        assert self.a.probe("reload") == "Probe: config reloaded"

    def reset(self) -> None:
        """The state every test starts from. Brings the server and the
        clients back if a failed restart test left them down."""
        if not SERVER.pid():
            SERVER.start()
        if not all(c.pid() for c in CLIENTS):
            for client in CLIENTS:
                client.stop()
            self.rejoin()
        self.configure()
        self.a.command("!conceal reveal all")
        for player in (self.a, self.b):
            player.go_home()
        self.a.command("!conceal reveal all")
        assert self.concealed_ids() == set()

    def rejoin(self) -> None:
        """After a server restart"""
        session.join_all()
        self.a, self.b = session.players()

    # --- saved world -------------------------------------------------------

    def _sector(self) -> str:
        return (SERVER.world / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")

    def saved_grids(self) -> list[int]:
        """Entity ids of the grids in the saved world, one per grid saved"""
        pattern = r'xsi:type="MyObjectBuilder_CubeGrid">.*?<EntityId>(\d+)</EntityId>'
        return [int(i) for i in re.findall(pattern, self._sector(), re.S)]

    def saved_block(self, entity_id: int) -> str:
        """A functional block of the saved world, as its XML"""
        pattern = rf"<EntityId>{entity_id}</EntityId>.*?</MyObjectBuilder_CubeBlock>"
        match = re.search(pattern, self._sector(), re.S)
        assert match, entity_id
        return match.group(0)

    # --- concealment -------------------------------------------------------

    def conceal(self, distance: float = grids.CONCEAL_RUN_M) -> int:
        """`!conceal run`, and how many grids it concealed"""
        reply = self.a.command(f"!conceal run {distance}")
        match = re.fullmatch(r"(\d+) grids concealed\.", reply)
        assert match, reply
        return int(match.group(1))

    def concealed_ids(self) -> set[int]:
        reply = self.a.probe("concealed").removeprefix("Probe concealed: ")
        return set() if reply == "none" else {int(i) for i in reply.split()}

    def grid(self, g: grids.Grid | int) -> dict:
        """The probe's view of a grid, with concealed, flagged and static as
        bools, pos and vel as vectors and group as entity ids. Empty when the
        server has no such grid."""
        reply = self.a.probe("grid", g if isinstance(g, int) else g.entity_id)
        if reply == "Probe grid: none":
            return {}
        state = parse(reply)
        for key in ("concealed", "flagged", "static", "physics"):
            state[key] = state[key] == "1"
        for key in ("pos", "vel"):
            state[key] = vector(state[key])
        state["group"] = {int(i) for i in state["group"].split(",")}
        state["blocks"] = int(state["blocks"])
        state["frame"] = int(state["frame"])
        return state

    def is_concealed(self, g: grids.Grid) -> bool:
        return g.entity_id in self.concealed_ids()

    def wait_concealed(self, g: grids.Grid, value: bool, timeout: float = 15.0):
        wait_until(
            lambda: self.is_concealed(g) == value,
            timeout,
            f"{g.name} concealed={value}",
        )


def wait_until(predicate, timeout: float, what: str, interval: float = 0.5):
    deadline = time.monotonic() + timeout
    while True:
        if predicate():
            return
        if time.monotonic() > deadline:
            raise AssertionError(f"Timed out waiting for {what}")
        time.sleep(interval)


@pytest.fixture(scope="session")
def session_game():
    attach = ATTACH and SERVER.pid() and all(c.pid() for c in CLIENTS)
    try:
        if not attach:
            session.stop_all()
            session.prepare()
            SERVER.start()
            session.join_all()
        yield Game()
    finally:
        if not KEEP:
            session.stop_all()


@pytest.fixture
def game(session_game):
    session_game.reset()
    return session_game


def pytest_collection_modifyitems(items):
    """The restart tests go last: a failed restart would take the session
    down with it"""
    items.sort(key=lambda item: item.module.__name__ == "test_restart")
