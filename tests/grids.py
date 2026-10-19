"""Every grid of the test world.

Each grid sits at a site of its own. The sites lie in a row along -z, far from
the players' home and from each other, so a player at one site reveals only
the grids there. Most tests have a grid or two of their own.

Functional blocks belong to player A unless said otherwise.
"""

from __future__ import annotations

from dataclasses import dataclass

import world
from world import PLAYER_A, block, grid

# The players wait here between tests, farther than CONCEAL_RUN_M from every
# site
HOME = (0.0, 0.0, 0.0)
FIRST_SITE_M = 1000.0
SITE_SPACING_M = 800.0

# What every test conceals with: `!conceal run 300` hides every grid farther
# than 300 m from both players, so every site while they are at home
CONCEAL_RUN_M = 300


def site(i: int):
    return (0.0, 0.0, -(FIRST_SITE_M + SITE_SPACING_M * i))


def offset(position, dx=0.0, dy=0.0, dz=0.0):
    return (position[0] + dx, position[1] + dy, position[2] + dz)


@dataclass
class Grid:
    name: str
    entity_id: int
    position: tuple
    xml: str


_grids: list[Grid] = []


def _id(k: int) -> int:
    return 900_000_000_000 + 1000 * k


def add(
    name: str, position, blocks: str, static: bool = True, size: str = "Large"
) -> Grid:
    entity_id = _id(len(_grids))
    g = Grid(
        name, entity_id, position, grid(name, entity_id, blocks, position, static, size)
    )
    _grids.append(g)
    return g


def armor(cells, size: str = "Large") -> str:
    return "".join(block("CubeBlock", world.ARMOR[size], c) for c in cells)


def box(x: int, y: int, z: int):
    return [(i, j, k) for i in range(x) for j in range(y) for k in range(z)]


def battery(cell, entity_id: int, owner: int = PLAYER_A) -> str:
    return block(
        "BatteryBlock",
        "LargeBlockBatteryBlock",
        cell,
        built_by=owner,
        entity_id=entity_id,
        extra="<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"
        "<ProducerEnabled>true</ProducerEnabled>",
    )


def decoy(cell, entity_id: int) -> str:
    return block(
        "Decoy",
        "LargeDecoy",
        cell,
        built_by=PLAYER_A,
        entity_id=entity_id,
        extra="<Enabled>true</Enabled>",
    )


# The block subtype the tests put into ExcludedSubtypes
EXCLUDED_SUBTYPE = "LargeDecoy"

# Grid ids of a site are the grid's id plus a small number; blocks get the
# grid's id plus 100 and up.

# --- plain grids --------------------------------------------------------------

PLAIN_STATION = add(
    "Plain station",
    site(0),
    armor(box(3, 3, 3)) + battery((1, 3, 1), _id(len(_grids)) + 100),
)
PLAIN_SHIP = add(
    "Plain ship",
    site(1),
    armor(box(3, 1, 3)) + battery((1, 1, 1), _id(len(_grids)) + 100),
    static=False,
)
# Set moving by the tests
DRIFTER = add(
    "Drifter",
    site(2),
    armor(box(3, 1, 3)) + battery((1, 1, 1), _id(len(_grids)) + 100),
    static=False,
)

# --- activity -----------------------------------------------------------------

# A timer counting down from an hour and a programmable block counting its
# 10-tick runs into its custom data
PB_SCRIPT = """int n;
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    if ((updateSource & UpdateType.Update10) == 0) return;
    n++;
    Me.CustomData = n.ToString();
}"""


def _escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


ACTIVITY_ID = _id(len(_grids))
TIMER_ID = ACTIVITY_ID + 101
PB_ID = ACTIVITY_ID + 102
ACTIVITY = add(
    "Activity",
    site(3),
    armor(box(5, 1, 5))
    + battery((0, 1, 0), ACTIVITY_ID + 100)
    + block(
        "TimerBlock",
        "TimerBlockLarge",
        (2, 1, 0),
        built_by=PLAYER_A,
        entity_id=TIMER_ID,
        extra="<Enabled>true</Enabled><Delay>3600000</Delay>"
        "<CurrentTime>3600000</CurrentTime><IsCountingDown>true</IsCountingDown>",
    )
    + block(
        "MyProgrammableBlock",
        "LargeProgrammableBlock",
        (4, 1, 0),
        built_by=PLAYER_A,
        entity_id=PB_ID,
        extra=f"<Enabled>true</Enabled><Program>{_escape(PB_SCRIPT)}</Program>",
    ),
)

