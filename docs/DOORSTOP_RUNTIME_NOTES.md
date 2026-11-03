# UnityDoorstop Runtime Notes

Last updated: 2026-06-07

Developer: Alex Cooper

These notes capture the Doorstop, Mono, and Harmony findings that matter for
the OpenACAI .NET 10 loader track and the isolated .NET 11 preview rewrite
branch.

## Sources Reviewed

- UnityDoorstop upstream: https://github.com/NeighTools/UnityDoorstop/tree/master
- UnityDoorstop breaking changes: https://github.com/NeighTools/UnityDoorstop/blob/master/CHANGES.md
- Mono source tree: https://gitlab.winehq.org/mono/mono
- Harmony upstream: https://github.com/pardeike/Harmony

## What Doorstop Does

UnityDoorstop is the native bootstrapper that gets managed loader code running
inside a Unity game before the normal game-side managed startup path. Doorstop 4
is a full rewrite of UnityDoorstop 3 and supports both Unity Mono and IL2CPP
Unity players.

Doorstop 4 always expects the managed entry point to be:

```csharp
namespace Doorstop;

class Entrypoint
{
    public static void Start()
    {
    }
}
```

The older automatic `Main(string[] args)` style entry point is not the Doorstop
4 contract.

## Unity Mono Versus IL2CPP

For a Unity Mono game, Doorstop runs the target assembly in the game's bundled
Mono/CLR runtime. That means the assembly is in the same managed runtime as the
game code and can access Unity APIs directly, though the game may still be very
early in startup when the entry point runs.

For an IL2CPP game, Doorstop cannot execute normal managed assemblies inside
IL2CPP itself. Doorstop 4 starts a separate CoreCLR runtime and invokes the
managed entry assembly from there. Any access back into the game has to go
through IL2CPP/native interop such as the `il2cpp_` API, generated wrappers,
Il2CppInterop, and carefully gated detours.

Sons Of The Forest is an IL2CPP Unity player, so OpenACAI's relevant Doorstop
path is the CoreCLR/hostfxr path, not the Unity Mono path.

## Current Upstream Base

The local OpenACAI Doorstop fork is based on current upstream master, not on an
old Doorstop 3 branch.

Verified on 2026-06-05:

- Upstream `master`: `3ac0156aebc33305865fa3dd2559752ed1dcdf00`
- Upstream latest tag: `v4.5.0` at `33dab9a6733862eb81869ff08431d9478b28784b`
- Local OpenACAI branch: `openacai-hostfxr-net10`
- Local branch commits:
  - `d1eb5c0 Add hostfxr CoreCLR bootstrap path`
  - `e85d34b Improve hostfxr bootstrap for OpenACAI loader`
  - `4e690fd Harden hostfxr bootstrap diagnostics`
  - `f1c4004 Validate native runtime architecture before hostfxr startup`

The upstream release panel now reports `v4.5.0` as latest, while upstream
`master` has moved one commit beyond that tag. For future updates, compare all
three before rebasing: release assets, latest tag, and upstream master.

## Doorstop 4 Configuration Changes

Doorstop 4 moved old 3.x options into clearer sections:

- General loader options live under `[General]`.
- Unity Mono runtime options live under `[UnityMono]`.
- IL2CPP/CoreCLR runtime options live under `[Il2Cpp]`.

Doorstop 4's stock IL2CPP options are:

- `coreclr_path`: path to `coreclr.dll`.
- `corlib_dir`: path to managed CoreCLR libraries.

OpenACAI's Doorstop fork adds modern hostfxr options:

- `hostfxr_path`: path to `dotnet\host\fxr\<version>\hostfxr.dll`.
- `runtime_config_path`: path to the runtimeconfig used to initialize the
  managed loader runtime.

For OpenACAI packages, these should point at the bundled side-by-side .NET 10
runtime and `Modstoke\Runtime\Modstoke.Loader.runtimeconfig.json`.

## OpenACAI Hostfxr Delta

The OpenACAI patch set teaches Doorstop to initialize CoreCLR through
`hostfxr_initialize_for_runtime_config`, then obtain
`hdt_load_assembly_and_get_function_pointer` through `hostfxr_get_runtime_delegate`.

The fork derives `dotnet_root` from `hostfxr_path`, builds the Doorstop 4 type
name as `Doorstop.Entrypoint, <target assembly name>`, and requests a
`System.Action, System.Private.CoreLib` delegate for `Start()`.

If both `hostfxr_path` and `runtime_config_path` are configured but hostfxr
startup fails, the fork intentionally does not fall through to Doorstop's legacy
`coreclr_initialize` path. That keeps a misconfigured OpenACAI package from
silently booting through the wrong runtime path. Any future fallback should be
explicit, diagnostic-only, and clearly reported in loader state.

The current patch set also validates the target assembly, `hostfxr_path`,
`runtime_config_path`, derived `dotnet_root`, and target assembly directory
before loading hostfxr. It wires `hostfxr_set_error_writer` when that export is
available, and unloads the hostfxr module on failures that happen before the
CoreCLR runtime is initialized. It also reads the Windows PE headers for
configured `hostfxr.dll` and `coreclr.dll` and verifies that their machine type
matches the current game process architecture before calling `LoadLibrary`.

## Package Layout OpenACAI Needs

The loader package should include and verify:

