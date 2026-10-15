"""The test rig: an isolated Magnetar server running Concealment from this
working copy, and two headless game clients that join it over DirectTransport
and are driven through the Remote plugin.

Every port, client id and folder comes from a test slot of the workspace
(notes/parallel-test-work/NOTE.md in the se workspace), so a run doesn't
collide with other tests on the machine. CC_TASK names the slot; it is claimed
on first use and kept until released with slot.sh. CC_SLOT skips the claim.

The server is server 0 of the slot: Magnetar config folder
~/.se-test/<task>-ds0/magnetar and DS data folder ~/.se-test/<task>-ds0/data.
It compiles Concealment, the test probe (tests/probe), Grid Backups and
DirectTransport as dev folders. Grid Backups keeps its config and backups in
~/.se-test/<task>-ds0/quasar. The clients are clients 1 and 2 of the slot:
Pulsar folder ~/.se-test/<task>-c<i>, made on first use with
new-pulsar-instance.sh, user
data ~/.se-test/<task>-c<i>-data. Each compiles Remote and DirectTransport as
dev folders. Every process starts under run-test.sh, which waits for the RAM
floor (SE_RAM_FLOOR_GIB) and holds the shared test lock.

Paths, all overridable by environment variables:

    CC_WORKSPACE             the se workspace, for its notes/ (default: the
                             folder that holds se1/plugins/concealment)
    CC_MAGNETAR              Magnetar install (~/.config/Magnetar)
    CC_PULSAR                Pulsar install the client folders are copied from
    CC_DS64                  DedicatedServer64 of the Steam install
    CC_BIN64                 Bin64 of the game client
    CC_GAME_CFG              SpaceEngineers.cfg the clients start from
    CC_REMOTE_REPO           Remote plugin working copy
    CC_DIRECT_TRANSPORT_REPO DirectTransport working copy
    CC_GRID_BACKUPS_REPO     Grid Backups working copy

The server and the clients download hub plugins from GitHub. On a machine
GitHub's API rate limits, export PULSAR_GITHUB_TOKEN: Pulsar and Magnetar read
it from the environment the rig passes on. Or put the plugins Magnetar can't
download into CC_SERVER_DEV_FOLDERS as dev folders: name|folder|file entries
separated by ";".
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

HOME = Path.home()
REPO = Path(__file__).resolve().parent.parent
TESTS = REPO / "tests"
PROBE = TESTS / "probe"
WORKSPACE = Path(os.environ.get("CC_WORKSPACE", REPO.parents[2]))
PARALLEL = WORKSPACE / "notes" / "parallel-test-work"
NEW_PULSAR = WORKSPACE / "notes" / "pulsar-dev-instances" / "new-pulsar-instance.sh"
MAGNETAR = Path(os.environ.get("CC_MAGNETAR", HOME / ".config/Magnetar"))
PULSAR = Path(os.environ.get("CC_PULSAR", HOME / ".config/Pulsar"))
STEAM = HOME / ".steam/steam/steamapps/common"
if not STEAM.exists():
    STEAM = HOME / ".steam/debian-installation/steamapps/common"
DS64 = Path(
    os.environ.get("CC_DS64", STEAM / "SpaceEngineersDedicatedServer/DedicatedServer64")
)
BIN64 = Path(os.environ.get("CC_BIN64", STEAM / "SpaceEngineers/Bin64"))
EMPTY_WORLD = DS64.parent / "Content" / "CustomWorlds" / "Empty World"
GAME_CFG = Path(
    os.environ.get("CC_GAME_CFG", HOME / ".config/SpaceEngineers/SpaceEngineers.cfg")
)
REMOTE_REPO = Path(os.environ.get("CC_REMOTE_REPO", REPO.parent / "remote"))
DIRECT_TRANSPORT_REPO = Path(
    os.environ.get("CC_DIRECT_TRANSPORT_REPO", WORKSPACE / "se1" / "direct-transport")
)
GRID_BACKUPS_REPO = Path(
    os.environ.get("CC_GRID_BACKUPS_REPO", REPO.parent / "grid-backups")
)

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))

from se_remote import RemoteAPI  # noqa: E402

TASK = os.environ.get("CC_TASK", "concealment")
TEST_ROOT = HOME / ".se-test"

CONCEALMENT_ID = "C88741F0-0AA5-4263-BD78-5D21467A84D3"
PROBE_ID = "4E0C7D52-2B8E-4F0B-9C55-6D1A0F3B7E21"
GRID_BACKUPS_ID = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C"

# Measured RSS (notes/parallel-test-work/NOTE.md); the server is a guess
CLIENT_GIB = 3.7
SERVER_GIB = 4.0


def _claim_slot() -> dict[str, int]:
    slot = os.environ.get("CC_SLOT") or _run(PARALLEL / "slot.sh", "claim", TASK)
    text = _run(PARALLEL / "slot.sh", "env", slot)
    values = {k: int(v) for k, v in re.findall(r"(\w+_BASE)=(\d+)", text)}
    values["SLOT"] = int(slot)
    return values


def _run(*command) -> str:
    return subprocess.run(
        [str(c) for c in command], check=True, capture_output=True, text=True
    ).stdout.strip()


SLOT = _claim_slot()


# ---------------------------------------------------------------------------
# Processes. Each runs in a session of its own under run-test.sh, so the whole
# process group is ours to stop.
# ---------------------------------------------------------------------------


def _alive(pid_file: Path, marker: str) -> int | None:
    try:
        pid = int(pid_file.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if marker.encode() in cmdline else None


def start_process(
    command, need_gib: float, cwd: Path, log: Path, pid_file: Path, env=None
):
    with open(log, "w") as out:
        process = subprocess.Popen(
            [str(PARALLEL / "run-test.sh"), str(need_gib), "--", *map(str, command)],
            cwd=cwd,
            env={**os.environ, **(env or {})},
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
        )
    pid_file.write_text(str(process.pid))


def _group_alive(pgid: int) -> bool:
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False


def stop_process(pid_file: Path, marker: str, timeout: float = 60.0) -> None:
    """SIGTERM to the process group we started, SIGKILL after the timeout"""
    pid = _alive(pid_file, marker)
    if pid is None:
        pid_file.unlink(missing_ok=True)
        return
    os.killpg(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while _group_alive(pid):
        if time.monotonic() > deadline:
            os.killpg(pid, signal.SIGKILL)
            break
        time.sleep(1)
    pid_file.unlink(missing_ok=True)


def profile_xml(dev_folders) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>"
        for i in dev_folders
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>"
        "<Local /><Mods /></Profile>\n"
    )


def write_sources(sources: Path, plugins: dict[str, tuple[Path, str]]) -> None:
    """The loader's dev folder sources: exactly these, name -> (folder, file)"""
    tree = ET.parse(sources)
    root = tree.getroot()
    local = root.find("LocalPluginSources")
    if local is not None:
        root.remove(local)
    local = ET.SubElement(root, "LocalPluginSources")
    for name, (folder, file) in plugins.items():
        plugin = ET.SubElement(local, "LocalPlugin")
        for tag, value in (
            ("Name", name),
            ("Folder", str(folder)),
            ("File", file),
            ("Enabled", "true"),
        ):
            ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


