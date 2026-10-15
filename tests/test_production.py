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
    """The refinery's state. It refines in production updates, every 60
    frames near a player and up to every 240 far from all of them, each the
    ore of the frames since the last one: `updated` is the frame of the last
    update, the one its input is from."""
    state = parse(game.a.probe("production", grids.REFINERY_ID))
    frame = int(state["frame"])
    return {
        "producing": state["producing"] == "1",
        "input": float(state["input"]),
        "power": float(state["power"]),
        "frame": frame,
        "updated": frame - int(state["since"]),
    }


def rate_between(start: dict, end: dict) -> float:
    """Ore refined per second between the production updates of two states"""
    frames = end["updated"] - start["updated"]
    assert frames > 0, (start, end)
    return (start["input"] - end["input"]) / (frames / 60)


def rate(game, seconds: float) -> float:
    """Ore refined per second, over probes this far apart. At least 4 s, so
    an update comes between them."""
    start = production(game)
    time.sleep(seconds)
    return rate_between(start, production(game))


def refining_after(game, before: dict) -> dict:
    """The refinery's state once it has refined since `before`. Right after a
    reveal it refines nothing for an update or two, until its power is back."""
    wait_until(
        lambda: production(game)["input"] < before["input"], 20, "the refinery refining"
    )
    return production(game)


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
    refining_after(game, start)
    assert grids.REFINERY.entity_id not in banks(game)


def test_approximate_catches_up(game, refinery):
    """The time concealed divided by the boost level is banked, and the
    refinery works at five times its speed until the bank is spent"""
    game.configure(ProductionConcealment="Approximate", ProductionBoostLevel=5)
    normal = rate(game, 10)
    assert normal > 0

    before = production(game)["frame"]
    game.conceal()
    concealed = production(game)["frame"]
    # 20 s of boost, long enough to measure the boosted rate in it
    time.sleep(100)
    last_concealed = production(game)["frame"]
    reply = game.a.command("!conceal reveal all")
    bank = banks(game).get(refinery.entity_id)
    revealed = production(game)
    assert bank is not None, reply
    # The bank drains by the frames since the reveal, while it boosts
    assert (
        (last_concealed - concealed) / 5 - (revealed["frame"] - last_concealed)
        <= bank
        <= (revealed["frame"] - before) / 5
    )

    # The bank lasts from the first update that refines, so the rate is taken
    # from there, while it lasts
    start = refining_after(game, revealed)
    time.sleep(5)
    boosted = rate_between(start, production(game))
    assert refinery.entity_id in banks(game), "the bank lasted the measurement"
    assert boosted == pytest.approx(5 * normal, rel=0.1)

    wait_until(lambda: refinery.entity_id not in banks(game), 30, "the bank spent")
    assert rate(game, 10) == pytest.approx(normal, rel=0.1)


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
