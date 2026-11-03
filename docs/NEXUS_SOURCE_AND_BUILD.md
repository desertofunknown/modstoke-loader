# Nexus Source And Build Instructions

This document is for Nexus Mods review of Modstoke Loader for Sons Of
The Forest.

The loader is open source and is built from the Modstoke Loader repository plus
the OpenACAI forks of BepInEx, UnityDoorstop, MonoMod, and RedLoader
compatibility source listed below. The manager that installs this loader is also
open source and linked for review because it downloads, verifies, repairs, and
launches the loader package.

## Public Source Links

OpenACAI source repositories:

- Modstoke Loader:
  https://github.com/desertofunknown/openacai-loader/tree/openacai-net11-preview
- OpenACAI BepInEx fork:
  https://github.com/desertofunknown/openacai-bepinex/tree/openacai-net11-preview
- OpenACAI UnityDoorstop fork:
  https://github.com/desertofunknown/openacai-unitydoorstop/tree/openacai-hostfxr-net10
- OpenACAI MonoMod fork:
  https://github.com/desertofunknown/openacai-monomod/tree/openacai-net11-preview
- OpenACAI RedLoader compatibility source:
  https://github.com/desertofunknown/openacai-redloader/tree/openacai-loader-compat
- Modstoke Manager:
  https://github.com/desertofunknown/openacai-mod-manager/tree/openacai-tauri2-svelte5-shell

Upstream source provenance:

- BepInEx:
  https://github.com/BepInEx/BepInEx
- UnityDoorstop:
  https://github.com/NeighTools/UnityDoorstop
- MonoMod:
  https://github.com/MonoMod/MonoMod
- RedLoader by ToniMacaroni, rewrite branch:
  https://github.com/ToniMacaroni/RedLoader/tree/rewrite
- RedManager by ToniMacaroni:
  https://github.com/ToniMacaroni/RedManager

## Licenses And Credits

OpenACAI-authored loader changes are licensed as GPL-3.0-or-later where they
are separably licensable. Third-party components keep their original licenses.
See the repository `LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES`, and `LICENSES`
folders/files for component-specific terms.

## Build Environment

The current Windows build environment used by OpenACAI is:

- Windows 10 or Windows 11
- Git
- PowerShell 7 or Windows PowerShell
- Visual Studio 2022 Build Tools with the Desktop C++ workload and Windows SDK
- .NET SDK `11.0.100-rc.3.26522.148`
- .NET SDK `8.0.x` and `6.0.x` are useful for transitional upstream/forked
  projects that still contain older target frameworks
- Rust/Cargo `1.96.0`, needed for the Modstoke Native Host and the
  manager
- LLVM tools on `PATH`, specifically `lld-link.exe` for the Rust native host
  link step and `llvm-readobj.exe` for package-time native-host export
  validation
- Node.js `24.16.0` and npm `11.16.0`, only needed for the manager
- 7-Zip `26.01` or newer is recommended for maximum ZIP compression
- xmake, only for comparison builds that pass `-UseDoorstopForkNativeHost` and
  rebuild UnityDoorstop from source

The loader package requires a legal Sons Of The Forest installation because it
references generated IL2CPP interop assemblies and game assemblies produced from
that installation.

## Recommended Source Layout

The default package script expects this workspace layout:

```text
ModdingWorkspace/
  Modstoke.Loader/
  RedLoader/
  Forks/
    BepInEx/
    UnityDoorstop/
    MonoMod/
```

## Build Dependency Forks

If the OpenACAI MonoMod packages are not already present in
`Forks/MonoMod/artifacts/openacai-nuget/`, build them first:

```powershell
cd "C:\path\to\ModdingWorkspace\Forks\MonoMod"
dotnet pack -c Release -p:VersionSuffix=openacai-net11.5 -o artifacts\openacai-nuget src\MonoMod.Core\MonoMod.Core.csproj
dotnet pack -c Release -p:VersionSuffix=openacai-net11.5 -o artifacts\openacai-nuget src\MonoMod.RuntimeDetour\MonoMod.RuntimeDetour.csproj
dotnet pack -c Release -p:VersionSuffix=openacai-net11.5 -o artifacts\openacai-nuget src\MonoMod.Utils\MonoMod.Utils.csproj
```

Build the OpenACAI BepInEx fork:

```powershell
cd "C:\path\to\ModdingWorkspace\Forks\BepInEx"
dotnet restore
dotnet build .\BepInEx.sln -c Release
```

Build the RedLoader compatibility support assemblies:

```powershell
cd "C:\path\to\ModdingWorkspace\RedLoader"
dotnet build .\SonsSdk\SonsSdk.csproj -c Debug
dotnet build .\RedLoader\RedLoader.csproj -c Debug
```

Expected RedLoader support outputs:

```text
RedLoader/SonsSdk/bin/Debug/net6/SonsSdk.dll
RedLoader/SonsSdk/bin/Debug/net6/RedLoader.dll
```

If rebuilding UnityDoorstop manually:

```powershell
cd "C:\path\to\ModdingWorkspace\Forks\UnityDoorstop"
xmake config --arch=x64 --mode=release
xmake build
```

## Build The Loader Package

From the loader repository:

```powershell
cd "C:\path\to\ModdingWorkspace\Modstoke.Loader"
.\build.ps1 -GameRoot "C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest"
```

Expected outputs:

```text
dist/OpenACAILoader.zip
dist/OpenACAILoader.manifest.json
dist/OpenACAILoader.zip.sha256
```

The current branch builds the loader against .NET 11 preview and intentionally
rejects older runtime-major settings. The script builds the OpenACAI core,
builds the Modstoke Native Host, validates its pinned `winhttp.dll` export
surface, reuses existing BepInEx IL2CPP outputs when the required files are
already present, and packages:

- `src/Modstoke.NativeHost/Cargo.toml`
- `src/Modstoke.Bootstrap/Modstoke.Bootstrap.csproj`
- `src/Modstoke.Loader/Modstoke.Loader.csproj`
- OpenACAI BepInEx IL2CPP runtime files
- OpenACAI UnityDoorstop entry files only for comparison builds that pass
  `-UseDoorstopForkNativeHost`
- transitional RedLoader/SonsSdk compatibility assemblies
- a `Modstoke/Mods` native package root plus legacy compatibility roots
- manifests, notices, and third-party license material

Pass `-RebuildExternalRuntime` when you want the packaging script to rebuild
the transitional BepInEx IL2CPP and UnityDoorstop outputs during review. By
default the release package keeps Windows x64 native runtime assets and prunes
other platform-native BepInEx runtime folders; pass
`-IncludeAllNativeRuntimeAssets` only for an all-platform audit package.

## Game Interop Requirement

Sons Of The Forest is an IL2CPP Unity game. A complete local compile references
generated interop assemblies such as:

- `Il2Cppmscorlib`
- `Sons`
- `Sons.Gui`
- `Sons.Physics`
- `TheForest.Utils`
- `UnityEngine.*`
- `Unity.TextMeshPro`

These are generated from a local legal game installation and are not public
source code. Reviewers compiling the loader should use their own legal Sons Of
The Forest install and generate or provide equivalent interop assemblies from
that installation.

## Verify Built Artifacts

```powershell
Get-FileHash ".\dist\OpenACAILoader.zip" -Algorithm SHA256
Get-Content ".\dist\OpenACAILoader.manifest.json"
```

The Modstoke Manager verifies loader-owned files against the release
manifest before reporting the loader as installed.
