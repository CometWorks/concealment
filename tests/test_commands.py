"""The !conceal chat commands: what they do and answer, who may use them, and
bad input. Player A is an administrator, player B has no rights; the
commands need space master."""

from __future__ import annotations

import math
import re
import time

import grids
import pytest
from grids import offset

NO_PERMISSION = "You do not have permission to use that command."
CONCEALABLE = [g for g in grids.ALL if g not in grids.EXCLUDED]


def concealed_count(reply: str, verb: str) -> int:
    match = re.fullmatch(rf"(\d+) grids {verb}\.", reply)
    assert match, reply
    return int(match.group(1))


@pytest.mark.parametrize(
    "command", ["run", "run 300", "reveal", "reveal 1000", "reveal all", "on", "off"]
)
def test_commands_need_space_master(game, command):
    game.conceal()
    before = game.concealed_ids()
    assert game.b.command(f"!conceal {command}") == NO_PERMISSION
    assert game.concealed_ids() == before
    assert "<Enabled>true</Enabled>" in game.server.plugin_cfg.read_text()


def test_overview_lists_the_commands(game):
    lines = game.a.command("!conceal").splitlines()
    assert lines[0] == "=== Concealment - grid concealment controls ==="
    for command in ("off", "on", "reveal", "reveal all", "run"):
        assert any(line.startswith(f"!conceal {command} - ") for line in lines), command


def test_overview_hides_the_commands_from_players(game):
    reply = game.b.command("!conceal")
    assert "!conceal run" not in reply


def test_unknown_command(game):
    assert game.a.command("!conceal hide") == "Unknown command. Try !conceal help"


def test_run_with_a_bad_distance(game):
    reply = game.a.command("!conceal run far")
    assert "Usage: !conceal run" in reply
    assert game.concealed_ids() == set()


def test_run_uses_the_configured_distance(game):
    # Sites at 1000 m, 1800 m, 2600 m and on from home
    game.configure(ConcealDistance=2000)
    reply = game.a.command("!conceal run")
    assert concealed_count(reply, "concealed") >= 1
    assert not game.is_concealed(grids.PLAIN_STATION)
    assert not game.is_concealed(grids.PLAIN_SHIP)
    assert game.is_concealed(grids.DRIFTER)


def test_run_zero_uses_the_configured_distance(game):
    game.configure(ConcealDistance=2000)
    game.a.command("!conceal run 0")
    assert not game.is_concealed(grids.PLAIN_SHIP)
    assert game.is_concealed(grids.DRIFTER)


def test_run_with_a_distance(game):
    reply = game.a.command("!conceal run 1500")
    concealed = game.concealed_ids()
    assert concealed_count(reply, "concealed") >= len(CONCEALABLE) - 1
    assert grids.PLAIN_STATION.entity_id not in concealed
    assert grids.PLAIN_SHIP.entity_id in concealed


def test_run_again_conceals_nothing_new(game):
    game.conceal()
    assert game.conceal() == 0


@pytest.mark.xfail(
    strict=True,
    reason="a negative distance conceals the grids next to the players",
)
def test_run_with_a_negative_distance(game):
    game.a.teleport(offset(grids.PLAIN_STATION.position, 200.0))
    game.a.command("!conceal run -1")
    assert not game.is_concealed(grids.PLAIN_STATION)


def test_run_while_disabled(game):
    """`run` works whether the periodic scans are enabled or not"""
    game.configure(Enabled=False)
    game.conceal()
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_reveal_all(game):
    game.conceal()
    reply = game.a.command("!conceal reveal all")
    # The projector's projection may be counted when concealing, but it is
    # gone by the time the projector is back on
    assert concealed_count(reply, "revealed") >= len(CONCEALABLE)
    assert game.concealed_ids() == set()


def test_reveal_all_with_nothing_concealed(game):
    assert game.a.command("!conceal reveal all") == "0 grids revealed."


def test_reveal_around_the_caller(game):
    game.conceal()
    # 500 m from the plain ship's site, 943 m from the sites beside it
    caller = offset(grids.PLAIN_SHIP.position, 500.0)
    game.a.teleport(caller)
    reply = game.a.command("!conceal reveal")
    revealed = {g.name for g in CONCEALABLE if not game.is_concealed(g)}
    near = {g.name for g in CONCEALABLE if math.dist(g.position, caller) < 990}
    assert revealed == near
    assert concealed_count(reply, "revealed") == len(near)


def test_reveal_with_a_distance(game):
    game.conceal()
    reply = game.a.command("!conceal reveal 1200")
    assert concealed_count(reply, "revealed") == 1
    assert not game.is_concealed(grids.PLAIN_STATION)
    assert game.is_concealed(grids.PLAIN_SHIP)


def test_reveal_with_a_bad_distance(game):
    game.conceal()
    reply = game.a.command("!conceal reveal near")
    assert "Usage: !conceal reveal" in reply
    assert game.is_concealed(grids.PLAIN_STATION)


def test_off_reveals_and_disables(game):
    game.configure(ConcealInterval=120)
    game.conceal()
    reply = game.a.command("!conceal off")
    assert re.fullmatch(r"Concealment disabled\. \d+ grids revealed\.", reply), reply
    assert game.concealed_ids() == set()
    assert "<Enabled>false</Enabled>" in game.server.plugin_cfg.read_text()
    # No periodic scan any more
    time.sleep(5)
    assert game.concealed_ids() == set()


def test_on_enables_and_conceals(game):
    game.configure(Enabled=False)
    reply = game.a.command("!conceal on")
    match = re.fullmatch(r"Concealment enabled\. (\d+) grids concealed\.", reply)
    assert match, reply
    assert int(match.group(1)) >= len(CONCEALABLE)
    assert game.is_concealed(grids.PLAIN_STATION)
    assert "<Enabled>true</Enabled>" in game.server.plugin_cfg.read_text()
