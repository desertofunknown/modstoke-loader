# Modstoke Loader .NET 11 RC3

Developer: Alex Cooper

This branch is an experiment, not the public stable loader line. The current
stable modernization branch remains `openacai-net10-port` because .NET 10 is an
LTS release supported until November 2028. The experimental branch now targets
.NET 11 RC3, released on October 25, 2026. A runtime release candidate does not
establish loader or mod compatibility.

## Why Test It

Microsoft's .NET 11 preview notes call out runtime-native async, lower async
overhead, JIT improvements, updated hardware-intrinsic cost modeling, and other
runtime/library improvements. Those are worth exploring for a loader that does
interop generation, reflection, diagnostics, mod inventory scanning, config IO,
and runtime hooks.

The OpenACAI rule for now:

- Keep `.NET 10` as the main public/runtime branch until net11 proves itself in
  repeated Sons Of The Forest launches.
- Use `openacai-net11-preview` for compile, package, smoke-test, and controlled
  in-game experiments.
- Do not overwrite a live game install with the net11 package while Sons Of The
  Forest is running.
- Treat any net11 package as experimental; sign public test packages through
  Azure Trusted Signing before refreshing `prebuilt/`.

## Current Build Pins

- Loader branch: `openacai-net11-preview`
- Loader manifest version: `0.4.0-net11-preview`
- Loader target framework: `net11.0`
- Loader SDK pin: `11.0.100-rc.3.26522.148`
- Loader package runtime: `.NET 11.0.0-rc.3.26522.148`
- BepInEx fork branch: `openacai-net11-preview`
- BepInEx fork commit: `76bb58ecda542467ee01b45a1abf66d8cf18e2ba`
  (`Modernize Harmony and MonoMod for net11`)
- BepInEx Core, Preloader.Core, and Unity.Common now target
  `netstandard2.1;net11.0` on this Sons IL2CPP path. The old `net35` and
  `netstandard2.0` targets are no longer part of the active IL2CPP preview
  output, though the Mono compatibility projects still carry legacy targets
  while they are only reference/compatibility material.
- BepInEx Unity IL2CPP targets `net11.0`.
- RC2 patching dependencies: HarmonyX `2.16.1`,
  MonoMod.Core `1.3.5-openacai-net11.5`, MonoMod.RuntimeDetour
  `25.3.5-openacai-net11.5`, and MonoMod.Utils
  `25.0.13-openacai-net11.5`. Preview 4 runtime evidence below applies to the
  earlier `.3` packages, not automatically to this port.
- MonoMod 25 officially gates its runtime support at `.NET 10`, so the
  OpenACAI MonoMod fork carries an explicit `.NET 11` `Core110Runtime` instead
  of bypassing the guard with a .NET 10 runtime layout.
- The OpenACAI runtime bridge now requires MonoMod.Core to expose
  `Core110Runtime` on `.NET 11`; if it sees the old unsafe `Core100Runtime`
  path, it treats that as failed rather than trying to continue.
- UnityDoorstop remains on the existing hostfxr branch because its native
  bootstrap is runtime-major agnostic after `hostfxr_path` and
  `runtime_config_path` are configured.

## RC3

