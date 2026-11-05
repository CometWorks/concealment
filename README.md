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

Battery profiles retain the native block's creative mode, including NPC batteries
in Survival worlds. Those batteries supply power without draining; Auto/Recharge
refill on their captured native 100-update phase. Other batteries, production and
fuel remain in the same paid resource budget. Older finite profiles remain readable.
An older unpaid NPC profile without the correct mode is refused with its original
claim/stocks retained; it is not silently reinterpreted or charged as Survival.

New battery captures retain the native 100-update phase. Near-full Auto batteries
settle with native float rounding, so alternating recharge/discharge also pays the
reactor's sampled fuel cost. Older continuous profiles retain their original math.
Solar sampling stays inside the engine's joined physics-read phase; callbacks on
the simulation thread hand computation to initialized native workers and join it.
The main thread can still wait for that work. Native requalification and the
exclusive sampling-time/frame budget remain required for these repairs.

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
Cluster explicitly invokes accounting contract 3, owns scheduling, authority clocks,
partition Finals and application, and loads this same plugin implementation.
A Cluster wake runs its models on a worker thread (`BeginApply`, `WakeReady`, `ApplyWake`):
a long-offline base can need minutes of model time. Only the clock read, validation,
admission and the builder commit run on the game thread.
No Cluster dependency or scheduler is required for standalone use.

Standalone profile schema 2 maps each saved 16 ms world-clock unit to one native resource
frame; resource seconds remain 1/60. Unpaid schema-1 profiles with either known
clock discriminator (`.016` or `1/60`) migrate on a detached copy, with an explicit
`ClockMigration` result. Frame addresses, stocks, native timer phases and sun
time stay intact. The immutable original remains the retry basis; already paid
receipts earn no second interval. Partly advanced schema-1 profiles and unknown
clocks are refused because their stock/phase history cannot be reconstructed.
Cluster schema 3 separately identifies local completed-native-update observations and
durable WA simulation intervals by epoch and generation. A fresh unpaid freeze translates
to WA coordinates without altering native stock or timer phases. Wake uses actual WA
native progress, excluding wall delay and downtime. Ordered, durable WA sun/work runs
preserve cached geometric generation and manual sun-step-at-end semantics. Old Cluster
schema-1/2 profiles do not identify native versus initialized extrapolated addresses and
are refused rather than silently reinterpreted; the immutable Final retains the claim.
Cluster propagates incompatible-state refusal before recovery can instantiate entities or
replace the immutable Final; standalone persists its group rejection.
Keep the corrected Cluster adapter and pinned core together. Contract-1 adapters
are incompatible; rollback requires the matching pre-upgrade world/Final/package,
never an old binary over schema-2 claims. No retroactive adjustment is made to
ordinary already-paid live stocks.

Failed standalone recovery retains each surviving unpaid row and its rejection.
Uncovered members of the loaded physical/logical closure retain opaque copies of
the surviving evidence. No missing root profile or stock baseline is invented.
These groups stay suspended and refuse admission, capture and reveal through
subsequent scans and world saves/reloads; unaffected groups proceed independently.
An update failure stops scheduling, profiling and commands. Already recovered complete
groups settle before stopping. Pending restoration still establishes group recovery
or quarantine if the first update fails, without starting observation or profiling.
Complete saved mechanical groups reconnect only their reciprocal saved native
base/top IDs on the owning game thread before topology validation. Rotor and piston
structural attachment runs with production/update guards retained; missing or changed
targets, foreign loaded membership and changed stocks still quarantine the claim.
A world-owned mechanical hold starts before structural calls and survives failed
recovery, stopped scheduling and plugin disposal. It snapshots every base's constraint
identity, including disabled constraints and absence. Native assignments are acquired
before publication; Havok ForceDisabled protects owned objects even when a callback
requests enabling. Complete original block/link/claim/closure, stock and pose checks
surround each structural call, including piston subparts. Invalid callbacks stop later
calls. Settlement checks the same ownership through native writes and inventory
notifications before publishing a paid receipt; failures roll stocks back while unpaid.
Each write checks an exact prepared intermediate stock basis, including untouched blocks.
Transaction expectations are sealed before native calls and do not share builders with
live stocks. Safe-zone comparison preserves settings, paid upkeep and activation while
the adapter separately checks the newly allocated bubble's identity and ownership.
The durable `StockHash` definition is unchanged, so existing unpaid native checkpoints
retain their original basis. Tank writes use the native server/sync lifecycle, including
source-capacity refresh. Rollback restores captured reservoir precision, resource caches,
litre bookkeeping and previous engine capacity; it cannot retract events already delivered.
Queue recipes remain part of profiling invalidation; authorized settlement completion
may remove recipes while immutable topology/settings and every stock stage stay checked.
Native admission permission ends with its OnAddedToWorld call and is revoked before
removal callbacks, including removal nested in admission. Removed retained objects
keep disabled managed intent without unsafe native setters or cleanup re-admission.
Unpaid SafeConstraint reads return only a currently safe retained object; native
builder serialization cannot dispose/recreate a removed constraint. Ownership
target checks read native connection state directly without invoking builders. Rollback
includes inventories even when unchanged at preparation time, plus native inventory
caches, queues, phases and reactor capacity derived from fuel. Native inventory
mutations through BeforeContentsChanged are refused while stock commit/rollback is
in progress, before an external callback can change those stocks.