# A ship with a small ion thruster pushing it along -x, away from the other
# sites; the tests set its override
THRUSTER_SHIP_ID = _id(len(_grids))
THRUSTER_ID = THRUSTER_SHIP_ID + 101
THRUSTER_SHIP = add(
    "Thruster ship",
    site(4),
    armor(box(3, 3, 3))
    + battery((1, 3, 1), THRUSTER_SHIP_ID + 100)
    + block(
        "Thrust",
        "LargeBlockSmallThrust",
        (3, 1, 1),
        built_by=PLAYER_A,
        entity_id=THRUSTER_ID,
        forward="Right",
        extra="<Enabled>true</Enabled>",
    ),
    static=False,
)

# A refinery with two batteries; the tests load its ore
REFINERY_GRID_ID = _id(len(_grids))
REFINERY_ID = REFINERY_GRID_ID + 102
REFINERY = add(
    "Refinery",
    site(5),
    armor(box(5, 1, 5))
    + battery((0, 1, 0), REFINERY_GRID_ID + 100)
    + battery((0, 1, 4), REFINERY_GRID_ID + 101)
    + block(
        "Refinery",
        "LargeRefinery",
        (2, 1, 2),
        built_by=PLAYER_A,
        entity_id=REFINERY_ID,
        extra="<Enabled>true</Enabled>",
    ),
)

# --- ownership and exclusions -------------------------------------------------

PIRATE_SHIP = add(
    "Pirate ship",
    site(6),
    armor(box(3, 1, 3)) + battery((1, 1, 1), _id(len(_grids)) + 100, owner=world.NPC),
    static=False,
)
DECOY_STATION = add(
    "Decoy station",
    site(7),
    armor(box(3, 1, 3)) + decoy((1, 1, 1), _id(len(_grids)) + 100),
)

# --- connected grids ----------------------------------------------------------

# A rotor base; the tests build its head, a grid of its own
ROTOR_BASE_ID = _id(len(_grids))
STATOR_ID = ROTOR_BASE_ID + 101
ROTOR_BASE = add(
    "Rotor base",
    site(8),
    armor(box(3, 1, 3))
    + battery((0, 1, 0), ROTOR_BASE_ID + 100)
    + block(
        "MotorStator",
        "LargeStator",
        (1, 1, 1),
        built_by=PLAYER_A,
        entity_id=STATOR_ID,
        extra="<Enabled>true</Enabled>",
    ),
)

# A station and a ship whose connectors face each other; the tests lock them.
# The dock's connector at (3, 0, 1) faces +x, the ship's at its (0, 0, 0)
# faces -x, one cell further.
DOCK_ID = _id(len(_grids))
DOCK_CONNECTOR_ID = DOCK_ID + 101
DOCK = add(
    "Dock",
    site(9),
    armor(box(3, 1, 3))
    + battery((1, 1, 1), DOCK_ID + 100)
    + block(
        "ShipConnector",
        "Connector",
        (3, 0, 1),
        built_by=PLAYER_A,
        entity_id=DOCK_CONNECTOR_ID,
        forward="Right",
        extra="<Enabled>true</Enabled>",
    ),
)
DOCKED_SHIP_ID = _id(len(_grids))
DOCKED_SHIP = add(
    "Docked ship",
    offset(site(9), 2.5 * 4, 0.0, 2.5 * 1),
    block(
        "ShipConnector",
        "Connector",
        (0, 0, 0),
        built_by=PLAYER_A,
        entity_id=DOCKED_SHIP_ID + 101,
        forward="Left",
        extra="<Enabled>true</Enabled>",
    )
    + armor([(1, 0, 0), (2, 0, 0), (1, 0, 1), (2, 0, 1)])
    + battery((1, 1, 0), DOCKED_SHIP_ID + 100),
    static=False,
)

# --- what other systems see ---------------------------------------------------

# A sensor on an excluded grid, and a ship 25 m from it
SENSOR_POST_ID = _id(len(_grids))
SENSOR_ID = SENSOR_POST_ID + 102
SENSOR_POST = add(
    "Sensor post",
    site(10),
    armor(box(3, 1, 3))
    + decoy((0, 1, 0), SENSOR_POST_ID + 100)
    + battery((2, 1, 0), SENSOR_POST_ID + 101)
    + block(
        "SensorBlock",
        "LargeBlockSensor",
        (1, 1, 1),
        built_by=PLAYER_A,
        entity_id=SENSOR_ID,
        extra="<Enabled>true</Enabled>"
        '<FieldMin x="-50" y="-50" z="-50" /><FieldMax x="50" y="50" z="50" />'
        "<DetectPlayers>false</DetectPlayers><DetectSmallShips>true</DetectSmallShips>"
        "<DetectLargeShips>true</DetectLargeShips><DetectStations>false</DetectStations>",
    ),
)
SENSED_SHIP = add(
    "Sensed ship",
    offset(site(10), 0.0, 0.0, -25.0),
    armor(box(3, 1, 1)) + battery((1, 1, 0), _id(len(_grids)) + 100),
    static=False,
)