RC3 is a servicing build of RC2. Against the RC2 tag of the
[.NET VMR](https://github.com/dotnet/dotnet/tree/v11.0.100-rc.3.26522.148)
(RC3 commit `5254d8bcf599`), its only runtime changes are a Windows
`SocketAsyncEventArgs` address-reuse fix and Brotli 1.1.1. CoreCLR, the JIT,
CoreLib and the host are unchanged, and `jiteeversionguid.h`, `corinfo.h`,
`corjit.h` and `icorjitinfoimpl_generated.h` are byte-identical to RC2. The `.5`
MonoMod packages and their `Core110Runtime` layout therefore apply without a
rebuild. Use the VMR as the RC3 source: the `dotnet/runtime` tag
`v11.0.0-rc.3.26522.148` points at the RC2 commit.

RC2 startup evidence does not carry over to the RC3 package. The packaged
runtime, hostfxr and runtime configuration move together; install them as one set.

## RC2 Runtime Interface

The MonoMod port uses the exact
[RC2 runtime source](https://github.com/dotnet/runtime/tree/v11.0.0-rc.2.26504.105),
commit `474ed963699fc29809015392738a15231aaae2de`. Its JIT GUID is
`0d18a7df-af1f-4481-a72f-aa6cf8aa0a65`; the extracted `allocMem` slot is `0xA7`
and the interface has `0xB8` slots. RC2 also replaces the fixed allocation
arguments with a chunk array. The port reads the hot/cold writable code blocks
from those chunks and retains the JIT version check. RC2's method-compilation
helper also uses the static `IRuntimeMethodInfo.GetValue` method in place of
the former interface property. Both the emitted-call and reflection paths
support that change while preserving the older accessor.

The old native hook backend still assumes the fixed allocation structure and
is disabled for .NET 11. Windows x64 already uses the managed hook wrapper.
This port does not establish macOS ARM64 support, whose native backend also
handles platform-specific JIT write protection.

Package IDs, assembly names, loader paths, mod configuration, and update URLs
are unchanged. The new `.5` dependency versions distinguish RC2 from the `.3`
Preview 4 build; rollback requires restoring the matching loader dependencies
and runtime together.

## Earlier Harmony And MonoMod Port Notes

The dependency upgrade replaced several removed MonoMod helper APIs instead of
trying to keep old packages around:

- Old `MonoMod.Utils.PlatformHelper` calls are bridged through a small
  OpenACAI compatibility type that maps to MonoMod 25 `PlatformDetection` and
  `DynDll.DllExtension`.
- Old `DynDllImport`/`ResolveDynDllImports` usage was replaced with explicit
  `DynDll.TryOpenLibrary`, `DynDll.GetExport`, and
  `Marshal.GetDelegateForFunctionPointer<T>()`.
- Old `AsDelegate<T>()` calls were replaced with
  `Marshal.GetDelegateForFunctionPointer<T>()`.
- Old `ReflectionHelper.IsCore`/`IsMono` checks were replaced with local target
  framework checks or MonoMod 25 `PlatformDetection.Runtime`.
- RuntimeDetour 25's `IDetour` shape now exposes `DetourConfig Config`; the
  IL2CPP native detour wrapper implements the new surface while preserving the
  BepInEx native-detour helper methods the IL2CPP path expects.
- The OpenACAI bridge now probes `0Harmony.dll`,
  `MonoMod.RuntimeDetour.dll`, and `MonoMod.Utils.dll` at startup and only
  enables Harmony-driven Sons menu patches when those dependencies meet the
  tested baseline and the IL2CPP hook state is healthy. `OPENACAI_ENABLE_SONS_MENU_HARMONY`
  can force diagnostics on, and `OPENACAI_DISABLE_SONS_MENU_HARMONY` disables
  that patch path.
- A .NET 11 Core100Runtime experiment reached MonoMod's JIT GUID check; changing
  only the expected GUID to the observed .NET 11 preview JIT GUID caused a
  native `coreclr.dll` access violation during MonoMod runtime initialization.
  The reason was the vtable layout, not only the GUID: .NET 11 preview uses JIT
  GUID `33ed917f-a197-4abd-83af-db9fa8186898`,
  `ICorJitInfo.allocMem` slot `0xA4`, and total vtable count `0xB4`; the .NET
  10 runtime layout used slot `0xA0` and count `0xB0`.
- The initial OpenACAI MonoMod fork included `Core110Runtime` and `CoreCLR.V110`
  generated from dotnet/runtime `v11.0.0-preview.4.26230.115` commit
  `7e66cd737ed083ae9fcb8ede136c6c76f8a4ba1e`.
- RuntimeDetour 25 removed the old public `Detour` class, but
  `Il2CppInterop.HarmonySupport` 1.5.2 still constructs it. The OpenACAI
  RuntimeDetour `.3` package restores `MonoMod.RuntimeDetour.Detour` as a
  compatibility facade over the modern `Hook` API so existing HarmonySupport
  binaries can load while the larger OpenACAI rewrite proceeds.

Dry package build on 2026-06-05 succeeded with this dependency set. The only
expected warnings were the .NET 11 preview SDK notice, MonoMod's explicit net11
runtime guard warning, and existing Doorstop C proxy warnings.

## Build

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet-11-rc3"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
.\build.ps1 -Configuration Release -RebuildExternalRuntime -NoUpdatePrebuilt -DotnetRoot $env:DOTNET_ROOT
```

This example assumes the verified Windows x64 RC3 SDK archive is extracted to
`.tools/dotnet-11-rc3`. Build the `.5` MonoMod packages first using
[the dependency instructions](NEXUS_SOURCE_AND_BUILD.md#build-dependency-forks).
The environment changes apply only to the current PowerShell session. An
existing RC3 SDK installation can be supplied instead.

Packaging selects the exact runtime and hostfxr version in
`loader.manifest.json`. It will not substitute another installed
preview, release candidate, or final runtime for a version-specific JIT port.

Use `-NoUpdatePrebuilt` for local-only test packages. Omit it when the tracked
GitHub prebuilt package should be refreshed. The script defaults to `.NET 11` on
this branch and refuses non-11 package builds so preview artifacts are not
confused with the LTS net10 line.

Latest signed package checkpoint:

- Loader commit: `51f42a5608481eacb95a0ac97841461ec8d5fb25`
- BepInEx commit: `76bb58ecda542467ee01b45a1abf66d8cf18e2ba`
- RedLoader/SonsSdk commit: `fdcf7c0e4f304e27ffe86260401d72bf4039a361`
- Package SHA256: `31166D73AC8E9C492912A5F0744C2E255F94F548E7F9DA15B9F03786AC9CD0CB`
- Package size: `50,516,474 bytes` (`48.18 MiB`)
- Compressor: `7-Zip Deflate`
- `BepInEx.Core.dll` SHA256: `D2F3AC8EF8C868CA2FED5A5D9813A222B661EB26E5E6FB5A006C2E2614C1333D`
- `BepInEx.Unity.IL2CPP.dll` SHA256: `058BF427E563E54094E6D8FAD1DDBA2727C1C849080DC4E0A4425CCDC2FF15BE`
- Transitional loader DLL `Modstoke.Loader.dll` SHA256: `82EDFA3E83C1FBE888A62EE1443A1A8DD6CD23E0A9C82056AB5056DAEA7D9D20`
- `RedLoader.dll` SHA256: `9CDAD057A70B77DDF006E6F4027B15A4DFEBB19B6600F89BB03820643BD04F07`
- `SonsSdk.dll` SHA256: `FEC9C4F5250380F87224EE68DA78D27A8578513A88169E43AE3B83FE2FF26F84`
- `winhttp.dll` SHA256: `D9F9C697F38BE95E8AAE887C21788CF5966029264F27E4EED86A29857B0293F9`
- Signing certificate: OpenACAI Inc, thumbprint `8402188618D8D3CC02A6F885EE2AB8B7A5E5481A`

The BepInEx fork now compiles the interpolated string handler attribute shims
only for legacy targets, which removes the .NET 11 CS0436 type-conflict
warnings from the preview build.

## 2026-06-05 In-Game Validation

This is historical validation evidence from before the Modstoke rename. Product
names in captured UI metadata and logs below are preserved as observed.

Computer Use launched the live `0.4.0-net11-preview` package from
`SonsOfTheForest.exe` after the tracked prebuilt was regenerated and staged to
the game root.

Observed good state:

- Title screen reached successfully with `MODS` present in the native bottom
  menu.
- Mods menu opened as the full-screen native-style page with the real rough
  border, dark tint, chromatic selected tab text, and `OpenACAI Endnight Loader
  v0.4.0-net11-preview` metadata.
- Author/source display stayed mod-specific instead of rewriting every mod to
  OpenACAI.
- `Extensions` listed `Mod Config` and `Protected Runtime` providers.
- `Mod Config` provider detail opened and Back returned to `Extensions`.
- A real RedLoader config editor opened for `AllowBuildInCaves` with friendly
  rows and actual boolean values instead of blank placeholder bars.
- The tooltip strip no longer overlaps the tabs or footer actions.
- The mods list and config settings pane both respond to mouse-wheel scrolling.
- Friendly config mode renders boolean values as `ENABLED`/`DISABLED` controls
  and text/numeric values as native-style inputs.
- Raw text mode keeps the config text intact when the editor receives focus; a
  space/backspace smoke test no longer clears the whole buffer.

Fresh `BepInEx\LogOutput.log` reported `.NET 11.0.0-preview.4.26230.115`,
OpenACAI Endnight Loader `0.4.0-net11-preview`, title `MODS` attach to
`BottomCanvas/BottomMenuPanel`, and no public loader errors. The private
`OpenACAI Admin Tool` still logs a `.NET 10` warning string; leave that for the
private repo.

An accidental overlapping launch produced `BepInEx\LogOutput.1.log` and exposed
the legacy RedLoader `RLog` file-lock/static-initializer path. Commit `ad77b53`
keeps the bridge alive by warning and continuing when RedLoader legacy config or
log bridge initialization fails.

Moment-of-truth retest on 2026-06-05 staged fresh local
`Modstoke.Loader.dll` and rebuilt `RedLoader.dll` into the live game
folder, then launched through Steam. The game reached the title path under
`.NET 11.0.0-preview.4.26230.115`, RedLoader compatibility initialized,
`Hosted 0 RedLoader mods` was reported, and the title `MODS` button attached to
`BottomMenuPanel`. The previous RedLoader `PlatformHelper` startup crash did
not recur.

RuntimeDetour/Core110 retest on 2026-06-05 rebuilt the local package with
`MonoMod.Core 1.3.5-openacai-net11.3`,
`MonoMod.RuntimeDetour 25.3.5-openacai-net11.3`, and
`MonoMod.Utils 25.0.13-openacai-net11.3`, staged it into the live game install,
and launched through Steam. The title screen reached, the Mods screen opened,
the `AllowBuildInCaves` friendly config page opened, and the game closed
cleanly. `openacai-loader-state.json` reported `harmony.patches` ready,
`safeForHarmonyPatches: true`, `sonsMenuHarmonyEnabled: true`, and
`Core110Runtime` ready. Fresh `BepInEx\LogOutput.log` showed no actual
`AccessViolation`, `TypeLoadException`, `HarmonyException`, missing
`MonoMod.RuntimeDetour.Detour`, unhandled exception, or fatal crash entry.

User in-world validation later on 2026-06-05 joined a server, opened the pause
menu, verified the pause-menu `MODS` button worked, and verified the Mods menu
opened from pause.

Latest unsigned local release-asset package:

- Package SHA256: `90E7623EE81EC211563322092ABD6ED0268D047C5014D9F1D472C017586FB0C2`
- Package size: `50,412,553 bytes`
- Compressor: `7-Zip Deflate Ultra (mx=9,mfb=258,mpass=15)`
- Manifest package URL: `https://github.com/desertofunknown/openacai-loader/releases/latest/download/OpenACAILoader.zip`
- `MonoMod.RuntimeDetour.dll` SHA256: `8D7E494B1644EC33603D167BF845D4FA91FC48EA2FE826F9F6D2923122AD533F`
- `MonoMod.Core.dll` SHA256: `0F78F54E1E4504B1217FA22FF6DFB3465AC4C1448AC06498E510D4B621BAEFEA`
- `Modstoke.Loader.dll` SHA256: `6D640405297BF6417C8EF98A773198FFE38EE5ADBF67D3901F14A60AF2592773`
- This artifact is not signed; sign before publishing. Upload the ZIP,
  manifest, and checksum from `dist\` as GitHub Release assets instead of
  committing the ZIP to Git.

Known UI follow-up: the edit-mode/save/revert row currently scrolls with the
config content. The next polish pass should make that row fixed while only the
settings list scrolls.

## Sources Consulted

- https://learn.microsoft.com/en-us/dotnet/core/releases-and-support
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview
- https://dotnet.microsoft.com/en-us/download/dotnet/11.0
- https://learn.microsoft.com/en-us/aspnet/core/migration/100-to-110
- https://www.softacom.com/wiki/net-6-is-out-of-support-how-to-safely-migrate-to-net-8/
- https://www.gapvelocity.ai/blog/dotnet8-to-dotnet10-migration-guide
- https://www.nuget.org/packages/HarmonyX
- https://www.nuget.org/packages/MonoMod.RuntimeDetour
- https://www.nuget.org/packages/MonoMod.Utils
- https://docs.github.com/articles/what-is-the-size-limit-for-a-repository