# ---------------------------------------------------------------------------
# Plugin configs
# ---------------------------------------------------------------------------


@dataclass
class DynamicRule:
    """A DynamicConcealmentRule of the plugin config"""

    name: str
    target_type: str
    conceal_type: str
    distance: float
    target_subtype: str = ""

    def xml(self) -> str:
        return (
            "<DynamicConcealmentRule>"
            f"<Name>{self.name}</Name><TargetType>{self.target_type}</TargetType>"
            f"<TargetSubtype>{self.target_subtype}</TargetSubtype>"
            f"<ConcealType>{self.conceal_type}</ConcealType>"
            f"<Distance>{self.distance}</Distance>"
            "</DynamicConcealmentRule>"
        )


def _value(value) -> str:
    if isinstance(value, bool):
        return str(value).lower()
    if isinstance(value, (list, tuple)):
        return "".join(
            v.xml() if isinstance(v, DynamicRule) else f"<string>{v}</string>"
            for v in value
        )
    return str(value)


def plugin_config(**options) -> str:
    """Concealment.cfg with these options, by option name; the rest keep their
    defaults. A list is a list option: strings, or DynamicRules."""
    values = "".join(f"<{k}>{_value(v)}</{k}>" for k, v in options.items())
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Settings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"{values}</Settings>\n"
    )


