"""Active production and the three ProductionConcealment modes: Off keeps a
grid with an active refinery revealed, Conceal pauses it, Approximate banks
the time it spent concealed and lets it catch up faster once revealed."""

from __future__ import annotations

import re
import time

import grids
import pytest
from conftest import wait_until
from session import parse

ORE = "Cobalt"
ORE_KG = 2000


@pytest.fixture
def refinery(game):
    """The refinery, refining, without a bank"""
    game.a.probe("clearbanks")
    game.a.probe("clear", grids.REFINERY_ID)
    assert game.a.probe("ore", grids.REFINERY_ID, ORE, ORE_KG).startswith("Probe ore:")
    wait_until(lambda: production(game)["producing"], 10, "the refinery producing")
    yield grids.REFINERY
    game.a.probe("clear", grids.REFINERY_ID)
    game.a.probe("clearbanks")


def production(game) -> dict:
    state = parse(game.a.probe("production", grids.REFINERY_ID))
    return {
        "producing": state["producing"] == "1",
        "input": float(state["input"]),
        "power": float(state["power"]),
        "frame": int(state["frame"]),
    }


def rate(game, seconds: float) -> float:
    """Ore refined per second over this long"""
    start = production(game)
    time.sleep(seconds)
    end = production(game)
    return (start["input"] - end["input"]) / ((end["frame"] - start["frame"]) / 60)


def banks(game) -> dict[int, float]:
    reply = game.a.probe("banks").removeprefix("Probe banks: ")
    if reply == "none":
        return {}
    return {int(k): float(v) for k, v in (p.split("=") for p in reply.split())}


def test_off_keeps_active_production_revealed(game, refinery):
    game.configure(ProductionConcealment="Off")
    game.conceal()
    assert not game.is_concealed(refinery)
    assert game.is_concealed(grids.PLAIN_STATION)


def test_off_conceals_idle_production(game):
    game.configure(ProductionConcealment="Off")
    game.a.probe("clear", grids.REFINERY_ID)
    game.conceal()
    assert game.is_concealed(grids.REFINERY)


def test_off_reveals_concealed_production_once_active(game, refinery):
    """Production started while concealed under another mode keeps the grid
    revealed once the mode is Off"""
    game.conceal()
    assert game.is_concealed(refinery)
    game.configure(ProductionConcealment="Off")
    game.wait_concealed(refinery, False, timeout=5)


def test_conceal_pauses_production(game, refinery):
    game.conceal()
    assert game.is_concealed(refinery)
    start = production(game)
    time.sleep(3)
    assert production(game)["input"] == start["input"]

    game.a.command("!conceal reveal all")
    assert rate(game, 3) > 0
    assert grids.REFINERY.entity_id not in banks(game)


def test_approximate_catches_up(game, refinery):
    """The refinery works in batches, so the rates are taken over several"""
    game.configure(ProductionConcealment="Approximate", ProductionBoostLevel=5)
    normal = rate(game, 20)
    assert normal > 0

    game.conceal()
    start = production(game)["frame"]
    time.sleep(60)
    reply = game.a.command("!conceal reveal all")
    bank = banks(game).get(refinery.entity_id)
    revealed = production(game)
    assert bank is not None, reply
    # Concealed time divided by the boost level, minus what it spent since
    assert bank == pytest.approx((revealed["frame"] - start) / 5, rel=0.2)

    wait_until(lambda: refinery.entity_id not in banks(game), 40, "the bank spent")
    spent = production(game)
    # Five times the normal speed while the bank lasted, normal speed from
    # then until the test saw it spent
    seconds = (spent["frame"] - revealed["frame"]) / 60
    boost_seconds = min(bank / 60, seconds)
    expected = normal * (5 * boost_seconds + (seconds - boost_seconds))
    assert revealed["input"] - spent["input"] == pytest.approx(expected, rel=0.3)

    # Normal speed again
    assert rate(game, 10) == pytest.approx(normal, rel=0.25)


def test_approximate_bank_is_capped(game, refinery):
    # 0.0005 hours is 108 frames, 10 s concealed would bank 120
    game.configure(ProductionConcealment="Approximate", MaxBoostHours=0.0005)
    game.conceal()
    time.sleep(10)
    # Without ore nothing spends the bank once revealed
    game.a.probe("clear", grids.REFINERY_ID)
    game.a.command("!conceal reveal all")
    assert banks(game).get(refinery.entity_id) == pytest.approx(108, abs=1)


def test_approximate_idle_grid_gets_no_bank(game):
    game.configure(ProductionConcealment="Approximate")
    game.a.probe("clearbanks")
    game.a.probe("clear", grids.REFINERY_ID)
    game.conceal()
    time.sleep(3)
    game.a.command("!conceal reveal all")
    assert grids.REFINERY.entity_id not in banks(game)


def test_banks_are_saved_with_the_world(game, refinery):
    game.configure(ProductionConcealment="Approximate")
    game.conceal()
    time.sleep(10)
    game.a.command("!conceal reveal all")
    # No ore, no production: the bank stays as it is
    game.a.probe("clear", grids.REFINERY_ID)
    bank = banks(game)[refinery.entity_id]
    assert game.a.probe("save") == "Probe save: True"
    rows = game.server.banks.read_text().splitlines()
    saved = {
        int(m.group(1)): float(m.group(2))
        for m in (re.search(r'"gridId":(-?\d+),"frames":([^}]+)', r) for r in rows)
        if m
    }
    assert saved[refinery.entity_id] == pytest.approx(bank, abs=1)
