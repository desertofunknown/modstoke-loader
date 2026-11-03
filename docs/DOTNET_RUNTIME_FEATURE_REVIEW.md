# Modstoke Loader .NET Runtime Feature Review

Last updated: 2026-06-05

Developer: Alex Cooper

This note records the .NET 6 -> .NET 10 and .NET 10 -> .NET 11 changes that
matter to Modstoke Loader, Modstoke Manager, and Modstoke SDK. It is not a
complete .NET changelog; it is the project-relevant subset.

## Current Position

- `.NET 10` remains the public/LTS baseline because it is supported until
  November 14, 2028.
- `.NET 11` remains an experimental branch until repeated in-game validation
  proves it is stable enough for public packages.
- The user's first `.NET 11` in-game test reported noticeably faster loader
  startup, more responsive in-game UI, and instant F7 anti-cheat/admin panel
  open/close behavior. Treat this as promising but not yet proof of causality.
- The latest BepInEx preview cleanup removed build-time warning noise and should
  not by itself explain runtime UI responsiveness.
- The public OpenACAI bridge currently has almost no `async`/`await` usage, so
  .NET 11 runtime-native async most likely helps framework/library paths,
  private companion/admin code, process/IO paths, or startup/JIT behavior rather
  than the current immediate-mode menu renderer directly.

## Support Facts

- `.NET 6` is out of support. Microsoft lists its end of support as
  November 12, 2024.
- `.NET 10` is LTS, active, and supported until November 14, 2028.
- `.NET 11` is currently preview. Microsoft expects final release in
  November 2026.

Project rule: do not make `.NET 11` the default public package until it has
multiple clean test cycles: startup, title menu, mods menu, in-world, pause
menu, admin panel, mod config editing, RedLoader inventory, BepInEx plugin
loading, and private anti-cheat compatibility.

## .NET 6 To .NET 10: Relevant Changes

The move away from `.NET 6` matters first because `.NET 6` is unsupported, then
because the runtime and libraries gained several features that fit this project.

### Runtime and JIT

- `.NET 6` already introduced Crossgen2, better FileStream behavior, source
  generators, reflection nullability APIs, C# 10, and improved logging APIs.
- `.NET 10` adds stronger JIT optimization, including improved code generation,
  profile-guided inlining behavior, devirtualization, loop/code layout
  improvements, and stack allocation/escape-analysis improvements.
- `.NET 10` stack-allocation improvements matter for loader code that creates
  many short-lived arrays or small helper objects during inventory scans,
  manifest parsing, resolver path construction, UI filtering, and diagnostics.
- `.NET 10` runtime improvements do not require us to replace Unity's own
  `UnityPlayer.dll` or `GameAssembly.dll`. They affect the separate CoreCLR
  runtime that Doorstop starts for the loader.

### Libraries and APIs

- `System.Text.Json` gains stricter options, including duplicate-property
  rejection and a strict preset. This is useful for `loader.manifest.json`,
  mod manifests, file-hash manifests, and future server policy documents.
- ZIP APIs are faster and gained async APIs. This is more important for the mod
  manager and package repair flow than for the in-game loader.
- `WebSocketStream` turns WebSockets into a stream-like transport. This could
  simplify future admin/companion IPC if we use WebSockets instead of raw files
  or a custom pipe protocol.
- Windows process group support and process/diagnostic APIs are useful for the
  manager/launcher when starting the game, collecting logs, and shutting down
  helper processes cleanly.

### Breaking Changes To Watch

- `BufferedStream.WriteByte` no longer implicitly flushes in `.NET 10`. Any
  loader or admin logging path that depends on byte writes must explicitly
  flush or dispose.
- `System.Linq.AsyncEnumerable` is included in core libraries in `.NET 10`.
  Avoid ambiguous references if a future dependency also brings an async LINQ
  package.
- Trace propagation and `ActivitySource` behavior changed. This matters only if
  we add telemetry or distributed tracing to the admin/companion stack.
- `System.Text.Json` property-conflict behavior is stricter. This is good for
  security, but malformed legacy manifests might fail earlier.

## .NET 10 To .NET 11: Relevant Changes

`.NET 11` is attractive because it appears to improve the exact kind of runtime
work our loader does, but it is still preview and has a higher compatibility bar.

### Runtime Async

- `.NET 11` introduces runtime-native async. Instead of compiler-generated
  async state machines doing all the work, the runtime can manage suspension and
  resumption.
- Microsoft documents cleaner live stack traces, better debugger behavior, and
  lower overhead. The .NET runtime libraries themselves are built with
  `runtime-async=on`.