def grid_backups_config(**options) -> str:
    """GridBackups.cfg with these options, by option name"""
    values = "".join(f"<{k}>{_value(v)}</{k}>" for k, v in options.items())
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<GridBackupsConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"{values}</GridBackupsConfig>\n"
    )


# ---------------------------------------------------------------------------
# Server
# ---------------------------------------------------------------------------


class Server:
    """Server 0 of the slot"""

    def __init__(self):
        self.root = TEST_ROOT / f"{TASK}-ds0"
        self.config = self.root / "magnetar"
        self.data = self.root / "data"
        self.quasar = self.root / "quasar"
        self.pid_file = self.root / "server.pid"
        self.log = self.root / "server.log"
        self.port = SLOT["DS_SERVER_PORT_BASE"]
        self.world = self.data / "Saves" / "ConcealmentTest"
        self.plugin_cfg = self.data / "Concealment.cfg"
        self.banks = self.data / "ConcealmentBanks.jsonl"
        self.backups = self.quasar / "GridBackups"

    def prepare(self, admins) -> None:
        """Magnetar's config and the dedicated config. The world is written
        separately, see world.prepare."""
        if not (self.config / "Sources").exists():
            # The install's plugin sources, hub cache and compiled hub plugins
            self.config.mkdir(parents=True, exist_ok=True)
            template = MAGNETAR / "Magnetar"
            shutil.copytree(template / "Sources", self.config / "Sources")
            shutil.copy(template / "config.xml", self.config / "config.xml")
            if (template / "GitHub").exists():
                shutil.copytree(template / "GitHub", self.config / "GitHub")
        sources = {
            "concealment": (REPO, "Concealment.xml"),
            "concealment-probe": (PROBE, "ConcealmentProbe.xml"),
            "grid-backups": (GRID_BACKUPS_REPO, "GridBackups.xml"),
            "direct-transport": (DIRECT_TRANSPORT_REPO, "DirectTransportServer.xml"),
        }
        for entry in filter(
            None, os.environ.get("CC_SERVER_DEV_FOLDERS", "").split(";")
        ):
            name, folder, file = entry.split("|")
            sources[name] = (Path(folder), file)
        write_sources(self.config / "Sources" / "sources.xml", sources)
        (self.config / "Profiles").mkdir(exist_ok=True)
        (self.config / "Profiles" / "Current.xml").write_text(
            profile_xml(
                ["direct-transport", CONCEALMENT_ID, PROBE_ID, GRID_BACKUPS_ID]
            ),
            encoding="utf-8",
        )
        admins_xml = "".join(f"<unsignedLong>{a}</unsignedLong>" for a in admins)
        self.data.mkdir(parents=True, exist_ok=True)
        (self.data / "SpaceEngineers-Dedicated.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            '<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
            'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
            f"<IP>127.0.0.1</IP><SteamPort>{SLOT['DS_STEAM_PORT_BASE']}</SteamPort>"
            f"<ServerPort>{self.port}</ServerPort>"
            f"<Administrators>{admins_xml}</Administrators><Banned />"
            f"<ServerName>Concealment Test {TASK}</ServerName>"
            "<WorldName>ConcealmentTest</WorldName>"
            "<PauseGameWhenEmpty>false</PauseGameWhenEmpty>"
            "<AutoRestartEnabled>false</AutoRestartEnabled>"
            "<IgnoreLastSession>true</IgnoreLastSession>"
            "<RemoteApiEnabled>false</RemoteApiEnabled>"
            f"<LoadWorld>{self.world}</LoadWorld>"
            "</MyConfigDedicated>\n",
            encoding="utf-8",
        )
        # Grid Backups in a Quasar data folder of its own
        if self.quasar.exists():
            shutil.rmtree(self.quasar)
        (self.quasar / "GridBackups").mkdir(parents=True)

    def write_plugin_config(self, text: str | None) -> None:
        """Concealment.cfg in the server's data folder, None for no file"""
        if text is None:
            self.plugin_cfg.unlink(missing_ok=True)
        else:
            self.plugin_cfg.write_text(text, encoding="utf-8")

    def write_grid_backups_config(self, text: str) -> None:
        (self.quasar / "GridBackups" / "GridBackups.cfg").write_text(
            text, encoding="utf-8"
        )

    def backup_files(self) -> list[Path]:
        """Every grid backup Grid Backups wrote"""
        return sorted(self.backups.rglob("*.sbc")) if self.backups.exists() else []

    def pid(self) -> int | None:
        return _alive(self.pid_file, str(self.config))

    def start(self, timeout: float = 2400.0) -> None:
        """Starts the server and waits for its world. The RAM guard may hold
        the start for a while."""
        if self.pid():
            raise RuntimeError(f"The test server is already running, pid {self.pid()}")
        # The game log of the last start, kept for looking into a failure
        old_logs = self.root / "old-logs"
        old_logs.mkdir(exist_ok=True)
        for old in self.data.glob("SpaceEngineersDedicated*.log"):
            old.replace(old_logs / f"{time.strftime('%Y%m%d-%H%M%S')}-{old.name}")
        start_process(
            [
                MAGNETAR / "MagnetarInterim.bin",
                "-multiInstance",
                "-stableLogs",
                "-noimplicitmod",
                "-consent",
                "deny",
                "-config",
                self.config,
                "-ds64",
                DS64,
                "-path",
                self.data,
            ],
            SERVER_GIB,
            MAGNETAR,
            self.log,
            self.pid_file,
            {"SE_DIRECT_TRANSPORT": "1", "QUASAR_DATA_DIR": str(self.quasar)},
        )
        deadline = time.monotonic() + timeout
        while "Game ready" not in self.log.read_text(errors="replace"):
            if self.pid() is None:
                raise RuntimeError(f"The server exited, see {self.log}")
            if time.monotonic() > deadline:
                raise TimeoutError(f"The server did not get ready, see {self.log}")
            time.sleep(2)

    def stop(self) -> None:
        """SIGTERM makes Magnetar save the world and quit"""
        stop_process(self.pid_file, str(self.config), timeout=180)


