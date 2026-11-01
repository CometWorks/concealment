# Concealment

Space Engineers grid concealment for Pulsar clients and Magnetar servers.

Concealment pauses distant grid groups to reduce simulation load. It runs in
offline games, on the host of friends multiplayer, and on dedicated Magnetar
servers. Joining clients leave simulation control to the host; guests do not
need Concealment installed. In online multiplayer, install the server plugin on
Magnetar to enable concealment for that world. Installing only the client plugin
does not enable concealment on someone else's server.

## License

Copyright (c) 2026 CometWorks.

Concealment is free software licensed under the [GNU Affero General Public
License version 3 only](LICENSE) (`AGPL-3.0-only`), without any warranty.

The licensing transition is dated 1 November 2026. The last MIT snapshot is
preserved by the [`archive/mit` tag](https://github.com/CometWorks/concealment/releases/tag/archive/mit)
at commit `087a234a085e9257f6d96c4233e8b8fc3f84b5e2`.
Permissions already granted under MIT remain in effect. The original MIT notice
is retained in [NOTICE](NOTICE) for inherited code; the current distribution is
not dual-licensed.

If you modify Concealment and let users interact with it over a network, AGPL
section 13 requires a prominent offer of your version's Corresponding Source,
including your changes, at no charge. When distributing binaries, meet the
Corresponding Source requirements of section 6 and keep the applicable copyright,
license and warranty notices. Source must match the version you run or distribute;
an upstream link alone does not provide the source of a modified fork.

Source is available at https://github.com/CometWorks/concealment, with build
instructions below. Binary builds include `LICENSE` and `NOTICE` alongside the
plugin; local Magnetar deployment keeps them in `Local/Concealment.licenses`.
Separately licensed dependencies retain their own licenses and notices.

## Prerequisites

- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Space Engineers](https://store.steampowered.com/app/244850/) and
  [Pulsar](https://github.com/SpaceGT/Pulsar) for offline and player-hosted games
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Projects

- `ClientPlugin` - Pulsar entry point, in-game settings, and local host commands.
- `ServerPlugin` - Magnetar entry point, Plugin SDK configuration and server commands.
- `Shared` - the concealment engine, production catch-up, configuration and helpers.

The build and client settings infrastructure follow
[`server-plugin-template` at `3980405`](https://github.com/CometWorks/server-plugin-template/tree/3980405bc77e776bf0bb88f65aefe59e891aabf7).
The two loaders share one engine. Only an authoritative, ready world starts it;
unloading a world reveals its grids and removes this plugin's session patches.

## Build

Install the game, Dedicated Server, Magnetar and the .NET SDK, then build both targets:

```sh
dotnet build Concealment.sln -c Debug
```

The outputs on Linux are:

```text
ClientPlugin/bin/Debug/net10.0/Concealment.dll
ServerPlugin/bin/Debug/net10.0/Concealment.dll
```

Windows builds both `net48` (Legacy) and `net10.0` (Interim). Linux builds `net10.0`.
You can build either `.csproj` separately when only that target's prerequisites are installed.

The plugin version lives in `Version.Build.props`. The loaders compile from source
without it, so keep the assembly attributes in both entry points in step with it.

`Directory.Build.props` auto-detects the game (`Bin64`), Dedicated Server (`Dedicated64`) and Magnetar
installation holding `PluginSdk.dll` (`Magnetar`). To override them, put your local paths into
`Directory.Build.props.user`, which is not committed. Running `setup.py` writes that file for you.

## Configuration

Magnetar keeps its Plugin SDK configuration and Quasar editor. Pulsar exposes an
in-game settings dialog for offline and player-hosted worlds. Both persist
`Concealment.cfg` under their game's user-data folder, with the same XML options
and defaults. Client settings apply only to worlds you host, never to a remote
server. Structured dynamic rules can be edited in that XML file while the game
is closed; the dialog exposes their query and scan intervals.

The host can use `!conceal run [distance]`, `!conceal reveal [distance]`,
`!conceal reveal all`, `!conceal on` and `!conceal off` in chat. On Magnetar,
these remain server commands requiring Space Master permission. A joining
client forwards chat commands to the server as usual.

Client-hosted production banks retain their existing boost implementation and are stored in `ConcealmentBanks.jsonl` inside
each world's save folder, so switching worlds does not mix catch-up time.
Magnetar retains its existing bank file under the server's user-data folder.

## Offline accounting

Standalone Magnetar Concealment enables whole-group accounting by default with `Approximate`.
It profiles eligible groups while live, suspends their native updates, and settles
production, power, fuel, gas, storage and zone upkeep before revealing them. The
elapsed compensation cap defaults to 72 hours (`OfflineCompensationMaxHours`);
excess is forfeited through the actual reveal time. Explicit `Off` and `Conceal`
choices keep their meanings. The cap uses elapsed world time (225,000 clock units per hour). Native world time advances 16 ms per engine frame;
resource phases retain native 60 Hz units, and server downtime earns nothing.

Profiles, stock identities and application receipts live in grid object builders,
with the world checkpoint clock. Changed stock/content/topology or a torn native
checkpoint is quarantined without overwriting stocks or revealing the group.
Keep the complete world and the matching plugin together for backup or rollback.
Old binaries cannot interpret new claims; downgrade only with a pre-migration world.

`ConcealmentBanks.jsonl` is immutable migration provenance. Existing positive banks
drain using the original configured native production/power multiplier. They create
no new grants and exclude the entire physical/logical accounting group from new
compensation until spent. `MaxBoostHours` still means old ONLINE bank units, never
elapsed offline hours. Remaining bank and import identity are saved with native
stocks. Changed identity/topology quarantines credit; a merge retains removed-grid
credit on its survivor. Restore the cutover/provenance files with the world; their
absence or disagreement cannot safely reconstruct historical stock entitlement.
The old file format did not provide a transaction with native world saves.

A nonblank `CLUSTER_GATEWAY_REGISTRY` fixes configured Cluster mode before managers
or hooks are created. All autonomous Concealment scans, dynamic suppression, jobs,
boost migration, commands and disposal reveal are inactive in that mode, regardless
of connection/provider health or saved settings. Mode changes require a restart.
Cluster explicitly invokes accounting contract 1, owns scheduling, authority clocks,
partition Finals and application, and loads this same plugin implementation.
No Cluster dependency or scheduler is required for standalone use.

## Grid Backups

If the server also runs [Grid Backups](https://github.com/CometWorks/grid-backups), Concealment
asks it for a backup of each grid group just before concealing it. Grid Backups decides whether
to write one: its `BackupBeforeConcealment` option, its grid filters (`BackupNobodyGrids`,
`BackupNpcGrids`, `MinBlocksForBackup`) and its minimum time between two backups of a grid
(`MinMinutesSinceLastSave`) apply. Grid Backups 1.1.0 and later answer that before Concealment
takes the grids' object builders, so a group that gets no backup costs next to nothing.

## Development

Load the working copy through the loader's development folders: start Pulsar or
Magnetar with `-sources` and add the repository with the Sources button. Choose
`ConcealmentClient.xml` for Pulsar and `Concealment.xml` for Magnetar.

Builds deploy nothing by default. To copy the build into Magnetar's `Local` plugin folder, set
`MagnetarData` to the Magnetar config folder (the one holding `Local`, `Sources` and `Profiles`)
in `Directory.Build.props.user`, or pass it to a single build:

```sh
dotnet build Concealment.sln -p:MagnetarData=$HOME/.config/Magnetar/Magnetar
```

Set `Pulsar` to deploy the client build into its `Legacy/Local/Concealment` or
`Interim/Local/Concealment` folder. Both deployment paths include the license and
notices. `Pulsar` and `MagnetarData` are empty by default, so builds deploy nothing.

`Concealment.xml` retains the existing MagnetarHub identity. `ConcealmentClient.xml`
is the separate Pulsar descriptor; publishing to PluginHub is a separate step.

## Tests

The portable runtime checks exercise the actual shared runtime, client commands
and config against small game-boundary stubs, using real Harmony patches:

```sh
dotnet run --project tests/runtime/RuntimeTests.csproj -c Release
```

They cover host/client authority, loading and paused worlds, host-to-client
transitions, world cleanup, recovery after a failure, keeping other plugins'
patches, command validation, and config persistence. They do not launch the game.

`tests/` holds an in-game test suite. It starts a Magnetar server with this working copy
of the plugin, joins it with two headless game clients and drives them through the
[Remote plugin](https://github.com/CometWorks/remote): player A is an administrator,
player B has no rights. The tests run the chat commands, move the players around and
check the results on the server. It runs on Linux only.

What it covers:

- Concealing by distance from players with `!conceal run` and the periodic scan, the
  reveal distance and interval, revealing as a player approaches, either player keeping a
  grid revealed (`test_conceal.py`)
- What stops while a grid is concealed and resumes once revealed: timers, programmable
  blocks, thrust; a concealed ship that keeps drifting; a block woken while its grid is
  concealed (`test_activity.py`)
- The three production modes: Off keeps active refineries revealed, Conceal pauses them,
  Approximate captures and settles paid resource-limited catch-up before reveal,
  caps elapsed absence, and saves claims with native world stocks (`test_production.py`)
- Excluded subtypes, pirate grids, the remote control keep-alive action, and grids joined
  by a rotor or a connector concealed and revealed as one (`test_exclusions.py`)
- The `!conceal` commands, their permissions and bad input (`test_commands.py`)
- A concealed grid on a client, one player revealing a grid for the other, dynamic
  concealment rules, a player logging back in to a cryo chamber (`test_clients.py`)
- Sensors and collisions with concealed grids, the projector of a concealed grid, a world
  saved while grids are concealed, and Grid Backups' backup before concealment
  (`test_systems.py`)
- Grids, config and concealment across a server restart, and a broken config file
  (`test_restart.py`)

Things the plugin doesn't do yet are strict xfails, so a fix shows up as an unexpected
pass.

The test world is the game's Empty World with the test players, a faction and a row of
test grids far apart, written fresh for every run. `tests/probe` is a test-only server
plugin the server loads next to Concealment: it reports which grids are concealed and the
state of grids and blocks on the server, and puts a new config into the running plugin,
so the tests don't need a restart for each config. The server also loads
[Grid Backups](https://github.com/CometWorks/grid-backups).

### Running

You need the Space Engineers client and Dedicated Server from Steam, Magnetar and Pulsar
installed, [uv](https://docs.astral.sh/uv/), and working copies of
[Remote](https://github.com/CometWorks/remote),
[DirectTransport](https://github.com/CometWorks/direct-transport) and Grid Backups. The
rig takes the ports, client ids and folders from a test slot of the `se` workspace
(`testing/parallel-test-work`), and waits for free memory before each process it starts.

```bash
uv run pytest
```

Where GitHub's API rate limits Magnetar, export `PULSAR_GITHUB_TOKEN` for it to download
its hub plugins with.

A full run takes about an hour, most of it the three server restarts. `tests/rig.py` lists
the environment variables that point the rig at other installations and working copies.
To keep the server and clients running between runs while you work on a test:

```bash
uv run python tests/session.py start
CC_ATTACH=1 CC_KEEP=1 uv run pytest tests/test_conceal.py
uv run python tests/session.py stop
```

Functionality is inspired by and reimplements the original Torch plugin
Concealment by TorchAPI: https://github.com/TorchAPI/Concealment
