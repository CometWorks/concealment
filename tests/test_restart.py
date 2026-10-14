"""What a server restart does to concealed grids and to the config: every grid
is saved once and comes back once, concealed grids are saved in their working
state, the plugin conceals again once it is ready, a config edited while the
server was down is the one in use, and a broken config file is set aside for
the defaults.

These tests restart the server and join the clients again; conftest.py runs
them last.
"""

from __future__ import annotations

import time

import grids
import rig
import session
from conftest import wait_until


def stop(game) -> None:
    for client in (game.a.client, game.b.client):
        client.stop()
    game.server.stop()


def grid_ids(game) -> set[int]:
    """Entity ids of the grids on the server, projections left out"""
    return {int(i) for i in game.a.probe("ids").split()[2:]}


def test_concealed_grids_survive_a_restart(game):
    game.conceal()
    concealed = game.concealed_ids()
    assert grids.PROJECTOR_GRID.entity_id in concealed
    before = grid_ids(game)
    assert {g.entity_id for g in grids.ALL} <= before
    stop(game)

    # Saved once each, nothing lost, nothing doubled
    saved = game.saved_grids()
    assert sorted(saved) == sorted(before)
    # The projector concealment switched off is saved switched on
    assert "<Enabled>true</Enabled>" in game.saved_block(grids.PROJECTOR_ID)

    # Edited while the server is down: projectors keep a grid revealed
    game.server.write_plugin_config(
        rig.plugin_config(
            **{
                **session.DEFAULT_CONFIG,
                "ExcludedSubtypes": [grids.EXCLUDED_SUBTYPE, "LargeProjector"],
            }
        )
    )
    game.server.start()
    game.rejoin()

    for g in grids.ALL:
        reply = game.a.probe("count", f'"{g.name}"')
        assert reply.split()[2] == "1", reply
    assert grid_ids(game) == before

    # The scan once the plugin is ready conceals again, by the edited config
    wait_until(lambda: game.is_concealed(grids.PLAIN_STATION), 60, "concealed again")
    assert not game.is_concealed(grids.PROJECTOR_GRID)
    state = game.a.probe("projector", grids.PROJECTOR_ID)
    assert state == "Probe projector: enabled=1 projecting=1"


def test_disabled_concealment_stays_disabled_after_a_restart(game):
    assert game.a.command("!conceal off").startswith("Concealment disabled.")
    stop(game)
    game.server.start()
    started = time.monotonic()
    game.rejoin()
    # Longer than the 30 s the plugin waits before its first scan
    time.sleep(max(0.0, started + 45 - time.monotonic()))
    assert game.concealed_ids() == set()
    assert "<Enabled>false</Enabled>" in game.server.plugin_cfg.read_text()


def test_broken_config_is_set_aside(game):
    data = game.server.data
    for old in data.glob("Concealment.cfg.corrupted.*"):
        old.unlink()
    stop(game)
    game.server.plugin_cfg.write_text("<Settings><Enabled>", encoding="utf-8")
    game.server.start()
    game.rejoin()

    assert len(list(data.glob("Concealment.cfg.corrupted.*.txt"))) == 1
    config = game.server.plugin_cfg.read_text(encoding="utf-8")
    assert "<ConcealDistance>15000</ConcealDistance>" in config
    assert game.a.command("!conceal run").endswith("grids concealed.")
