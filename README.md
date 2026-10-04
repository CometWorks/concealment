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

The plugin version lives in `Version.Build.props`.

`Directory.Build.props` auto-detects the Dedicated Server (`Dedicated64`) and the Magnetar
installation holding `PluginSdk.dll` (`Magnetar`). To override them, put your local paths into
`Directory.Build.props.user`, which is not committed. Running `setup.py` writes that file for you.

## Configuration

Magnetar stores configuration through the Plugin SDK config system.

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

Functionality is inspired by and reimplements the original Torch plugin
Concealment by TorchAPI: https://github.com/TorchAPI/Concealment
