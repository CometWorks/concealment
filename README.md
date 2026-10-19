# Concealment

Server-only Space Engineers plugin for Magnetar.

## Prerequisites

- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Projects

- `ServerPlugin` - Magnetar plugin entry point and server runtime code.
- `Shared` - common plugin helpers and interfaces.

This repository builds only the Magnetar server plugin.

## Build

Install the Space Engineers Dedicated Server, Magnetar and the .NET SDK required by the project, then build:

```sh
dotnet build Concealment.sln -c Debug
```

The plugin output is:

```text
ServerPlugin/bin/Debug/net10.0/Concealment.dll
```

The plugin version lives in `Version.Build.props`. Magnetar compiles the plugin from source
without it, so change the assembly attributes at the top of `ServerPlugin/Plugin.cs` with it.

`Directory.Build.props` auto-detects the Dedicated Server (`Dedicated64`) and the Magnetar
installation holding `PluginSdk.dll` (`Magnetar`). To override them, put your local paths into
`Directory.Build.props.user`, which is not committed. Running `setup.py` writes that file for you.

## Configuration

Magnetar stores configuration through the Plugin SDK config system.

## Grid Backups

If the server also runs [Grid Backups](https://github.com/CometWorks/grid-backups), Concealment
asks it for a backup of each grid group just before concealing it. Grid Backups decides whether
to write one: its `BackupBeforeConcealment` option and its minimum time between two backups of
a grid (`MinMinutesSinceLastSave`) apply.

## Development

Load the working copy through a Magnetar development folder: start Magnetar with `-sources` and
add the repository with the Sources button. Magnetar then compiles the plugin from source.

Builds deploy nothing by default. To copy the build into Magnetar's `Local` plugin folder, set
`MagnetarData` to the Magnetar config folder (the one holding `Local`, `Sources` and `Profiles`)
in `Directory.Build.props.user`, or pass it to a single build:

```sh
dotnet build Concealment.sln -p:MagnetarData=$HOME/.config/Magnetar/Magnetar
```

`Concealment.xml` is the MagnetarHub metadata file for server-side publication.

## Tests

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
  Approximate banks the concealed time, catches up faster, caps the bank and saves the
  banks with the world (`test_production.py`)
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