# ---------------------------------------------------------------------------
# Clients
# ---------------------------------------------------------------------------


class Client:
    """Client i of the slot"""

    def __init__(self, index: int, name: str):
        self.index = index
        self.name = name
        self.pulsar = TEST_ROOT / f"{TASK}-c{index}"
        self.launcher = self.pulsar / "Interim.bin"
        self.appdata = TEST_ROOT / f"{TASK}-c{index}-data"
        self.port = SLOT["REMOTE_PORT_BASE"] + index
        self.steam_id = SLOT["CLIENT_ID_BASE"] + index
        self.pid_file = self.appdata / "game.pid"
        self.launch_log = self.appdata / "launch.log"
        self.log = self.appdata / "SpaceEngineers.log"

    def args(self, server_port: int) -> list:
        return [
            "-multiInstance",
            "-bin64",
            BIN64,
            "-lazySteam",
            "-noprompt",
            "-noupdate",
            "-nosplash",
            "-stablelogs",
            "-sources",
            "--no-steam",
            "--client-id",
            self.steam_id,
            "--client-name",
            self.name,
            "-appdata",
            self.appdata,
            "--quality",
            "minimal",
            "--resolution",
            "640x480",
            "--no-audio",
            "--headless",
            "--no-render",
            "--connect",
            f"127.0.0.1:{server_port}",
        ]

    def _ensure_pulsar(self) -> None:
        if not self.launcher.exists():
            subprocess.run(
                [str(NEW_PULSAR), str(self.pulsar), str(PULSAR)],
                check=True,
                stdout=subprocess.DEVNULL,
            )

    def _write_configs(self) -> None:
        legacy = self.pulsar / "Legacy"
        (legacy / "Profiles" / "Current.xml").write_text(
            profile_xml(["remote", "direct-transport"]), encoding="utf-8"
        )
        write_sources(
            legacy / "Sources" / "sources.xml",
            {
                "remote": (REMOTE_REPO, "Remote.xml"),
                "direct-transport": (
                    DIRECT_TRANSPORT_REPO,
                    "DirectTransportClient.xml",
                ),
            },
        )
        self.appdata.mkdir(parents=True, exist_ok=True)
        (self.appdata / "Remote.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            "<PluginConfig><Enabled>true</Enabled><ListenIP>127.0.0.1</ListenIP>"
            f"<ListenPort>{self.port}</ListenPort>"
            "<AdminPassword>SpaceEngineers</AdminPassword>"
            "<GridGetRateLimit>1000</GridGetRateLimit><GridGetBurstSize>2000</GridGetBurstSize>"
            "<GridSetRateLimit>200</GridSetRateLimit><GridSetBurstSize>400</GridSetBurstSize>"
            "</PluginConfig>\n",
            encoding="utf-8",
        )
        game_cfg = self.appdata / "SpaceEngineers.cfg"
        if not game_cfg.exists():
            shutil.copy(GAME_CFG, game_cfg)

    def pid(self) -> int | None:
        return _alive(self.pid_file, str(self.launcher))

    def launch(self, server_port: int) -> None:
        if self.pid():
            raise RuntimeError(f"Client {self.index} is already running")
        self._ensure_pulsar()
        self._write_configs()
        self.log.unlink(missing_ok=True)
        start_process(
            [self.launcher, *self.args(server_port)],
            CLIENT_GIB,
            self.pulsar,
            self.launch_log,
            self.pid_file,
            {"PULSAR_NO_RENDER": "true"},
        )

    def stop(self) -> None:
        stop_process(self.pid_file, str(self.launcher))

    def api(self) -> RemoteAPI:
        return RemoteAPI(
            f"http://127.0.0.1:{self.port}", username="admin", password="SpaceEngineers"
        )

    def wait_api(self, timeout: float = 2400.0) -> RemoteAPI:
        """The RAM guard may hold the launch for a while, so this waits as long
        as the process lives"""
        api = self.api()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self.pid():
                raise RuntimeError(f"Client {self.index} exited, see {self.launch_log}")
            try:
                api.ping()
                return api
            except Exception:  # noqa: BLE001 -- not listening yet
                time.sleep(3)
        raise TimeoutError(f"Client {self.index} has no Remote API")