# An excluded wall, and a ship 60 m in front of it that the tests ram into it
WALL = add(
    "Wall",
    site(11),
    armor([c for c in box(7, 7, 1) if c != (3, 3, 0)])
    + decoy((3, 3, 0), _id(len(_grids)) + 100),
)
RAMMER = add(
    "Rammer",
    offset(site(11), 2.5 * 2, 2.5 * 2, 60.0),
    armor(box(3, 1, 3)) + battery((1, 1, 1), _id(len(_grids)) + 100),
    static=False,
)

# A projector showing four heavy armor blocks next to it
PROJECTOR_GRID_ID = _id(len(_grids))
PROJECTOR_ID = PROJECTOR_GRID_ID + 101


def _projection_xml() -> str:
    projected = block("Projector", "LargeProjector", (0, 1, 0)) + "".join(
        block("CubeBlock", "LargeHeavyBlockArmorBlock", c)
        for c in [(1, 1, 2), (2, 1, 2), (3, 1, 2), (2, 1, 1)]
    )
    return (
        "<MyObjectBuilder_CubeGrid>"
        "<SubtypeName /><EntityId>0</EntityId>"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{projected}</CubeBlocks>"
        "<DisplayName>Projection</DisplayName></MyObjectBuilder_CubeGrid>"
    )


PROJECTOR_GRID = add(
    "Projector",
    site(12),
    armor(box(6, 1, 6))
    + battery((0, 1, 5), PROJECTOR_GRID_ID + 100)
    + block(
        "Projector",
        "LargeProjector",
        (0, 1, 0),
        built_by=PLAYER_A,
        entity_id=PROJECTOR_ID,
        extra="<CustomName>Test Projector</CustomName><Enabled>true</Enabled>"
        f"<ProjectedGrids>{_projection_xml()}</ProjectedGrids>"
        "<KeepProjection>true</KeepProjection><ShowOnlyBuildable>false</ShowOnlyBuildable>",
    ),
)

# --- players ------------------------------------------------------------------

# A cryo chamber facing -z, for player B to log off in
CRYO_STATION_ID = _id(len(_grids))
CRYO_ID = CRYO_STATION_ID + 101
CRYO_STATION = add(
    "Cryo station",
    site(13),
    armor(box(3, 1, 3))
    + battery((0, 1, 2), CRYO_STATION_ID + 100)
    + block(
        "CryoChamber",
        "LargeBlockCryoChamber",
        (1, 1, 1),
        built_by=world.PLAYER_B,
        entity_id=CRYO_ID,
        extra="<Enabled>true</Enabled>",
    ),
)

# A ship with a remote control, for the keep-alive action
KEEPALIVE_SHIP_ID = _id(len(_grids))
REMOTE_CONTROL_ID = KEEPALIVE_SHIP_ID + 101
KEEPALIVE_SHIP = add(
    "Keepalive ship",
    site(14),
    armor(box(3, 1, 3))
    + battery((0, 1, 0), KEEPALIVE_SHIP_ID + 100)
    + block(
        "RemoteControl",
        "LargeBlockRemoteControl",
        (2, 1, 2),
        built_by=PLAYER_A,
        entity_id=REMOTE_CONTROL_ID,
        extra="<Enabled>true</Enabled>",
    ),
    static=False,
)

SHARED_STATION = add(
    "Shared station",
    site(15),
    armor(box(3, 1, 3)) + battery((1, 1, 1), _id(len(_grids)) + 100),
)

# --- Grid Backups -------------------------------------------------------------

# The only grid with Grid Backups' MinBlocksForBackup blocks (session.py)
BACKUP_STATION = add(
    "Backup station",
    site(16),
    armor(box(5, 3, 5)) + battery((2, 3, 2), _id(len(_grids)) + 100),
)

ALL = list(_grids)
# The grids an exclusion keeps revealed in the default test config
EXCLUDED = [DECOY_STATION, SENSOR_POST, WALL]


def xml() -> str:
    return "".join(g.xml for g in ALL)