- `winhttp.dll`
- `.doorstop_version`
- `doorstop_config.ini`
- `dotnet\host\fxr\<shipped .NET 11 version>\hostfxr.dll`
- `dotnet\shared\Microsoft.NETCore.App\<shipped .NET 11 version>\`
- `Modstoke\Runtime\Modstoke.Bootstrap.dll` as the
  Doorstop target assembly
- `BepInEx\core\BepInEx.Unity.IL2CPP.dll` as the transitional IL2CPP
  compatibility API/bridge
- `Modstoke\Runtime\Modstoke.Loader.runtimeconfig.json`
- `Modstoke\Runtime\Modstoke.Loader.dll`
- `BepInEx\plugins\OpenACAILoader\Modstoke.Loader.dll` as the
  temporary bootstrap mirror

The exact .NET patch version can change, but the package manifest and manager
repair flow must hash and validate whichever runtime version is shipped.

## Hardening Checklist

- Validate `hostfxr_path` exists before loading it. Implemented in
  `4e690fd`.
- Validate `runtime_config_path` exists before loading hostfxr. Implemented in
  `4e690fd`.
- Verify hostfxr and the game process architecture match.
  Implemented in `f1c4004`.
- Derive `dotnet_root` safely and log the derived value. Implemented in
  `4e690fd`.
- Log hostfxr HRESULTs and, where possible, wire the hostfxr error writer.
  Implemented in `4e690fd`.
- Keep `APP_PATHS` and `DOORSTOP_DLL_SEARCH_DIRS` deterministic.
- Record the Modstoke Native Host metadata in package manifests.
- Keep Doorstop upstream SHA and license/source-trail metadata only for
  comparison packages built with `-UseDoorstopForkNativeHost`.
- Do not replace Sons Of The Forest or Unity engine native binaries to chase a
  newer Unity runtime. Adapt the loader to the shipped game runtime instead.

## Mono Notes

The WineHQ Mono source is useful background for Mono/Wine and Unity Mono cases,
but it is not the primary path for OpenACAI's Sons Of The Forest loader. Sons is
IL2CPP, and our .NET 10/.NET 11 work is centered on CoreCLR hosting through
hostfxr.

Mono should stay on the research map for cross-platform behavior, legacy Unity
Mono games, and Wine-specific testing, but it should not drive the Sons IL2CPP
bootstrap design.

## Harmony Notes

The pardeike Harmony project is the upstream patching library for modifying
.NET and Mono methods at runtime. Harmony is relevant because existing BepInEx
and game-mod ecosystems depend heavily on Harmony-style prefixes, postfixes,
transpilers, and non-file-mutating runtime patches.

OpenACAI uses HarmonyX/MonoMod as patch-engine libraries while owning the patch
policy, baseline probe, lifecycle timing, and failure reporting. For the .NET
10/11 IL2CPP track, treat Harmony-family patching as a gated capability:

- Keep existing BepInEx and RedLoader mods compatible when the runtime is safe.
- Probe and report Harmony/native-detour capability before applying risky hooks.
- On .NET 11, require the OpenACAI MonoMod baseline with `Core110Runtime`
  before marking Harmony patches safe.
- Prefer Modstoke SDK services for new mods instead of forcing every feature
  through raw Harmony patches.
- Do not mix incompatible Harmony 1.x and Harmony 2.x families.

## Practical Direction

OpenACAI now owns the native injection layer for the default package path:

- `src\Modstoke.NativeHost` builds the Rust `winhttp.dll`.
  It preserves the WinHTTP proxy export list, hooks `GetProcAddress`, captures
  `il2cpp_init`, validates the configured hostfxr/CoreCLR paths, sets the
  Doorstop-compatible environment variables, and invokes
  `Doorstop.Entrypoint.Start()` through hostfxr. The proxy stubs and load table
  are generated by `build.rs` from `native_host_exports.def`, so the Rust host
  and DEF export contract cannot drift silently.
- The managed Doorstop target is now
  `Modstoke\Runtime\Modstoke.Bootstrap.dll`, which records
  `openacai-bootstrap-state.json`, `openacai-chainloader-state.json`, and
  `openacai-il2cpp-preloader-state.json`. The default `openacai-native` mode
  owns preloader orchestration and runs explicit OpenACAI IL2CPP preloader
  phases. Its chainloader handoff now passes through the
  `IL2CPPChainloaderAdapter`, which records
  `openacai-il2cpp-chainloader-state.json`. The adapter proves that
  GameAssembly, `il2cpp_runtime_invoke`, the native detour factory,
  `BasePlugin`, `ManualLogSource`, and the packaged
  `RuntimeEntryPoint.Load` entrypoint are visible. It then arms an
  OpenACAI-owned direct trigger for `Internal_ActiveSceneChanged`, preloads
  IL2CPP interop, creates the core log source, and invokes
  `RuntimeEntryPoint.Load` directly. A fresh 2026-06-07 launch validated
  that the direct core runtime executed without creating the BepInEx
  `IL2CPPChainloader` instance. The older bridge delegation path remains
  available as a fallback mode while remaining preloader and compatibility
  internals are ported.
- BepInEx and RedLoader behavior become compatibility shims.
- Modstoke Loader exposes stable services, diagnostics, file integrity, mod
  inventory, SDK events, and safe patch hosting.
- The manager repairs the Rust native host, runtime layout, and loader-owned
  assemblies by hash so a broken host path can be fixed before launch.

Keep the Rust host small: Windows proxy/export handling, configuration parsing,
hostfxr loading, architecture validation, environment setup, and bootstrap
invocation only. Use a C ABI surface, avoid panics across FFI, keep
dependencies focused, and use `-UseDoorstopForkNativeHost` only for diagnostic
comparison builds. Small, well-maintained crates such as `windows-sys`,
`serde`, and `serde_json` are acceptable when they reduce native ABI,
serialization, or state-maintenance risk; do not add dependencies that own
loader policy, mod hosting, or chainloader behavior for OpenACAI.