def wait_world(api: RemoteAPI, timeout: float = 900.0) -> None:
    """Waits until the session is ready, clicking OK on any message box"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(1)
        try:
            boxes = [
                s
                for s in api.list_screens()
                if s.get("type") == "MyGuiScreenMessageBox"
            ]
            for box in boxes:
                api.control_click(text="OK", screen=box["index"])
            if not boxes and api.get_state().get("ready"):
                return
        except Exception:  # noqa: BLE001 -- the API times out while a world loads
            pass
    raise TimeoutError("The world did not become ready")


def ensure_character(api: RemoteAPI, timeout: float = 300.0) -> dict:
    """Respawns at the first spawn option until there is a live character"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            character = api.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        try:
            screens = api.list_screens()
            medical = next(
                (i for i, s in enumerate(screens) if "Medical" in s.get("type", "")),
                None,
            )
            if medical is not None:
                for button in ("Join", "Respawn"):
                    try:
                        api.control_click(text=button, screen=medical)
                        break
                    except Exception:  # noqa: BLE001 -- the other page of the screen
                        continue
        except Exception:  # noqa: BLE001 -- slow while the world streams in
            pass
        time.sleep(3)
    raise TimeoutError("No live character")


def focus_gameplay(api: RemoteAPI, timeout: float = 30.0) -> None:
    """Closes the welcome screen and anything else above the gameplay screen"""
    keep = ("MyGuiScreenGamePlay", "MyGuiScreenHudSpace")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        screens = [s.get("type") for s in api.list_screens()]
        extra = [i for i, kind in enumerate(screens) if kind not in keep]
        if not extra:
            return
        if "MyGuiScreenLoading" not in screens:
            api.close_screen(extra[-1])
        time.sleep(0.5)
    raise TimeoutError("Could not get back to the gameplay screen")
