"""What keeps a grid revealed: an excluded block subtype, pirate ownership with
ConcealPirates off, and the remote control keep-alive action. And grids
connected by a rotor or a connector, which are concealed and revealed as one
group."""

from __future__ import annotations

import time

import grids
from conftest import wait_until
from grids import offset


def test_excluded_subtype_keeps_the_grid_revealed(game):
    game.conceal()
    for g in grids.EXCLUDED:
        assert not game.is_concealed(g), g.name


def test_without_the_exclusion_the_grid_is_concealed(game):
    game.configure(ExcludedSubtypes=[])
    game.conceal()
    for g in grids.EXCLUDED:
        assert game.is_concealed(g), g.name


def test_new_exclusion_reveals_concealed_grids(game):
    game.configure(ExcludedSubtypes=[])
    game.conceal()
    assert game.is_concealed(grids.DECOY_STATION)
    game.configure()
    game.wait_concealed(grids.DECOY_STATION, False, timeout=5)
    assert game.is_concealed(grids.PLAIN_STATION)


def test_exclusion_of_another_subtype(game):
    game.configure(ExcludedSubtypes=["LargeProgrammableBlock"])
    game.conceal()
    assert not game.is_concealed(grids.ACTIVITY)
    assert game.is_concealed(grids.DECOY_STATION)


def test_pirate_grids_are_concealed_by_default(game):
    game.conceal()
    assert game.is_concealed(grids.PIRATE_SHIP)


def test_pirate_grids_stay_revealed_without_conceal_pirates(game):
    game.configure(ConcealPirates=False)
    game.conceal()
    assert not game.is_concealed(grids.PIRATE_SHIP)
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_keep_alive_action(game):
    """The remote control action keeps its grid revealed for a concealment
    interval. Only the server has the action, so a script or a timer on the
    server is what can use it."""
    ship = grids.KEEPALIVE_SHIP
    # A 10 s concealment interval, so the keep-alive lasts 10 s. The periodic
    # scan may conceal the ship before the action, so reveal it after.
    game.configure(ConcealInterval=600)
    reply = game.a.probe("action", grids.REMOTE_CONTROL_ID, "Concealment.KeepAlive")
    assert reply == "Probe action: applied"
    start = time.monotonic()
    game.a.command("!conceal reveal all")
    game.conceal()
    assert not game.is_concealed(ship)
    assert game.is_concealed(grids.PLAIN_SHIP)

    time.sleep(max(0.0, start + 11 - time.monotonic()))
    game.conceal()
    assert game.is_concealed(ship)


def test_rotor_head_is_concealed_with_its_base(game):
    head = rotor_head(game)
    game.conceal()
    assert game.is_concealed(grids.ROTOR_BASE)
    assert head in game.concealed_ids()

    # Approaching the head reveals the base too
    game.a.teleport(offset(game.grid(head)["pos"], 60.0), settle=0)
    game.wait_concealed(grids.ROTOR_BASE, False, timeout=5)
    assert head not in game.concealed_ids()


def test_excluded_subgrid_keeps_the_group_revealed(game):
    rotor_head(game)
    # The stator is on the base; the whole group stays
    game.configure(ExcludedSubtypes=["LargeStator"])
    game.conceal()
    assert not game.is_concealed(grids.ROTOR_BASE)
    assert not set(game.grid(grids.ROTOR_BASE)["group"]) & game.concealed_ids()


def test_docked_ship_is_concealed_with_its_dock(game):
    dock(game)
    game.conceal()
    assert game.is_concealed(grids.DOCK)
    assert game.is_concealed(grids.DOCKED_SHIP)
    game.a.teleport(offset(grids.DOCKED_SHIP.position, 60.0), settle=0)
    game.wait_concealed(grids.DOCK, False, timeout=5)
    assert not game.is_concealed(grids.DOCKED_SHIP)


def rotor_head(game) -> int:
    """The rotor head's grid id, built if need be"""
    reply = game.a.probe("attach", grids.STATOR_ID)
    head = int(reply.removeprefix("Probe attach: top="))
    if head == 0:
        wait_until(
            lambda: len(game.grid(grids.ROTOR_BASE)["group"]) == 2, 10, "the rotor head"
        )
        head = int(
            game.a.probe("attach", grids.STATOR_ID).removeprefix("Probe attach: top=")
        )
    assert game.grid(grids.ROTOR_BASE)["group"] == {grids.ROTOR_BASE.entity_id, head}
    return head


def dock(game) -> None:
    """Locks the docked ship's connector to the dock's"""
    if grids.DOCKED_SHIP.entity_id not in game.grid(grids.DOCK)["group"]:
        reply = game.a.probe("connect", grids.DOCK_CONNECTOR_ID)
        assert reply == "Probe connect: status=Connected", reply
    assert game.grid(grids.DOCK)["group"] == {
        grids.DOCK.entity_id,
        grids.DOCKED_SHIP.entity_id,
    }
