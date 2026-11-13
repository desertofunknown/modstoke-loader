# Sons Of The Forest Runtime Compatibility

Last updated: 2026-06-07

Developer: Alex Cooper

This document records the local Sons Of The Forest runtime facts that matter for
Modstoke Loader and the .NET 10 modernization track.

## Local Runtime Facts

The current local install is an IL2CPP Unity player.

| Area | Current evidence |
| --- | --- |
| Unity player | `UnityPlayer.dll` product version `2022.2.16f1 (d535843d11e1)` |
| Game executable | `SonsOfTheForest.exe` product version `2022.2.16f1 (d535843d11e1)` |
| IL2CPP assembly | `GameAssembly.dll`, 64-bit native IL2CPP payload |
| IL2CPP metadata | `SonsOfTheForest_Data\il2cpp_data\Metadata\global-metadata.dat`, metadata version `29` |
| Boot config | `gfx-enable-gfx-jobs=1`, `gfx-enable-native-gfx-jobs=1`, `gc-max-time-slice=3` |
| Doorstop | `.doorstop_version` is `4.5.0` |
| Loader runtime | The active build targets `.NET 11.0.0`; the LTS package line remains on `openacai-net10-port` |
| BepInEx | `BepInEx 6.0.0` compatibility runtime, OpenACAI fork source includes IL2CPP hook-state diagnostics, atomic interop generation, and Modstoke Loader version branding |
| Il2CppInterop | OpenACAI BepInEx fork source builds with `1.5.2-ci.1063` |
| HarmonyX | Preview baseline `2.16.1` |
| MonoMod.Core | RC2 port `1.3.5-openacai-net11.5`; exposes `Core110Runtime` with the RC2 JIT layout, unchanged in RC3 |
| MonoMod.RuntimeDetour | RC2 dependency `25.3.5-openacai-net11.5`; Harmony detours are enabled only after the OpenACAI safety probe passes |
| Cpp2IL | `2022.1.0-development.1452` in current package |
| Unity base libraries | `BepInEx\unity-libs\2022.2.16.zip` and extracted Unity base refs |

Useful package clues from generated scripting assembly metadata include HDRP,
Burst, Addressables, InputSystem, TextMeshPro, FMOD, Steamworks.NET, Bolt,
udpkit, NWH vehicle packages, Obi, Crest, Astar, and multiple Endnight/Sons
assemblies.

## What We Can Force

We can force the managed loader runtime because IL2CPP games need a separate
managed host for BepInEx plugins:

