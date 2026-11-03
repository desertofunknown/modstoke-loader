# Project Structure

Use this guide to find code and understand the module boundaries. For API
contracts and current limits, see the [SDK reference](sdk.md)
and [runtime compatibility notes](SOTF_RUNTIME_COMPATIBILITY.md).

## Projects

Projects live under [src](../src). Startup is split from the runtime and game services.

| Project | Responsibility |
| --- | --- |
| [NativeHost](../src/Modstoke.NativeHost) | Rust `winhttp.dll` proxy and native CLR startup boundary. |
| [Bootstrap](../src/Modstoke.Bootstrap) | Managed entry point and startup orchestration. |
| [Preloader](../src/Modstoke.Preloader) | Interop generation, assembly patching, startup diagnostics and compatibility adapters. |
| [Core](../src/Modstoke.Loader/README.md) | Loader lifecycle, mod services, UI and Sons-specific SDK code. |
| `Modstoke.Compatibility.BepInEx.*` | Retained BepInEx Core, Preloader Core, Unity Common and Unity IL2CPP assembly surfaces. |
| [Compatibility.RedLoader](../src/Modstoke.Compatibility.RedLoader), [Compatibility.SonsSdk](../src/Modstoke.Compatibility.SonsSdk) | Legacy RedLoader and SonsSdk assembly surfaces. |
| [Modstoke.Harmony](../src/Modstoke.Harmony/README.md) | Harmony-compatible managed API and native planning implementation. |

The separate mod projects have their own documentation:
[Client](../src/Modstoke.Client/README.md),
[Community](../src/Modstoke.Community/README.md),
[Terraforming](../src/Modstoke.Terraforming/README.md) and
[Virginia](../src/Modstoke.Virginia/README.md).
Their presence in the source tree does not mean they or their assets ship in the loader.

## Core Folders

All paths below are relative to [Core](../src/Modstoke.Loader).
Its [README](../src/Modstoke.Loader/README.md) gives the detailed navigation map.

| Folder | Responsibility |
| --- | --- |
| `Hosting/` | Runtime startup, state, commands and mod actions. |
| `Discovery/` | Assembly resolution, mod inventory and file integrity. |
| `Configuration/` | Paths, preferences and server policy. |
| `Diagnostics/` | Loader status, logging and timings. |
| `Runtime/` | Shared runtime code in `Coroutines`, `Interop`, `Jobs`, `NativeMods` and `Unity`. |
| `Compatibility/` | BepInEx, RedLoader, SonsSdk and other adapters within Core, distinct from the compatibility assembly projects. |
| `UI/` | Loader input and `Menus/`. |
| `Sons/` | Game-specific services, separate from general loader infrastructure. |

`Sons/` is divided into `Runtime`, `Audio`, `Building`, `Crafting`,
`Inventory`, `Networking`, `Players`, `Combat`, `Server`, `Vehicles`,
`World`, `Saves`, `Settings`, `UI`, `Companions` and `Diagnostics`.
Player code lives under `Players/`; host-side requests and queries live under
`Server/`. Larger areas use subfolders to separate responsibilities.

## Where To Start

| Change | Entry point |
| --- | --- |
| Native or managed startup | [Native host](../src/Modstoke.NativeHost/src/lib.rs), [bootstrap](../src/Modstoke.Bootstrap/Entrypoint.cs), then [Preloader](../src/Modstoke.Preloader). |
| Loader lifecycle | [Plugin.cs](../src/Modstoke.Loader/Plugin.cs) and [Hosting](../src/Modstoke.Loader/Hosting). |
| Package discovery or configuration | [Discovery](../src/Modstoke.Loader/Discovery) and [Configuration](../src/Modstoke.Loader/Configuration). |
| In-game loader menu | [UI/Menus](../src/Modstoke.Loader/UI/Menus); game-specific UI is in [Sons/UI](../src/Modstoke.Loader/Sons/UI). |
| Game APIs or mod development | The matching [Sons folder](../src/Modstoke.Loader/Sons), [development workflow](SDK_DEVELOPMENT_WORKFLOW.md) and [game routines guide](SDK_GAME_ROUTINES.md). |
| Packaging or dependency inputs | [Source and Build](NEXUS_SOURCE_AND_BUILD.md), [dependencies](../dependencies/README.md) and [.NET branch notes](NET11_PREVIEW_EXPERIMENT.md). |

## Names And Partial Files

Projects, assemblies and namespaces use the `Modstoke` name. Types inside those
namespaces use domain names without repeating the brand: `Modstoke.Sons.Inventory`,
`Modstoke.Loader.LoaderRuntime`, and `Modstoke.Jobs` for shared jobs.
The new API requires mods built against it; older compiled mods must be rebuilt.
Legacy BepInEx, RedLoader and SonsSdk compatibility assemblies keep their identities.

For example, `Inventory` is in
[Inventory.cs](../src/Modstoke.Loader/Sons/Inventory/Inventory.cs).
Its [Inventory.Equipment.cs](../src/Modstoke.Loader/Sons/Inventory/Inventory.Equipment.cs)
partial keeps the `Type.Area.cs` suffix convention. Search by symbol when following
an API name. Save keys, plugin IDs, network fields and native exports are separate
contracts; do not rename them to match source files.

Core lists sources explicitly in its [project file](../src/Modstoke.Loader/Modstoke.Loader.csproj).
Keep existing compile order when moving partials: field initialization can depend
on it. Follow [Contributing](../CONTRIBUTING.md) for changes.

## Upstreams And Release Boundaries

[Upstream lineage](../UPSTREAMS.md), [NOTICE](../NOTICE),
[third-party notices](../THIRD_PARTY_NOTICES.md) and [LICENSES](../LICENSES)
retain the upstream attribution and license terms.
[Source and Build](NEXUS_SOURCE_AND_BUILD.md) and [patches](../patches/README.md)
document dependency sources and the patch trail; do not remove their notices
when moving files.

[Modstoke Manager](https://github.com/desertofunknown/openacai-mod-manager) is a
separate repository and release. Modstoke Admin and private companion modules
are outside this public loader repository. Private moderation tools, policy data,
credentials, local notes, logs and game caches do not belong in public packages.
Only redistributable assets belong in releases; a source folder is not a release
manifest. See [prebuilt](../prebuilt/README.md) for release artifact pointers.
