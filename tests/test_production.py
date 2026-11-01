"""Active production and the three ProductionConcealment modes: Off keeps a
grid with an active refinery revealed, Conceal pauses it, Approximate settles
paid, stock-limited catch-up before reveal. Legacy banks are drain-only."""

from __future__ import annotations

import json
import time

import grids
import pytest
from conftest import wait_until
from session import parse

ORE = "Cobalt"
ORE_KG = 2000


@pytest.fixture
def refinery(game):
    """The refinery, refining, with no legacy credit"""
    game.a.probe("clear", grids.REFINERY_ID)
    assert game.a.probe("ore", grids.REFINERY_ID, ORE, ORE_KG).startswith("Probe ore:")
    wait_until(lambda: production(game)["producing"], 10, "the refinery producing")
    yield grids.REFINERY
    game.a.probe("clear", grids.REFINERY_ID)


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


def claims(game) -> list[dict]:
    return json.loads(game.a.probe("accounting").removeprefix("Probe accounting: "))


def claim(game, grid) -> dict:
    return next(c for c in claims(game) if grid.entity_id in c["Members"])


def test_approximate_catches_up_before_reveal(game, refinery):
    game.configure(ProductionConcealment="Approximate")
    normal = rate(game, 10)
    assert normal > 0
    game.conceal()
    game.wait_concealed(refinery, True, timeout=60)
    frozen = production(game)
    epoch = claim(game, refinery)["Epoch"]
    time.sleep(12)
    assert production(game)["input"] == frozen["input"]
    game.a.command("!conceal reveal all")
    revealed = production(game)
    assert frozen["input"] - revealed["input"] > normal * 8
    applied = claim(game, refinery)
    assert applied["Epoch"] == epoch and not applied["Concealed"]
    assert applied["Profiles"] is None and applied["AppliedThrough"] > 0
    assert refinery.entity_id not in banks(game)
    game.a.command("!conceal reveal all")
    assert claim(game, refinery)["AppliedThrough"] == applied["AppliedThrough"]
    assert rate(game, 10) == pytest.approx(normal, rel=0.1)


def test_zero_elapsed_cap_still_closes_the_interval(game, refinery):
    game.configure(ProductionConcealment="Approximate", OfflineCompensationMaxHours=0)
    game.conceal()
    game.wait_concealed(refinery, True, timeout=60)
    time.sleep(3)
    game.a.command("!conceal reveal all")
    applied = claim(game, refinery)
    assert not applied["Concealed"] and applied["AppliedThrough"] > 0
    assert refinery.entity_id not in banks(game)
    game.a.command("!conceal reveal all")
    assert claim(game, refinery)["AppliedThrough"] == applied["AppliedThrough"]


def test_approximate_idle_grid_gets_no_bank(game):
    game.configure(ProductionConcealment="Approximate")
    game.a.probe("clear", grids.REFINERY_ID)
    game.conceal()
    game.wait_concealed(grids.REFINERY, True, timeout=60)
    game.a.command("!conceal reveal all")
    assert grids.REFINERY.entity_id not in banks(game)


def test_claim_is_saved_with_native_world_stocks(game, refinery):
    game.configure(ProductionConcealment="Approximate")
    game.conceal()
    game.wait_concealed(refinery, True, timeout=60)
    current = claim(game, refinery)
    assert current["Concealed"] and current["Profiles"] and current["StockHash"]
    assert game.a.probe("save") == "Probe save: True"
    sector = game._sector()
    assert current["Epoch"] in sector and current["StockHash"] in sector