- To use runtime-native async in OpenACAI code, add
  `<Features>runtime-async=on</Features>` to the relevant project file. Do not
  enable this globally until we add focused tests.

OpenACAI relevance:

- Highest value for the private admin panel, future companion client, manager
  networking, log streaming, Vortex/Nexus/store API calls, and mod download/
  extraction flows.
- Lower direct value for the current in-game menu renderer, because the public
  bridge currently uses synchronous file IO and immediate-mode rendering.
- Useful for diagnostics because cleaner live stacks can make async failures in
  the admin/companion pipeline less painful to debug.

### JIT, ReadyToRun, and Hardware Baseline

- `.NET 11` raises the minimum x86/x64 JIT/AOT baseline to `x86-64-v2` and
  raises Windows/Linux ReadyToRun targets to `x86-64-v3`.
- That can help modern game PCs, but it means the manager should eventually
  detect unsupported CPUs and offer the `.NET 10` LTS package as fallback.
- JIT improvements include better bounds-check elimination, redundant checked
  context removal, switch-expression folding, faster `uint` to float/double
  casts, and ReadyToRun devirtualization for non-shared generic virtual calls.

OpenACAI relevance:

- Mod inventory scans, config parsing, manifest parsing, resolver sorting,
  Harmony/interop metadata walks, and UI filtering all contain loops and generic
  collection work that can benefit from better JIT decisions.
- ReadyToRun should be tested for startup improvements, but we should not enable
  it blindly until crash logs and package size are acceptable.

### Low-Level IO and Compression

- `.NET 11` adds low-level IO improvements such as `SafeFileHandle.Type`,
  anonymous pipe creation, and `RandomAccess` support on non-seekable handles.
- On Windows, process redirected stdout/stderr uses overlapped IO, reducing
  thread-pool blocking in process-heavy applications.
- ZIP/TAR reading now does more validation, including ZIP CRC32 validation and
  TAR checksum verification. This can help catch corrupted mod packages earlier.
- Zstandard support in `System.IO.Compression` is potentially useful for future
  package formats if the manager and repo need smaller/faster mod packages.

OpenACAI relevance:

- Useful for manager-side install/repair/download/extract workflows.
- Useful for game-launcher diagnostics that redirect process output.
- Useful for future IPC if we move from text files to pipes.
- Validation changes can break malformed archives that used to pass, but that is
  preferable for a loader/manager that verifies hashes and package integrity.

## net35, netstandard2.0, And netstandard2.1 Decision

`net35`, `netstandard2.0`, and `netstandard2.1` are doing different jobs. None
of them should be treated as the future OpenACAI runtime implementation target,
but `netstandard2.1` is important for a Sons-specific SDK surface.

### net35

- `net35` is a legacy .NET Framework target. BepInEx keeps it for old Unity Mono
  games and upstream compatibility.
- `.NET Framework 4.8.1` is the latest supported .NET Framework line on
  supported Windows versions, but it is still Windows-only .NET Framework, not
  the modern `.NET` runtime that OpenACAI starts through hostfxr.
- Unity's ".NET 4.x Equivalent" scripting profile is not the same thing as
  making the Modstoke Loader target `net481`, and Sons Of The Forest is not a
  Unity Mono game in our loader path.
- Sons Of The Forest is IL2CPP, and OpenACAI's package uses the `.NET 10/11`
  BepInEx IL2CPP output, not the `net35` output.
- Keeping `net35` inside the transitional BepInEx fork preserves upstream patch
  shape and legacy compatibility. It also forces some shared BepInEx code to
  keep compiling against very old APIs.
- Replacing `net35` with `net481` would not make the Sons loader more modern;
  it would mostly trade one legacy .NET Framework target for another legacy
  .NET Framework target.
- OpenACAI-owned runtime modules should not target `net35` or `net481`.

Decision: keep `net35` only in the transitional BepInEx fork for now. Do not add
new OpenACAI code to `net35` or `net481`. When the OpenACAI-owned runtime
replaces more of the BepInEx-shaped behavior, split the old compatibility target
away or drop it from the Sons-only fork. Do not upgrade `net35` to `net481`
unless a separate Windows-only legacy compatibility package explicitly needs it.

### netstandard2.0 And netstandard2.1

- `netstandard2.0` is an API contract, not a runtime. It is supported by
  .NET Core 2.0+, .NET 5+, .NET Framework 4.6.1+, and Mono 5.4+.
- Microsoft's compatibility table maps `netstandard2.0` to Unity `2018.1`.
- `netstandard2.1` is also an API contract, and the final .NET Standard version.
  The same Microsoft table maps it to Unity `2021.2`.