- Use Doorstop to inject `winhttp.dll`.
- Start CoreCLR through `hostfxr` using `Modstoke\Runtime\Modstoke.Loader.runtimeconfig.json`.
- Ship a side-by-side `.NET 10.0.8` runtime layout under `dotnet\`.
- Keep OpenACAI-owned assemblies in `Modstoke\Runtime` and
  `Modstoke\Runtime\Compatibility`; `BepInEx\plugins\OpenACAILoader` is only
  a bootstrap compatibility mirror while the native IL2CPP bootstrap is being
  rewritten.
- Ship the small RedLoader/SonsSdk compatibility dependency set beside those
  assemblies. `Tomlet` backs legacy config files, `SemanticVersioning` backs
  RedLoader metadata/version APIs, and `Alt.Json`/`GLTF` support SDK asset
  helpers that OpenACAI expects to own or supersede later.
- Control assembly resolution order for loader, BepInEx, Il2CppInterop,
  RedLoader/SonsSdk compatibility assemblies, and .NET runtime assemblies.
- Choose and gate MonoMod/Harmony behavior through BepInEx config and environment
  flags.
- Choose and probe native detour providers such as Dobby or Funchook in the
  OpenACAI BepInEx fork.
- Pre-generate or repair `BepInEx\interop` wrappers when game binaries or Unity
  base libraries change.

This is the path that lets Modstoke Loader become more than a normal loader:
we build an OpenACAI-owned runtime with stable APIs, event surface, generated
interop wrappers, config UI, asset helpers, source-aware mod inventory,
diagnostics, and safe hook/patch hosting on top of the real game runtime.

## What We Should Not Force

We should not try to patch Sons Of The Forest to use a higher Unity player
version. `UnityPlayer.dll`, `GameAssembly.dll`, `baselib.dll`, native Unity
modules, serialized assets, generated IL2CPP metadata, Burst output, and native
plugins are built as one ABI-sensitive set by the game developer.

Swapping in a newer Unity player would likely break:

- IL2CPP metadata and method layout assumptions.
- Serialized asset and shader pipeline compatibility.
- Native plugin ABI expectations.
- Burst/job-system generated code.
- Addressables and resource bundle loading.
- Steam/networking/physics/plugin integration.

It would also create redistribution and license risk if the loader shipped game
or Unity engine binaries. The safer rule is: do not redistribute or replace
Sons/Unity binaries. Detect the installed game version, then adapt the loader to
that runtime.

## .NET 10 Stability Position

The recorded `.NET 10.0.8` hostfxr path reached BepInEx startup in the local
install. Historical `BepInEx\LogOutput.log` evidence, with original names:

- `Running under Unity 2022.2.16f1`
- `Runtime version: 10.0.8`
- `Runtime information: .NET 10.0.8`
- OpenACAI Endnight Loader `0.3.2-net10-alpha`

The loader intentionally runs with reduced RedLoader behavior on .NET 10:

- RedLoader native Unity support is disabled.
- RedLoader mod hosting is inventory-only unless explicitly enabled for
  diagnostics. On .NET 10/11, OpenACAI-managed legacy hosting also requires
  `OPENACAI_REDLOADER_MOD_ALLOWLIST` to name specific mod ids/names/assembly
  names, or `*` for an intentional load-all diagnostic run.
- Entrypointless RedLoader ports are disabled by default.
- Sons menu Harmony patches are disabled by default.

That means the bootstrap is improving, but full RedLoader parity is still gated
behind detour/Harmony/Interop hardening.

## .NET 11 Preview Position

`openacai-net11-preview` now targets RC3 and retains the existing
`0.4.0-net11-preview` package identity. Earlier Preview 4 and RC2 evidence does
not establish RC3 compatibility. Keep `.NET 10` as the LTS baseline until the
RC3 branch has repeated launch, menu, world, and admin/anti-cheat integration
acceptance. See [the current build pins](NET11_PREVIEW_EXPERIMENT.md#current-build-pins).

The 2026-06-05 moment-of-truth retest launched the live preview staging through
Steam, reached the loader console and title path, initialized RedLoader
compatibility, and attached the title `MODS` button. The previous RedLoader
`MonoMod.Utils.PlatformHelper` startup crash did not recur after rebuilding the
RedLoader compatibility DLL against the modernized dependency shape. Stale
compatibility DLLs can still reference that removed MonoMod 22 helper, so the
OpenACAI bridge now maps RedLoader's legacy `LoaderEnvironment` path state
directly instead of calling RedLoader's `SetExecutablePath` during startup.
The next runtime retest found the package was too lean for RedLoader's legacy
config bootstrap: `Tomlet.dll` was not copied with `RedLoader.dll` and
`SonsSdk.dll`. The package script now stages the curated managed compatibility
dependency set into both `Modstoke\Runtime\Compatibility` and the bootstrap
mirror, and legacy core preference initialization is warning-only so OpenACAI
startup can continue if the old config layer fails.
OpenACAI now also probes those dependency files at startup and publishes
`openacai.loader.compatibilityDependencies` into loader state. This gives the
manager and SDK consumers a direct health/readiness signal for RedLoader/SonsSdk
config/version/asset helper dependencies instead of discovering missing files
only through `TypeInitializationException`. Three-part versions such as
`5.4.0` are normalized against four-part baselines such as `5.4.0.0`, and
older assemblies already loaded by the compatibility runtime are reported as
binding conflicts instead of making the packaged OpenACAI dependency set look
missing or stale.

OpenACAI RedLoader host discovery now inspects both `Mods` and `Libs`.
Assemblies from `Libs` default to RedLoader `Library` candidates, appear in
host state and mod inventory, and remain non-hostable. This keeps SDK/helper
dependencies visible without accidentally treating them as mods. Inventory-only
mode also records per-candidate skipped load reports so the manager can show
that libraries were skipped as libraries while hostable mods were skipped by the
global inventory-only guard. The in-game menu separates these helper assemblies
into a `Libraries` tab so normal mod views stay focused on playable/loadable
mods. The native menu tab row is count-anchored so all built-in tabs remain
visible after adding the library split.

The prior .NET 11 Harmony blocker is resolved in the current preview staging:
OpenACAI now ships a MonoMod baseline that exposes `Core110Runtime`, and live
loader state reports `safeForHarmonyPatches=True` after probing HarmonyX,
MonoMod.Core, MonoMod.RuntimeDetour, MonoMod.Utils, and the IL2CPP native hook
state. Harmony-driven features should still go through the OpenACAI patch-host
gate rather than running as unconditional startup side effects.

## Highest-Value Hardening Work

1. Harden `UnityDoorstop\src\bootstrap.c`.
   `4e690fd` now validates `hostfxr_path`, `runtime_config_path`, derived
   dotnet root, and target assembly paths before attempting hostfxr startup.
   `f1c4004` now validates the PE machine type for configured `hostfxr.dll` and
   `coreclr.dll` against the current game process architecture before loading
   them. Remaining work here is deeper failure classification.
   Keep `docs\DOORSTOP_RUNTIME_NOTES.md` updated when the Doorstop base or
   hostfxr patch set changes.

2. Make native detour setup exception-safe.
   `2142da4` now validates native pointers/delegates, checks Dobby status
   returns, checks Funchook handles, cleans up failed detour setup, and prevents
   cleanup failures from masking the original hook failure.

3. Probe and select detour providers explicitly.
   `2142da4` now probes Dobby and Funchook native libraries/exports before
   selecting a provider. Default mode prefers Dobby, falls back to Funchook, and
   fails clearly when neither provider is available.
   The OpenACAI package script now stages the BepInEx Dobby win-x64 native
   backend as both `BepInEx\core\dobby.dll` and
   `BepInEx\core\runtimes\win-x64\native\dobby.dll` so the early
   `il2cpp_runtime_invoke` patch has a provider even when using the direct
   `dotnet build` output instead of BepInEx's full Cake distribution task.

4. Make `IL2CPPChainloader` runtime invoke hook observable.
   `2142da4` now uses `NativeLibrary.TryGetExport` for `il2cpp_runtime_invoke`,
   catches native detour setup failures, and guards unhook cleanup failures.
   `c7a7a85` now writes `openacai-il2cpp-hook-state.json` with GameAssembly
   load status, `il2cpp_runtime_invoke` export status, Dobby/Funchook provider
   probes, selected provider, detour patch status, chainloader execution state,
   and cleanup/failure detail. The OpenACAI bridge reads this into loader state
   as the `il2CppNativeHooks` block and publishes the `il2cpp.nativeHooks`
   compatibility shim.

5. Make interop generation atomic.
   `7884f33` now generates into an OpenACAI staging directory, validates that
   Il2CppInterop emitted loadable managed DLLs, writes `assembly-hash.txt` in
   staging, then swaps staging into `BepInEx\interop`. If Cpp2IL,
   Il2CppInterop, or the swap fails, the last known-good interop directory is
   preserved.

6. Keep startup branding version-correct.
   `5e9076f` now reads `OpenACAI\Runtime\loader.manifest.json`
   for the OpenACAI Endnight Loader version used in the console title and first startup
   banner, with a copy mirrored to the transitional BepInEx bootstrap path.

7. Make resolver ordering deterministic.
   The OpenACAI bridge now builds a fixed resolver order for bridge, BepInEx,
   Unity managed, RedLoader library, and RedLoader mod-package paths. It sorts
   discovered package directories, deduplicates paths case-insensitively, and
   keeps disabled/private OpenACAI admin paths out of the public resolver list.
   The resolver is now package-aware: it keeps RedLoader package roots and
   DLL-bearing dependency folders, but skips dev/resource/data folders that do
   not contain assemblies. It writes structured `resolver` state plus the legacy
   `resolverSearchDirectories` string list into `openacai-loader-state.json`.

8. Modernize compatible NuGet packages in a staged branch.
   Verified on 2026-06-05 with `dotnet list package --outdated
   --include-prerelease`:
   - HarmonyX: `2.10.2` -> `2.16.1`
   - MonoMod.RuntimeDetour: `22.7.31.1` -> `25.3.4`
   - MonoMod.Utils: `22.7.31.1` -> `25.0.12`
   - Il2CppInterop packages: `1.5.1-ci.829` -> `1.5.2-ci.1063` completed in OpenACAI BepInEx fork commit `b4c6622`
   - Cpp2IL: `2022.1.0-development.1452` -> `2022.1.0-pre-release.21`

   Do not update all of these in one commit. Update one family at a time and
   validate the hostfxr package after each dependency family changes.

## Modstoke Modding Platform Direction

Long term, the loader should make modding feel closer to source access without
pretending we have the game source:

- Generate stable typed wrappers around Sons/Endnight classes from IL2CPP
  metadata.
- Publish a documented Modstoke SDK/event bus for game lifecycle, world state,
  players, inventory, construction, AI, weather, vehicles, and UI.
- Treat BepInEx and RedLoader as compatibility shims for existing mods, not as
  the permanent identity of the loader.
- Provide a safe patch host that validates Harmony/native-detour compatibility
  before applying risky hooks.
- Expose a mod config UI that mirrors the game's native options screen.
- Support asset bundles and addressable-style content registration without
  replacing game assets.
- Provide diagnostics for mod authors: method maps, type lookup, dependency
  status, hook safety, and conflict detection.
- Keep RedLoader and BepInEx compatibility, but route new OpenACAI mods through
  the Modstoke SDK first.

## Early Startup Diagnostics

The current BepInEx IL2CPP bridge writes
`BepInEx\plugins\OpenACAILoader\openacai-il2cpp-hook-state.json` before the
OpenACAI runtime starts; future OpenACAI bootstrap work should move this to
`Modstoke\Runtime\openacai-il2cpp-hook-state.json`. If the game crashes before
the loader console appears, check this file after Doorstop/hostfxr logs. A
successful run should show `runtimeInvokePatched=true`, a selected provider
such as Dobby or Funchook, and eventually `chainloaderExecuted=true` or
`status=loaded`.

## Manifest Drift Policy

The live install was refreshed to `0.4.0-net11-preview` on 2026-06-05 after the
clean `ad77b53` package build. Earlier `0.3.0`/`0.3.2` live manifest drift is no
longer the current local state.

The manager repair path should still treat installed loader metadata as a
managed file: hash-check it, repair it from the package manifest, and warn when
runtime state and installed manifest disagree.