Piston internal subpart constraints belong to the same owner. Native constructors
are acquired before field publication. Subparts rebuilt by LoadSubparts remain outside
the physics world until ordinary paid native updates admit them; no piston motion or
shape-update callback runs during recovery. Their recorded flags are restored as
managed intent while inactive and at native world admission after settlement. A new
unpaid owner takes precedence over any deferred restoration from an older paid interval.

New standalone records include private `Mechanical` state on the root row, with
required IntentVersion=1/Present/Enabled/ForceDisabled fields per base and piston subpart, plus
explicit subpart world-membership history. Missing subpart history in a new piston
record is refused. Enabled records the managed requested flag independently of effective native Enabled,
including when ForceDisabled masks it. Private standalone record schema 2 requires this
representation on both unpaid and paid history. An unpaid schema-2 root missing its
history is quarantined before reconstruction, with its raw evidence retained; child
rows need no duplicate map. Schema-1 history with ForceDisabled=true
is ambiguous and is quarantined without guessing; unforced history safely migrates.
Genuinely old claims without history retain observation-only compatibility. Originally disabled
constraints stay disabled across native saves/reloads. Mechanical record schema 2 is
independent of accounting contract 3 and its shared profile time domains.
Older claims have no historical ephemeral-constraint state. They
retain their supported native reconstruction path: record the actual native requested
state observed during reconstruction, including a native callback leaving it disabled.
Paid saves snapshot the current flags per grid and bind them to that checkpoint's
native saved targets. Normal native initialization restores them before later capture
or a world save, including paid restart. This flag normalization does not invoke an
unpaid motion callback or grant another compensation interval.
That migration cannot recover an unrecorded historical disabled flag, and makes no such
claim. Subsequent saves carry the observed state explicitly. Do not downgrade a world
with new mechanical records: older binaries ignore this ownership and can force enable
on reveal. Roll back the matching pre-upgrade world and plugin/package together.

Reconstructed constraints stay disabled until paid reveal, keeping native solver
jitter out of the held checkpoint. Pose validation accepts only the original pose
or its exact native cluster-local float round trip; the original profile/pose
evidence stays unchanged, with no added positional tolerance.
Unpaid/rejected/opaque claims and legacy receipts keep their
world-owned builder serializers and update guards through later ordinary saves.
Disposing the plugin while that world is loaded has the same retention rule.
The native world unload event releases the retained owner, hooks and callbacks;
unload that world before replacing its runtime. No unregistered ModStorage key
is used as a substitute for native builder serialization.

Persisted profiles must explicitly include both `Schema` and `ClockFrameSeconds`,
including inside standalone records. Missing discriminators are malformed input,
not evidence for current defaults or a supported legacy clock. Cluster refuses
that input before recovery instantiates entities; standalone retains it opaque
and suspended. New programmatic profiles retain their construction defaults.

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

Targeted accounting checks (build the server plugin first):

```sh
DEDICATED64=/path/to/DedicatedServer64 dotnet run --project tests/accounting-clock/ClockChecks.csproj -c Release
python3 tests/accounting-recovery/run.py /absolute/path/to/new/probe-output
python3 tests/accounting-mechanical/run.py /absolute/path/to/new/mechanical-output
python3 tests/accounting-physics/run.py /absolute/path/to/new/physics-output
python3 tests/accounting-prediction/run.py /absolute/path/to/new/prediction-output
python3 tests/accounting-cycles/run.py /absolute/path/to/new/cycle-output
```

The clock/JSON-boundary check consumes the built production DLL and native DS references. The recovery check extracts
the exact production recovery/save/admission methods and uses inert native
collaborators; it is not a native game proof. `tests/NativeProbe` separately
supports genuine incomplete-root rotor-group reloads and raw-clock live-stock
prefix references through the actual loaded host.
The prediction check uses the exact preparation methods and detached model with inert
session/history transport, covering current-period quality, resource predictions and
unavailable-history refusal. Predictions share the wake preparation, including its
Approximate fallback when the saved solar rotation period differs from the current one.
The cycle check compares exact model sources with cycle skipping disabled, including
staggered battery phases and fuel, tier, solar and zone boundaries. It is a synthetic
model check; the retained native Auto oracle remains in `tests/accounting-auto`.

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
