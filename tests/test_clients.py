"""Concealment as the players see it: a concealed grid on a client, one player
revealing a grid for the other, dynamic concealment rules that depend on who
is near, and a player logging back in to a cryo chamber on a concealed
grid."""

from __future__ import annotations

import math
import time

import grids
import session
from conftest import wait_until
from grids import offset
from rig import DynamicRule
from session import parse

TIMER_RULE = "Timers near friends"


def client_grids(player) -> dict[int, dict]:
    return {g["entityId"]: g for g in player.api.list_grids()}


def timer_ms(game) -> int:
    return int(parse(game.a.probe("timer", grids.TIMER_ID))["ms"])


def timer_runs(game, seconds: float = 3.0) -> bool:
    start = timer_ms(game)
    time.sleep(seconds)
    return timer_ms(game) < start


def test_client_sees_a_concealed_grid(game):
    ship = grids.PLAIN_SHIP
    # Outside the reveal distance, well inside the sync distance
    game.a.teleport(offset(ship.position, 400.0), settle=3)
    seen = client_grids(game.a)[ship.entity_id]

    game.conceal(distance=300)
    assert game.is_concealed(ship)
    time.sleep(2)
    concealed = client_grids(game.a)[ship.entity_id]
    assert concealed == seen

    game.a.teleport(offset(ship.position, 50.0), settle=0)
    game.wait_concealed(ship, False, timeout=5)
    time.sleep(1)
    on_client = [g for g in game.a.api.list_grids() if g["name"] == ship.name]
    assert [g["entityId"] for g in on_client] == [ship.entity_id]


def test_one_player_reveals_for_the_other(game):
    """B arrives after the grid was concealed: B's client gets it all the same"""
    station = grids.SHARED_STATION
    game.conceal()
    assert game.is_concealed(station)
    game.b.teleport(offset(station.position, 400.0), settle=0)
    wait_until(
        lambda: station.entity_id in client_grids(game.b),
        30,
        "the station on B's client",
    )

    game.a.teleport(offset(station.position, -50.0), settle=0)
    game.wait_concealed(station, False, timeout=5)
    assert station.entity_id in client_grids(game.b)


def test_dynamic_rule_skips_updates_while_nobody_is_near(game):
    game.configure(
        DynamicConcealment=[
            DynamicRule(TIMER_RULE, "TimerBlock", "FriendlyCharacters", 100)
        ]
    )
    time.sleep(2)
    assert not timer_runs(game)

    game.a.teleport(offset(grids.ACTIVITY.position, 50.0))
    wait_until(lambda: timer_runs(game, 2), 20, "the timer counting with A near")

    game.a.go_home()
    wait_until(lambda: not timer_runs(game, 2), 20, "the timer stopping with A gone")


def test_dynamic_rule_tells_friends_from_enemies(game):
    """Player B is in no faction, so A's timer counts B as hostile"""
    game.configure(
        DynamicConcealment=[
            DynamicRule(TIMER_RULE, "TimerBlock", "FriendlyCharacters", 100)
        ]
    )
    game.b.teleport(offset(grids.ACTIVITY.position, 50.0), settle=5)
    assert not timer_runs(game)

    game.configure(
        DynamicConcealment=[
            DynamicRule("Timers near enemies", "TimerBlock", "HostileCharacters", 100)
        ]
    )
    wait_until(lambda: timer_runs(game, 2), 20, "the timer counting with B near")


def test_dynamic_rule_by_subtype(game):
    game.configure(
        DynamicConcealment=[
            DynamicRule(
                "Other timers",
                "TimerBlock",
                "FriendlyCharacters",
                100,
                "TimerBlockSmall",
            )
        ]
    )
    time.sleep(2)
    assert timer_runs(game)


def test_dynamic_rule_with_a_bad_type_is_ignored(game):
    game.configure(
        DynamicConcealment=[
            DynamicRule("Nothing", "NoSuchBlock", "FriendlyCharacters", 100)
        ]
    )
    time.sleep(2)
    assert timer_runs(game)


def test_cryo_chamber_reveals_its_grid_when_its_player_joins(game):
    """B logs off in a cryo chamber; its grid is concealed while B is away and
    revealed as B logs back in, before B could come near it"""
    b = game.b
    cryo = grids.CRYO_STATION
    # The chamber's door faces -z; cell (1, 1, 1) is 2.5 m from the grid's origin
    chamber = offset(cryo.position, 2.5, 2.5, 2.5)
    # The character settles after the teleport; until then it may face away
    for _ in range(5):
        b.teleport(offset(chamber, 0.0, 0.0, -3.0))
        b.api.character_look_at(*chamber, tolerance=1)
        used = b.api.character_use()
        if used.get("used"):
            break
    assert used.get("used"), used
    wait_until(
        lambda: (b.api.get_character().get("controlledEntity") or {}).get("entityId")
        == grids.CRYO_ID,
        10,
        "B in the cryo chamber",
    )
    try:
        session.CLIENT_B.stop()
        # No reveal scan from here on: only the join may reveal it
        game.configure(RevealInterval=864000)
        game.conceal()
        assert game.is_concealed(cryo)

        session.join(session.CLIENT_B)
        game.b = session.players()[1]
        wait_until(lambda: not game.is_concealed(cryo), 60, "the cryo station revealed")
    finally:
        game.configure()
        game.b.go_home()
        position = parse(game.a.probe("players"))[str(session.CLIENT_B.steam_id)]
        assert math.dist(tuple(float(v) for v in position.split(",")), grids.HOME) < 100