- Sons Of The Forest ships on Unity `2022.2.16f1`, so `netstandard2.1` is the
  better Unity-era match for the Modstoke SDK abstractions.
- It remains useful for compatibility libraries and plugin-facing abstractions
  because it lets older mods and helper assemblies compile against a broad API
  surface.
- `netstandard2.0` still matters for broad compatibility and older helper
  libraries, but it is an older Unity baseline than Sons needs.
- `netstandard2.1` gives the public SDK more APIs while still staying inside the
  Unity-supported .NET Standard world for modern Unity.
- A `netstandard2.0` assembly loaded by the `.NET 11` runtime can still benefit
  from runtime/JIT improvements, but it cannot use new `.NET 10/11` APIs unless
  it multi-targets.

Decision: keep `netstandard2.0` only where it buys compatibility with older
mods, older helper libraries, or upstream BepInEx patch shape. Prefer
`netstandard2.1` for Sons-specific Modstoke SDK/abstraction packages. Prefer
`net10.0`/`net11.0` for OpenACAI-owned runtime implementation code. If we create
a public `OpenACAI.Abstractions` package, target `netstandard2.1` plus
`net10.0`/`net11.0`; add `netstandard2.0` only if we deliberately need an older
compatibility layer.

## Harmony/MonoMod Modernization Findings

The current dependency pass confirms there are practical replacements for the
old APIs that blocked newer Harmony/MonoMod packages:

- `PlatformHelper` can be treated as compatibility glue over MonoMod 25
  `PlatformDetection` and `DynDll.DllExtension`. OpenACAI should not build new
  runtime logic around the old helper name.
- `DynDllImportAttribute` and `ResolveDynDllImports` can be replaced with
  explicit library/export lookup through `DynDll.TryOpenLibrary` and
  `DynDll.GetExport`, followed by
  `Marshal.GetDelegateForFunctionPointer<T>()`.
- `AsDelegate<T>()` is not needed; direct `Marshal.GetDelegateForFunctionPointer<T>()`
  is the stable substitute.
- `ReflectionHelper.IsCore` and `ReflectionHelper.IsMono` should be replaced by
  target-framework checks where the answer is compile-time obvious, or by
  `PlatformDetection.Runtime` where runtime detection is actually needed.
- RuntimeDetour 25 adds/changes `IDetour` surface expectations such as
  `DetourConfig Config`. The OpenACAI IL2CPP/native-detour host should own that
  adapter instead of scattering BepInEx-era assumptions through gameplay code.

Current package baseline: HarmonyX `2.16.1`, MonoMod.RuntimeDetour `25.3.4`,
and MonoMod.Utils `25.0.12`. MonoMod 25 is officially comfortable through
`.NET 10`; `.NET 11` remains an explicit OpenACAI preview override until logs
and in-game testing prove it.

## Action Items

1. Continue startup and UI timing instrumentation before judging `.NET 10` vs
   `.NET 11` by feel:
   - Implemented in the public bridge: OpenACAI bridge load phases, loader state
     write, mod inventory refresh, native mods menu create/rebuild, and mods
     menu open/close through `openacai.loader.timings`.
   - Implemented for the Sons menu bridge: title/pause Mods button attach scan
     duration and attach latency are recorded, and state refreshes now persist
     the latest timing/menu bridge data after meaningful UI events.
   - Still needed outside the public bridge: Doorstop hostfxr start, BepInEx
     entrypoint reached, earliest title/pause menu creation before the bridge
     sees it, and private admin F7 panel open/close.

2. Test `.NET 11` with and without ReadyToRun once startup is stable.

3. Keep `.NET 10` as the public release package until `.NET 11` survives
   repeated real game sessions with logs.

4. Avoid adding new OpenACAI code to `net35` or `net481`.

5. Use `netstandard2.1` as the default .NET Standard target for Sons-specific
   Modstoke SDK abstractions.

6. Multi-target only the compatibility/abstraction surface that actually needs
   old-mod support; keep implementation code modern.

7. Continue moving BepInEx/RedLoader-shaped implementation details into
   OpenACAI-owned runtime modules. Preserve BepInEx and RedLoader APIs as
   compatibility adapters, not as the architecture we build new features on.

## Sources

- https://dotnet.microsoft.com/en-us/platform/support/policy
- https://learn.microsoft.com/en-us/dotnet/core/releases-and-support
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-6
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/11
- https://learn.microsoft.com/en-us/dotnet/standard/frameworks
- https://learn.microsoft.com/en-us/dotnet/standard/whats-new/whats-new-in-dotnet-standard
- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-framework
- https://learn.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade

