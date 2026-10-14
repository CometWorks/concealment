"""A running test session: the server with its world, and the players on the
clients. Also usable from the command line while iterating::

    uv run python tests/session.py start     # server and both clients
    uv run python tests/session.py stop
"""

from __future__ import annotations

import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import grids  # noqa: E402
import rig  # noqa: E402
import world  # noqa: E402

SERVER = rig.Server()
CLIENT_A = rig.Client(1, "Alpha")
CLIENT_B = rig.Client(2, "Bravo")
CLIENTS = (CLIENT_A, CLIENT_B)

REPLY_TIMEOUT_S = 20.0
# A reply is complete once no more of it came for this long
QUIET_S = 1.0
# Replies come from the command's root, errors from its title
ROOT_TITLES = {"conceal": "Concealment", "cprobe": "ConcealmentProbe"}

# The plugin config every test starts from. The periodic concealment scan
# runs once when the plugin gets ready and then hardly ever, so the tests
# conceal with `!conceal run`. Reveal scans run twice a second.
DEFAULT_CONFIG = {
    "Enabled": True,
    "ConcealDistance": grids.CONCEAL_RUN_M,
    "ConcealInterval": 864000,
    "RevealDistance": 150,
    "RevealInterval": 30,
    "ProductionConcealment": "Conceal",
    "ProductionBoostLevel": 5,
    "MaxBoostHours": 8,
    "ConcealPirates": True,
    "RemoteControlKeepAliveAction": True,
    "ExcludedSubtypes": [grids.EXCLUDED_SUBTYPE],
    "DynamicConcealQueryInterval": 1,
    "DynamicConcealScanInterval": 0.5,
    "DynamicConcealment": [],
}

# Grid Backups backs up nothing on its own: no grid has enough blocks for its
# scans. A backup can only come from a call of its public entry points.
GRID_BACKUPS_CONFIG = {
    "Enabled": True,
    "UseQuasarConfigFolder": True,
    "BackupNobodyGrids": True,
    "ScanIntervalSeconds": 86400,
    "MinMinutesSinceLastSave": 0,
    "MinBlocksForBackup": 1000000,
    "BackupIfSteady": False,
    "MaxBlockCountChange": 0,
}

# No NPC ships: the game spawns and removes encounters on its own, which would
# change the set of grids under the tests
WORLD_SETTINGS = {
    "EnableIngameScripts": "true",
    "EnableEncounters": "false",
    "GlobalEncounterCap": "0",
    "CargoShipsEnabled": "false",
    "EnableContainerDrops": "false",
    "EnableDrones": "false",
    "EnablePlanetaryEncounters": "false",
}


def parse(reply: str) -> dict[str, str]:
    """`Probe x: a=1 b=2` as {"a": "1", "b": "2"}"""
    body = reply.split(": ", 1)[1]
    return dict(p.split("=", 1) for p in body.split() if "=" in p)


def vector(text: str) -> tuple[float, float, float]:
    x, y, z = (float(v) for v in text.split(","))
    return x, y, z


class Player:
    """A client's player: chat commands and moving around through Remote"""

    def __init__(self, client: rig.Client, identity_id: int):
        self.client = client
        self.identity_id = identity_id
        self.api = client.api()

    # --- chat ----------------------------------------------------------------

    def _chat(self) -> list[tuple[str, str, str]]:
        hud = self.api.get_chat_messages().get("hud") or []
        return [(m.get("sender"), m.get("text"), m.get("timestamp")) for m in hud]

    def chat_since(self, before: set) -> list[tuple[str, str]]:
        return [(s, t) for s, t, ts in self._chat() if (s, t, ts) not in before]

    def mark(self) -> set:
        return set(self._chat())

    def command(self, text: str, timeout: float = REPLY_TIMEOUT_S) -> str:
        """Sends a chat command and returns its reply. A reply of several
        messages comes back as their lines."""
        prefix = text.split()[0][1:]
        senders = {prefix, ROOT_TITLES.get(prefix)}
        before = self.mark()
        self.api.send_chat(text)
        deadline = time.monotonic() + timeout
        replies, last = [], None
        while time.monotonic() < deadline:
            new = [t for s, t in self.chat_since(before) if s in senders]
            if new != replies:
                replies, last = new, time.monotonic()
            elif replies and time.monotonic() - last > QUIET_S:
                return "\n".join(replies)
            time.sleep(0.3)
        if replies:
            return "\n".join(replies)
        raise TimeoutError(f"No reply to {text!r}")

    def probe(self, *args) -> str:
        return self.command("!cprobe " + " ".join(str(a) for a in args))

    # --- moving --------------------------------------------------------------

    def teleport(self, position, settle: float = 2.0) -> None:
        """The admin teleport. Player B may not use it, so the probe runs it
        for B on the server."""
        if self.client is CLIENT_A:
            self.api.character_teleport(*position)
        else:
            reply = players()[0].probe("tp", self.client.steam_id, *position)
            assert reply == "Probe tp: done", reply
        time.sleep(settle)

    def go_home(self) -> None:
        self.teleport(home_of(self))


def home_of(player: Player):
    """A and B wait 20 m apart, so they never stand in each other"""
    dx = 0.0 if player.client is CLIENT_A else 20.0
    return grids.offset(grids.HOME, dx)


# ---------------------------------------------------------------------------
# Start and stop
# ---------------------------------------------------------------------------


def prepare() -> None:
    """A fresh world with every grid, and the server's configs"""
    world.prepare(
        rig.EMPTY_WORLD,
        SERVER.world,
        {
            CLIENT_A.steam_id: (world.PLAYER_A, CLIENT_A.name),
            CLIENT_B.steam_id: (world.PLAYER_B, CLIENT_B.name),
        },
        grids.xml(),
        WORLD_SETTINGS,
    )
    SERVER.prepare([CLIENT_A.steam_id])
    SERVER.write_plugin_config(rig.plugin_config(**DEFAULT_CONFIG))
    SERVER.write_grid_backups_config(rig.grid_backups_config(**GRID_BACKUPS_CONFIG))
    SERVER.banks.unlink(missing_ok=True)


def join(client: rig.Client) -> rig.RemoteAPI:
    """Launches a client, which connects on its own, and waits for its
    character"""
    client.launch(SERVER.port)
    api = client.wait_api()
    rig.wait_world(api)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    return api


def join_all(clients=CLIENTS) -> None:
    """Joins the clients in parallel, and sends the players home"""
    errors = []

    def run(client):
        try:
            join(client)
        except Exception as error:  # noqa: BLE001 -- reported below
            errors.append((client.name, error))

    threads = [threading.Thread(target=run, args=(c,)) for c in clients]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    if errors:
        raise RuntimeError(f"Joining failed: {errors}")
    for player in players():
        if player.client in clients:
            player.go_home()


def stop_all() -> None:
    for client in CLIENTS:
        client.stop()
    SERVER.stop()


def players() -> tuple[Player, Player]:
    return Player(CLIENT_A, world.PLAYER_A), Player(CLIENT_B, world.PLAYER_B)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_all()
        prepare()
        SERVER.start()
        join_all()
        print(
            f"Joined. Remote ports {CLIENT_A.port}, {CLIENT_B.port}; server log {SERVER.log}"
        )
    elif command == "stop":
        stop_all()
    else:
        sys.exit(__doc__)
