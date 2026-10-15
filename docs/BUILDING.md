# Building Modstoke Loader

**Publication status (15 October 2026):** The new public Loader repository was started with a README only. The source files and commands below exist in a separate, prepared local `OpenACAILoaderSuite` checkout; they do not work from a clone without that source.

These instructions describe that checkout's `openacai-net11-preview` source tree. They produce a **local test package**, not a verified public release. The package script depends on files from a prepared Sons of the Forest installation and sibling source checkouts; it is not yet a standalone clone-and-build project.

## What you need

- Windows x64, a legal Sons of the Forest installation, and PowerShell.
- The .NET SDK pinned by `global.json`: `11.0.100-rc.2.26504.105`. Packaging also requires the exact runtime and hostfxr version in `openacai-loader.manifest.json`; the script will not substitute another preview or final runtime.
- Rust/Cargo with the `x86_64-pc-windows-msvc` target, Visual Studio C++ Build Tools and Windows SDK, and LLVM's `lld-link.exe` and `llvm-readobj.exe` on `PATH`.
- The OpenACAI BepInEx fork, RedLoader/SonsSdk compatibility outputs, and the current OpenACAI MonoMod package outputs used by the BepInEx fork. The local source checkout's `docs/NEXUS_SOURCE_AND_BUILD.md` records the fork build order and package pins for this branch; it is not published in the README-only repository.
- A prepared game install with `BepInEx/core`, `BepInEx/config/BepInEx.cfg`, `BepInEx/patchers`, and `BepInEx/unity-libs`. Building Loader Core also requires the game's **generated** `BepInEx/interop` wrapper set. Those game-derived binaries do not belong in this repository.

7-Zip is optional. When it is unavailable, the script falls back to PowerShell `Compress-Archive`. xmake and the UnityDoorstop fork are needed only for comparison packages built with `-UseDoorstopForkNativeHost`.

## Arrange the source

The package script expects these sibling directories by default:

```text
ModdingWorkspace/
  OpenACAILoaderSuite/
  RedLoader/
  Forks/
    BepInEx/
    MonoMod/
    UnityDoorstop/       # comparison builds only
```

`RedLoader/SonsSdk/bin/Debug/net6/SonsSdk.dll` and `RedLoader.dll` must exist before packaging. The BepInEx fork must have IL2CPP build outputs. You can pass `-BepInExForkRoot` to the package script if that fork is elsewhere; the script currently resolves RedLoader from the sibling `RedLoader` directory.

Build the dependency forks according to `docs/NEXUS_SOURCE_AND_BUILD.md` in the local source checkout. Its recorded sequence is to package the OpenACAI MonoMod fork, build the OpenACAI BepInEx fork, then build the RedLoader `SonsSdk` and `RedLoader` projects. Older .NET SDKs may be needed by those transitional fork projects. A fresh game install may need a prepared Loader/BepInEx startup to generate its `BepInEx/interop` wrappers before Loader Core can compile.

## Build the package

From `OpenACAILoaderSuite`:

```powershell
dotnet --version
cargo --version
Get-Command lld-link.exe, llvm-readobj.exe
.\build-openacai-loader.ps1 -GameRoot "C:\Games\Sons Of The Forest"
```

Replace the example game path with your installation folder, which must contain `SonsOfTheForest.exe`. In the documented workspace layout, the script can resolve the game root automatically; the explicit argument makes the intended installation clear.

The script builds the Rust native host and the managed preloader, bootstrap, and Core; reuses existing BepInEx IL2CPP outputs unless `-RebuildExternalRuntime` is supplied; validates the native host's `winhttp.dll` exports; and writes:

```text
dist/OpenACAILoader.zip
dist/OpenACAILoader.manifest.json
dist/OpenACAILoader.zip.sha256
```

The ZIP is built for Windows x64 by default. The script does **not** update `prebuilt/` unless `-UpdatePrebuilt` is explicitly supplied. `-NoUpdatePrebuilt` can be used to make that choice explicit for a test run. Signing is a separate, opt-in `-SignArtifacts` flow and is not part of the command above.

## Install a local test ZIP

Use a separate test installation or back up the game and Loader files you intend to replace. The package includes a `BepInEx/config/BepInEx.cfg` copied from the build installation, so extraction can replace an existing configuration. The ZIP contents go **directly** beside `SonsOfTheForest.exe`:

```powershell
$gameRoot = 'C:\Games\Sons Of The Forest'
Expand-Archive -LiteralPath .\dist\OpenACAILoader.zip -DestinationPath $gameRoot -Force
```

Launch the game normally after extraction. The Loader writes its state under `OpenACAI/Runtime/`; the staged `TROUBLESHOOTING.md` guide explains how to follow startup from the native host onward. This local install is a test of the built package, not proof of general release readiness.

## Build Loader Core or the example

For a focused Core build after the references above are present:

```powershell
dotnet build src\OpenACAI.Endnight.Loader.Core\OpenACAI.Endnight.Loader.Core.csproj --configuration Release --framework net11.0
```

The Core project in the local source checkout checks for generated IL2CPP wrappers, including `Il2Cppmscorlib.dll` and `UnityEngine.UnityWebRequestAudioModule.dll`. Set its `GameRoot`, `GameInteropRoot`, `BepInExCoreRoot`, or `RedLoaderSourceRoot` MSBuild properties if your development layout differs. Supplying Unity base libraries in place of the generated wrappers does not satisfy the project.

The local source checkout's native mod example can then be compiled with:

```powershell
dotnet build examples\WorldConditions\WorldConditions.csproj --configuration Release
```

The example project references the built Core DLL by default. Its local `examples/WorldConditions/README.md` shows how to point `OpenACAICorePath` at an installed matching Core DLL and how to lay out the example under `OpenACAI/Mods/WorldConditions/`. Compilation alone does not install or verify the mod in game.

## Release boundary

The branch's .NET 11 RC2 runtime and mod behavior need in-game validation before treating a build as a release.

Do not add the generated ZIP, logs, game binaries, interop wrappers, signing material, or local development notes to source control. Keep `LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES.md`, and `LICENSES/` with any redistributed package.

