"""Concealing and revealing by player distance: `!conceal run`, the periodic
scans, approaching a concealed grid, and what a concealed grid is on the
server."""

from __future__ import annotations

import time

import grids
from conftest import wait_until
from grids import offset

# Every grid of the world that no exclusion keeps revealed
CONCEALABLE = [g for g in grids.ALL if g not in grids.EXCLUDED]


def test_run_conceals_every_grid_far_from_players(game):
    count = game.conceal()
    concealed = game.concealed_ids()
    assert {g.entity_id for g in CONCEALABLE} <= concealed
    assert not {g.entity_id for g in grids.EXCLUDED} & concealed
    # The count may include the projector's projection, a grid of its own
    assert count >= len(CONCEALABLE)


def test_concealed_grid_stays_in_the_world(game):
    before = game.grid(grids.PLAIN_SHIP)
    game.conceal()
    concealed = game.grid(grids.PLAIN_SHIP)
    assert concealed["concealed"] and concealed["flagged"]
    assert concealed["blocks"] == before["blocks"]
    assert concealed["pos"] == before["pos"]
    assert concealed["physics"]

    game.a.command("!conceal reveal all")
    revealed = game.grid(grids.PLAIN_SHIP)
    assert not revealed["concealed"] and not revealed["flagged"]
    assert revealed["blocks"] == before["blocks"]


def test_player_nearby_keeps_a_grid_revealed(game):
    # 200 m: inside the concealment distance, outside the reveal distance
    game.a.teleport(offset(grids.PLAIN_STATION.position, 200.0))
    game.conceal()
    assert not game.is_concealed(grids.PLAIN_STATION)
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_other_player_nearby_keeps_a_grid_revealed(game):
    """Concealment is the same for everybody: a grid stays revealed for all
    while any player is near it"""
    game.b.teleport(offset(grids.SHARED_STATION.position, 200.0))
    game.conceal()
    assert not game.is_concealed(grids.SHARED_STATION)

    game.b.go_home()
    game.conceal()
    assert game.is_concealed(grids.SHARED_STATION)


def test_approach_reveals(game):
    game.conceal()
    game.a.teleport(offset(grids.PLAIN_SHIP.position, 100.0), settle=0)
    game.wait_concealed(grids.PLAIN_SHIP, False, timeout=5)
    # Only the grids near the player
    assert game.is_concealed(grids.PLAIN_STATION)
    assert game.is_concealed(grids.DRIFTER)


def test_other_player_approach_reveals(game):
    game.conceal()
    game.b.teleport(offset(grids.SHARED_STATION.position, 100.0), settle=0)
    game.wait_concealed(grids.SHARED_STATION, False, timeout=5)


def test_reveal_distance_is_a_hysteresis(game):
    """Between the reveal and the concealment distance a concealed grid stays
    concealed"""
    game.conceal()
    game.a.teleport(offset(grids.PLAIN_STATION.position, 200.0), settle=3)
    assert game.is_concealed(grids.PLAIN_STATION)
    game.a.teleport(offset(grids.PLAIN_STATION.position, 120.0), settle=0)
    game.wait_concealed(grids.PLAIN_STATION, False, timeout=5)


def test_reveal_interval(game):
    # A reveal scan every 10 minutes
    game.configure(RevealInterval=36000)
    game.conceal()
    game.a.teleport(offset(grids.PLAIN_SHIP.position, 50.0), settle=4)
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_periodic_scan_conceals(game):
    game.configure(ConcealInterval=120)
    game.wait_concealed(grids.PLAIN_STATION, True, timeout=10)


def test_periodic_scan_uses_the_conceal_distance(game):
    # The first sites are 1000 and 1800 m from home
    game.configure(ConcealInterval=120, ConcealDistance=1500)
    game.wait_concealed(grids.PLAIN_SHIP, True, timeout=10)
    time.sleep(3)
    assert not game.is_concealed(grids.PLAIN_STATION)


def test_disabled_plugin_does_not_scan(game):
    game.configure(ConcealInterval=120, Enabled=False)
    time.sleep(6)
    assert game.concealed_ids() == set()

    # Grids concealed while disabled are not revealed by approach either
    game.conceal()
    game.a.teleport(offset(grids.PLAIN_SHIP.position, 50.0), settle=3)
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_reveal_scan_after_concealing_does_not_flap(game):
    """A grid revealed by approach is not concealed again while the player
    stays, even by a scan with a short interval"""
    game.configure(ConcealInterval=120)
    game.a.teleport(offset(grids.PLAIN_SHIP.position, 50.0))
    wait_until(lambda: game.is_concealed(grids.PLAIN_STATION), 10, "first scan")
    for _ in range(4):
        assert not game.is_concealed(grids.PLAIN_SHIP)
        time.sleep(1.5)
