"""What a concealed grid is to other systems: sensors, collisions, its own
projector, and Grid Backups."""

from __future__ import annotations

import time

import grids
import pytest
from conftest import wait_until
from session import parse


def sensor(game) -> tuple[bool, set[int]]:
    state = parse(game.a.probe("sensor", grids.SENSOR_ID))
    detected = {int(i) for i in state.get("detected", "").split(",") if i}
    return state["active"] == "1", detected


def projector(game) -> tuple[bool, bool]:
    state = parse(game.a.probe("projector", grids.PROJECTOR_ID))
    return state["enabled"] == "1", state["projecting"] == "1"


def place(game, g) -> None:
    assert game.a.probe("place", g.entity_id, *g.position) == "Probe place: done"


def test_sensor_detects_a_concealed_grid(game):
    """The sensor post stays revealed (excluded subtype), the ship 25 m from it
    is concealed: the sensor still sees it"""
    time.sleep(1)
    active, detected = sensor(game)
    assert active and grids.SENSED_SHIP.entity_id in detected

    game.conceal()
    assert game.is_concealed(grids.SENSED_SHIP)
    assert not game.is_concealed(grids.SENSOR_POST)
    time.sleep(2)
    active, detected = sensor(game)
    assert active and grids.SENSED_SHIP.entity_id in detected


def test_concealed_grid_collides(game):
    """A concealed ship rammed into an excluded wall stops at it: its physics
    still run"""
    rammer = grids.RAMMER
    try:
        game.conceal()
        assert game.is_concealed(rammer)
        assert not game.is_concealed(grids.WALL)
        game.a.probe("velocity", rammer.entity_id, 0, 0, -15)
        # 60 m at 15 m/s
        time.sleep(7)
        state = game.grid(rammer)
        assert state["concealed"]
        # It did not pass the wall at the wall's z
        assert state["pos"][2] > grids.WALL.position[2]
        assert state["vel"][2] > -14
    finally:
        game.a.command("!conceal reveal all")
        place(game, rammer)


def wait_projecting(game) -> None:
    """A projector switched back on takes a moment to build its projection"""
    wait_until(lambda: projector(game) == (True, True), 15, "the projection back")


def test_projector_is_switched_off_while_concealed(game):
    wait_projecting(game)
    game.conceal()
    assert game.is_concealed(grids.PROJECTOR_GRID)
    assert projector(game) == (False, False)

    game.a.command("!conceal reveal all")
    wait_projecting(game)


def test_projector_switched_off_stays_off(game):
    """Concealment switches back on only the projectors it switched off"""
    game.a.probe("action", grids.PROJECTOR_ID, "OnOff_Off")
    try:
        assert projector(game)[0] is False
        game.conceal()
        game.a.command("!conceal reveal all")
        time.sleep(1)
        assert projector(game)[0] is False
    finally:
        game.a.probe("action", grids.PROJECTOR_ID, "OnOff_On")


@pytest.mark.xfail(
    strict=True,
    reason="a world saved while a grid is concealed saves its projector switched off",
)
def test_world_saved_while_concealed_keeps_the_projector_on(game):
    """An autosave or a save command while the grid is concealed must not save
    the projector switched off: after a crash it would stay off"""
    game.conceal()
    assert projector(game) == (False, False)
    assert game.a.probe("save") == "Probe save: True"
    assert "<Enabled>true</Enabled>" in game.saved_block(grids.PROJECTOR_ID)


@pytest.mark.xfail(
    strict=True,
    reason="SE1-0198: Concealment never calls Grid Backups' BackupBeforeConcealment",
)
def test_grid_backups_backs_up_before_concealment(game):
    before = set(game.server.backup_files())
    game.conceal()
    time.sleep(3)
    new = set(game.server.backup_files()) - before
    assert any(f"_{grids.BACKUP_STATION.entity_id}" in str(p) for p in new)
