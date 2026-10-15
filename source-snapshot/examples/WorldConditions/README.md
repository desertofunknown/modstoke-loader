# World Conditions Example

A small Modstoke-native mod with a settings page and a read-only menu action.
It references only the public loader Core assembly, without BepInEx, RedLoader,
SonsSdk or game-interop references.

## Build

From the loader repository, after building the current loader:

```powershell
dotnet build examples/WorldConditions/WorldConditions.csproj --configuration Release
```

To use an installed Core instead of the repository build:

```powershell
dotnet build examples/WorldConditions/WorldConditions.csproj --configuration Release "-p:OpenACAICorePath=C:\Games\Sons Of The Forest\OpenACAI\Runtime\OpenACAI.Endnight.Loader.Core.dll"
```

This example targets the loader's current `net11.0` framework and needs a matching
.NET SDK. The Core reference is not copied into the output. Building does not
install the example or modify the loader.

## Package

The native host expects this layout beneath the game directory:

```text
OpenACAI/Mods/WorldConditions/
  openacai.mod.json
  OpenACAI.Examples.WorldConditions.dll
```

Both files are emitted to `bin/Release/net11.0/`. Do not bundle a second Core or
the loader's runtime dependencies. Package changes require a game restart, and
execution requires the OpenACAI-owned Unity driver.

The manifest names the static entrypoint and shutdown callback explicitly.
No update, background polling or GUI callback is registered.

## In Game

The Extensions tab contains **World Conditions** and **World Conditions Settings**.
Refresh reads local clock and weather snapshots; it never changes time or weather.
The optional clock-ticks setting changes the display and lasts for the current
process only. It does not write a config file.

Displayed values are the last observations, not a live feed. Each source has its
own observation timestamp. A failed read replaces that source's old values with
its failure status, while an independently successful read is retained. An
unavailable world does not become a zero clock or clear weather.

Game time and effective time remain separate when a native clock override is
active. Cloud phase, precipitation and snow flags describe native state, not
rendered particles or agreement with remote clients. This mod can run on a host
or client without requiring other players to install it. A dedicated server has
no player-facing menu; the example does not add a remote administration endpoint.

## Adapt It

- `Mod.cs` registers the providers and removes them during shutdown or a failed
  startup. Give a derived mod its own manifest, menu and settings IDs; provider
  IDs are shared names, not ownership tokens.
- `WorldConditionsProvider.cs` implements menu content, one safe command and a
  typed Boolean setting. The setting setter validates keys and values rather
  than relying on UI controls.
- Native reads happen only through the SDK on the initialized game thread.
  Menu content uses managed snapshots, so rendering does not access game objects.
- The refresh action is intentionally unprivileged. Menu authorization and
  confirmation flags are not a substitute for server-side permission checks
  when adding world-changing or remote commands.

The example is compiled against the current public API. Loading, layout,
interaction and scene-transition behavior have not yet been verified in game.
