"""What stops while a grid is concealed and comes back when it is revealed:
timers, programmable blocks, thrusters and a drifting grid. The plugin
conceals active grids just the same (only production has special handling,
see test_production.py)."""

from __future__ import annotations

import math
import time

import grids
import pytest
from grids import offset
from session import parse

# The timer counts down 166 ms of its time every 10 ticks
TIMER_MS_PER_S = 166 * 6


def timer(game) -> tuple[bool, int, int]:
    state = parse(game.a.probe("timer", grids.TIMER_ID))
    return state["counting"] == "1", int(state["ms"]), int(state["frame"])


def pb_runs(game) -> tuple[int, int]:
    """How often the programmable block ran, and the server's frame"""
    reply = game.a.probe("data", grids.PB_ID).removeprefix("Probe data: ")
    runs, frame = reply.split(" frame=")
    return int(runs or 0), int(frame)


def speed(game, g) -> float:
    return math.dist(game.grid(g)["vel"], (0, 0, 0))


def stop(game, g) -> None:
    """Back where it was built, at rest"""
    assert game.a.probe("place", g.entity_id, *g.position) == "Probe place: done"


def test_timer_stops_while_concealed(game):
    counting, before, _ = timer(game)
    assert counting
    time.sleep(2)
    assert timer(game)[1] < before

    game.conceal()
    _, start, start_frame = timer(game)
    time.sleep(3)
    _, end, end_frame = timer(game)
    assert end_frame - start_frame > 120
    assert end == start

    game.a.command("!conceal reveal all")
    _, start, start_frame = timer(game)
    time.sleep(3)
    counting, end, end_frame = timer(game)
    assert counting
    elapsed_s = (end_frame - start_frame) / 60
    # Counting at its normal speed, not faster
    assert start - end == pytest.approx(elapsed_s * TIMER_MS_PER_S, rel=0.25)


def test_programmable_block_stops_while_concealed(game):
    runs, frame = pb_runs(game)
    time.sleep(2)
    assert pb_runs(game)[0] > runs

    game.conceal()
    runs, frame = pb_runs(game)
    time.sleep(3)
    assert pb_runs(game)[0] == runs

    game.a.command("!conceal reveal all")
    runs, frame = pb_runs(game)
    time.sleep(3)
    later, later_frame = pb_runs(game)
    # Every 10 ticks, not more often
    assert later - runs == pytest.approx((later_frame - frame) / 10, abs=3)


def test_thrust_stops_while_concealed(game):
    ship = grids.THRUSTER_SHIP
    try:
        # A slow push, so the ship stays well below the speed limit
        assert game.a.probe("thrust", grids.THRUSTER_ID, 0.02) == "Probe thrust: set"
        time.sleep(2)
        v1 = speed(game, ship)
        time.sleep(2)
        v2 = speed(game, ship)
        assert v2 > v1 + 0.5, "the thruster accelerates the ship"

        game.conceal()
        assert game.is_concealed(ship)
        v1 = speed(game, ship)
        time.sleep(3)
        v2 = speed(game, ship)
        assert v2 == pytest.approx(v1, abs=0.2), "no thrust while concealed"

        game.a.command("!conceal reveal all")
        v3 = speed(game, ship)
        time.sleep(2)
        assert speed(game, ship) > v3 + 0.5, "thrust again once revealed"
    finally:
        game.a.probe("thrust", grids.THRUSTER_ID, 0)
        stop(game, ship)


def test_concealed_grid_keeps_drifting(game):
    """A concealed grid is not frozen in space: its physics still run, so a
    grid concealed while moving keeps moving"""
    drifter = grids.DRIFTER
    try:
        game.a.probe("velocity", drifter.entity_id, 20, 0, 0)
        game.conceal()
        start = game.grid(drifter)
        time.sleep(4)
        end = game.grid(drifter)
        assert end["concealed"]
        moved = end["pos"][0] - start["pos"][0]
        elapsed = (end["frame"] - start["frame"]) / 60
        assert moved == pytest.approx(20 * elapsed, rel=0.3)
    finally:
        game.a.command("!conceal reveal all")
        stop(game, drifter)


@pytest.mark.xfail(
    strict=True,
    reason="the reveal scan looks for a concealed grid where it was concealed",
)
def test_approach_reveals_a_grid_that_drifted(game):
    drifter = grids.DRIFTER
    try:
        game.a.probe("velocity", drifter.entity_id, 0, 50, 0)
        game.conceal()
        time.sleep(8)
        position = game.grid(drifter)["pos"]
        assert position[1] - drifter.position[1] > 300
        game.a.teleport(offset(position, 50.0), settle=0)
        game.wait_concealed(drifter, False, timeout=5)
    finally:
        game.a.command("!conceal reveal all")
        stop(game, drifter)


@pytest.mark.xfail(
    strict=True,
    reason="a block of a concealed grid that asks for updates gets them",
)
def test_block_woken_while_concealed_stays_concealed(game):
    """Restarting the timer of a concealed grid, the way a mod, a script on
    another grid or a network event would, must not make it count"""
    game.conceal()
    reply = game.a.probe("restarttimer", grids.TIMER_ID)
    assert reply == "Probe restarttimer: restarted"
    _, start, _ = timer(game)
    time.sleep(3)
    assert timer(game)[1] == start


def test_block_woken_while_concealed_counts_normally_once_revealed(game):
    """Whatever happened while concealed, a revealed timer counts at its normal
    speed: it is not registered for updates twice"""
    game.conceal()
    game.a.probe("restarttimer", grids.TIMER_ID)
    game.a.command("!conceal reveal all")
    _, start, start_frame = timer(game)
    time.sleep(3)
    _, end, end_frame = timer(game)
    elapsed_s = (end_frame - start_frame) / 60
    assert start - end == pytest.approx(elapsed_s * TIMER_MS_PER_S, rel=0.25)
