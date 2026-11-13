# Loader Core

Start at [Plugin.cs](Plugin.cs) for the runtime entry point. Loader services live
outside `Sons/`; game-specific code lives inside it.

## Loader services

| Folder | Contents |
| --- | --- |
| [Hosting](Hosting) | Runtime state, startup, commands and mod actions. |
| [Discovery](Discovery) | Assembly resolution, mod inventory and file integrity. |
| [Configuration](Configuration) | Paths, preferences and server policy. |
| [Runtime](Runtime) | Unity lifecycle, coroutines, jobs, interop and native mod hosting. |
| [Compatibility](Compatibility) | BepInEx, RedLoader, SonsSdk and Axel adapters. |
| [UI](UI) | Loader input and the Mods menu. |
| [Diagnostics](Diagnostics) | Status snapshots, logging and timings. |

## Sons of the Forest

| Work area | Start here |
| --- | --- |
| Startup and scene changes | [Sons/Runtime](Sons/Runtime) |
| Item registration and inventory | [Sons/Inventory](Sons/Inventory) |
| Module stacks and persistence | [Sons/Inventory/Modules](Sons/Inventory/Modules) |
| Inventory transactions | [Sons/Inventory/Operations](Sons/Inventory/Operations) |
| Recipes and printers | [Sons/Crafting](Sons/Crafting) |
| Blueprints and placement | [Sons/Building](Sons/Building) |
| Transport, enrollment and Bolt | [Sons/Networking](Sons/Networking) |
| Host-side requests and world queries | [Sons/Server](Sons/Server) |
| Movement, equipment and survival | [Sons/Players](Sons/Players) |
| Combat authority and native hooks | [Sons/Combat](Sons/Combat) |
| Vehicle lifecycle and migration | [Sons/Vehicles](Sons/Vehicles) |
| Environment and world records | [Sons/World](Sons/World) |
| Companions and scheduling | [Sons/Companions](Sons/Companions) |
| Save lifecycle | [Sons/Saves](Sons/Saves) |
| Audio, settings and game UI | [Sons/Audio](Sons/Audio), [Sons/Settings](Sons/Settings), [Sons/UI](Sons/UI) |
| Textures decoded off the game thread | [Sons/Textures](Sons/Textures) |
| Game diagnostics | [Sons/Diagnostics](Sons/Diagnostics) |

`Sons/Players` contains player ownership and presentation code; `Sons/Server`
contains host-side event handling and queries. `Sons/Combat` separates client,
game hooks, equipment, local-player, protocol and presentation partials. These are
source boundaries, not a list of fully supported gameplay features. Check the
[SDK documentation](../../docs/sdk.md) for API contracts and limits.

## Finding a file

Filenames and owned types omit the historical brand prefixes. For example,
`Inventory` is in `Sons/Inventory/Inventory.cs`; its equipment partial
is `Inventory.Equipment.cs` in the same folder. Search by symbol when starting
from an API name.

Use `Type.Area.cs` for partial files and keep related parts together. Large
families such as combat may use subfolders for distinct responsibilities. Put the
brand in namespaces and assemblies, not every class or method. This API migration
requires rebuilding mods; serialized fields, network IDs and native exports remain
unchanged.

`Runtime/Mods` contains managed mod loading and lifecycle planning. The planning
services do not execute callbacks; `ModRuntime` owns execution. Inventory custody
code is under `Sons/Inventory/Operations`, including `EquipmentTransfer`. Game
detours and their retained bindings live under `Sons/Combat/GameHooks`. Keep
`Native` in names only where the unmanaged representation or lifetime matters.

The project lists every source file explicitly. Add new paths to
[the project file](Modstoke.Loader.csproj) and preserve the order of
existing entries: partial field initializers can depend on declaration order.

See [Contributing](../../CONTRIBUTING.md) for code conventions and
[Source and Build](../../docs/NEXUS_SOURCE_AND_BUILD.md) for build inputs.
