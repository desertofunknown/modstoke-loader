# Modstoke Loader Runtime Rewrite

Developer: Alex Cooper

Modstoke Loader should become an OpenACAI-owned mod runtime for Sons Of The
Forest. BepInEx, RedLoader, and SonsSdk remain important upstream references and
compatibility targets, but they should not remain the permanent foundation for
new OpenACAI features.

Core source paths below are relative to `src/Modstoke.Loader`.
Use the [source guide](../src/Modstoke.Loader/README.md) for the
current folder layout; type and assembly names retain their existing identities.

## Goal

Build a loader that gives OpenACAI direct control over:

- native host startup and diagnostics
- IL2CPP interop generation and repair
- plugin discovery and dependency resolution
- lifecycle/event dispatch
- safe Harmony/native-detour gating
- RedLoader and BepInEx compatibility shims
- Modstoke SDK services for Sons-specific modding
- public UI/menu provider services for third-party mods and first-party tools
- manager-facing install, update, file hash, and state metadata

The public loader should still run existing BepInEx plugins and RedLoader mods
where practical. The difference is architectural: those ecosystems become
compatibility modes hosted by OpenACAI Runtime, not the runtime identity itself.
Native Modstoke packages should move toward `Modstoke/Mods`; legacy `Mods` and
`BepInEx/plugins` remain compatibility roots for existing mods.

The final distribution should not require the original BepInEx or RedLoader
loader engines. Legacy mods may still require Modstoke-owned compatibility
assemblies with names such as `BepInEx.Core`, `RedLoader` and `SonsSdk`; those
assembly and API identities are contracts, not a reason to retain the original
startup, discovery or execution engines. Replace the required behavior behind
those contracts before removing an engine from the package. Preserve existing
settings, install paths and supported runtime choices during the transition.
The package builds the BepInEx, RedLoader and SonsSdk compatibility assemblies
from Modstoke-owned projects. Third-party interop and asset libraries remain
pinned dependencies; broad runtime acceptance is still open.

Game access is not limited to APIs exposed by earlier loaders. Static binary
metadata and native disassembly can identify additional event, state and patch
boundaries. New APIs must describe the actual thread, lifetime and authority
constraints of those boundaries. A mapped or compiled hook is not proof of live
behavior, and replication observations must not be presented as player intent.

## Current Transitional State

The current source uses the components below. Compiled changes can be ahead of
the installed runtime and release package; ownership is not runtime acceptance.

- SonsSdk custom-save registration and serialization execute in Modstoke Core.
  The compatibility assembly retains its native serializer and callback types,
  public saveable API, shared registry and `Mod` save-file identity. Existing
  `Name`/`Data` records and JSON converter settings are unchanged. This path
  requires matching Core and SonsSdk assemblies; it remains separate from the
  SDK's sidecar save registry.

- SonsSdk's fallback scene, gameplay-event and startup routines execute in
  Modstoke Core. The compatibility facade retains event objects, registered
  callbacks, Harmony targets and shared startup state. Explicit lifecycle
  selection still determines which startup and completion model runs; the
  fallback keeps its existing client coroutine and dedicated-server sequence.
  Matching Core and SonsSdk assemblies must be deployed together.

- Save callback dispatch and patch installation, command registration, manager
  lookup and bootstrap fallbacks also execute in Core. SonsSdk retains the
  registered callback methods, shared patch status and public entry points.
  Explicit lifecycle selection, save event order and existing dedicated-server
  patch behavior are preserved.

- RedLoader's coroutine, managed-enumerator and scene/lifecycle fallbacks execute
  in Core, alongside SonsSdk input registration and polling. The compatibility
  assemblies retain native wrapper types, callback targets, queues and cached
  values. Existing explicit driver selection and input callback ordering remain
  unchanged.

- SonsSdk networking and blueprint fallback services execute in Core. Packet
  framing, relay targets, chat callbacks, prefab registries and recipe state
  retain their existing contracts. Explicit lifecycle selection still chooses
  the original or selected behavior; both paths require matching Core and
  SonsSdk assemblies.

- Shared UI setup, settings panels, dialogs, tooltips and input-icon fallbacks
  execute in Core. Compatible overlay and blur paths share their existing Core
  implementations. SonsSdk retains native wrapper types, callback targets,
  shared registries, build-specific diagnostics and widget/asset support code.
  These contracts now build in `src/Modstoke.Compatibility.RedLoader` and
  `src/Modstoke.Compatibility.SonsSdk`, retaining assembly names and version
  0.8.6.0. The host build selects Debug/net6.0 for these facades to preserve
  existing conditional behavior while hosting them on .NET 11. The original
  RedLoader/SonsSdk projects no longer supply the packaged facade assemblies.
  Pinned third-party libraries and generated game wrappers remain build inputs
  from the compatibility cache; this is not a dependency-free build.

- Script discovery, reload watching, assembly loading and script callbacks execute
  in Core. The RedScript API and RedScriptManager entry points keep their existing
  identity, with a typed lazy binding to the owned lifecycle. Script locations,
  reload timing and callback order are unchanged. Matching RedLoader and Core
  assemblies must be installed together. Runtime acceptance remains open.

- RedLoader's mod registration and event operations forward to Modstoke Core.
  Each mod has an owned registration driver; each event has an owned dispatcher
  created lazily on first use. Constructing an event does not start the runtime.
  Existing event objects, delegates, subscriber snapshots, virtual mod callbacks
  and registry identities remain available. Registration rollback removes only
  its own subscriptions, stale callbacks remain inactive after re-registration,
  and assembly-wide unregistration still isolates cleanup failures. Event
  priority, duplicate matching, reentrancy and one-shot claiming retain their
  existing semantics, including split dispatch during SDK initialization.
  These operations require the matching Core module. Other RedLoader/SonsSdk
  engines remain in the package; this is not yet a replacement for both assemblies.

- RedLoader preference loading, saving, reloads and category writes now forward
  to Modstoke Core through lazy typed bindings. Existing configuration objects,
  file paths, TOML/legacy formats, mapper and document helpers remain compatible.
  Global and category operations retain their distinct callback and error
  behavior, including early category loads before global initialization.
  Tomlet remains the existing shared serialization dependency; dormant file
  watchers stay disabled. These operations require the matching Core module.

- Mod callback batches and reflected messages execute in Modstoke Core through
  cached typed bindings. Snapshot order, live registration checks, deferred
  unregistration and legacy reflection behavior remain compatible. Attributed
  IL2CPP registration also uses Core, retaining the shared readiness/queue
  fields, ordered draining, reentrancy and per-type failure isolation. These
  paths do not add concurrent-registration guarantees or permanent deduplication.
  RedLoader component creation uses a separate owned host; its manager object
  name, lifetime behavior and identity stay distinct from the BepInEx host.
  Direct injection and serialization libraries remain support dependencies.

- RedLoader's chainloader contracts forward initialization and the native scene
  hook to Modstoke Preloader, and mod execution and post-Unity startup to Core.
  Virtual callbacks, shared processor state, completion events and the original
  native-call order remain intact. Unlike upstream, the scene hook is not removed
  after it fires: Unity, interop calls and mod threads all enter
  `il2cpp_runtime_invoke`, so no unhook point is provably free of callers. It is
  applied as a process-lifetime gateway, an activation error retains it rather
  than releasing reachable storage, and after the one-time start it only
  forwards to the original. Core binding is deferred until
  execution; arming the hook does not load game-facing Core types. This does not
  activate the standalone Red chainloader in either suite startup mode. The
  explicit Red compatibility switch still selects the SDK processor, not that
  chainloader. The matching owned modules are required.

- RedLoader's `INativeDetour` factories use Modstoke's shared native-hook engine.
  The Red interface, interop adapter and default Dobby selection remain intact;
  BepInEx provider preferences do not select Red's backend. A small retained
  P/Invoke shim keeps native lookup and registered resolvers in `RedLoader.dll`.
  The old Red hook-state engine and unused Funchook implementation are excluded
  from compilation. Callbacks remain rooted until native cleanup succeeds;
  native errors now propagate instead of being ignored. `Undo` clears applied
  state, and `Free`/`Dispose` release prepared hooks, including unapplied ones.
  Reapplying a Dobby hook requires callers to acquire its new trampoline.
  Public signatures are unchanged, but these corrected failure and lifetime
  semantics need live validation. No startup mode is enabled by this change.

- RedLoader's `AssemblyPatcher` delegates discovery, patch execution, loading
  and cleanup to Modstoke Preloader. Its context, patch definitions, plugin
  callbacks, configuration entries and Cecil reader remain RedLoader contracts.
  Discovery still recognizes the RedLoader assembly identity. The original
  snapshot timing, reflected invocation, error boundaries and dump/load behavior
  are retained, including the supplied loader callback's bytes and source path.
  Internal friend access connects the owned engine to those existing contracts;
  public signatures and settings are unchanged.

- RedLoader type discovery, binary metadata caching and fallback assembly
  resolution execute in Modstoke Preloader. Its public resolver objects,
  replaceable search-directory set, cache records and event remain shared
  contracts. Typed bindings preserve the distinct read/write generic constraints,
  RedLoader search precedence, cache format and existing error behavior. Cache
  enablement remains independent of BepInEx preferences. The stateless exception
  formatter stays in the compatibility assembly without loading the owner.

- The standalone RedLoader `Preloader.Run` entry point now forwards to Modstoke
  Preloader. Console and splash setup, the native import resolver, interop
  initialization, patcher execution and chainloader startup keep their existing
  order, RedLoader configuration and exception behavior. The suite's startup
  modes are unchanged; this does not activate the standalone route.

- RedLoader's Doorstop entry and Unity runner also forward to Modstoke Preloader.
  Their names and callable signatures remain available. Early binding can locate
  the installed owner before the standalone resolver exists. The resolver is
  registered before preferences initialize, since preference storage already
  uses owned Core. Loaded assemblies and the four legacy search roots retain
  precedence, followed by direct lookups in the packaged runtime directories.
  Existing environment keys, log paths, platform setup, Cecil registration and
  Reshade behavior remain compatible. Startup releases its mutex only after
  acquiring it and disposes the handle. This does not select or validate the
  standalone route.

- RedLoader's interop manager forwards path queries, initialization and assembly
  preloading to Modstoke Preloader. Existing configuration entries, cached path
  substitutions, environment overrides, MD5 marker format and runtime adapter
  remain compatible. Conversion and generation use the owned pipeline, with
  RedLoader's Unity version and paths supplied explicitly. Replacement wrappers
  and base libraries are staged before publication; previous directories remain
  available as backups. Support files are preserved, and failed generation
  leaves existing wrappers intact. The legacy dummy-dump setting remains a
  reserved, inactive entry. The public Harmony logger retains its identity.
  This requires matching compatibility and preloader assemblies; no startup
  mode is enabled and regeneration has not been runtime-validated.

- The Sons SDK entry point delegates mod discovery, dependency resolution,
  manifest loading and post-Unity subscriptions to Modstoke Core. Existing
  callback method/target pairs, lifecycle selection and optional legacy-menu
  behavior remain compatible. Core also owns resource loading, linked-UI
  operations and GLTF importer registration. Resource dictionaries and mutable
  UI registrations remain shared through their original SDK types, and the
  importer retains its existing type identity. Core and SonsSdk must be updated
  together. Public API names and save formats are unchanged; remaining SDK
  fallback bodies and custom-save serialization are still transitional.

- `Modstoke.Compatibility.BepInEx.Unity.Common` produces the owned
  `BepInEx.Unity.Common.dll` replacement. Its unsigned 6.0.0.0 identity,
  `UnityInfo` properties, reflected initialization flag and three friend
  assemblies remain compatible. The `Version` property still uses the shared
  AssetRipper type. Default startup keeps its existing reflected publisher;
  legacy startup can call the replacement's internal initializer. The latter
  bounds manager-file reads, tries each known offset independently, falls back
  after inaccessible files, and retains the managed CoreModule version estimate.
  Packaging replaces the upstream DLL and XML and removes any stale symbols.
  No original loader engine is needed by this assembly. Other legacy engine
  assemblies remain in the transitional package; this is not yet engine-free
  mod hosting or a claim of live compatibility acceptance.
- `Modstoke.Compatibility.BepInEx.Preloader.Core` retains the unsigned 6.0.0.0
  preloader identity and legacy patcher, logging and startup contracts.
  Its `AssemblyPatcher` forwards to the existing Modstoke engine through a lazily
  bound typed factory; it does not include the old patching engine. Legacy
  callbacks still receive patched bytes and the original source path, while
  configured dump loading bypasses that callback. Normal Modstoke startup keeps
  its default-context loading behavior. Shared patcher collections, configuration
  keys, log buffer and friend access remain available. The facade targets the
  current .NET 11 distribution, not a standalone Mono/.NET Framework runtime.
  Compile-time contracts do not prove live patcher or compatibility-mode startup.
- `Modstoke.Compatibility.BepInEx.Unity.IL2CPP` replaces the original IL2CPP
  runtime assembly while retaining its unsigned 6.0.0.0 identity, plugin API,
  input constants, coroutine adapters, native-detour contract and logging types.
  Its entrypoints route to the owned preloader without repeating mode selection.
  Explicit compatibility mode retains the legacy plugin registry and chainloader
  extension points; the default path retains Modstoke's plugin host. Both use
  owned interop and patching services. Compatibility patchers retain their
  byte-loading callback, and native activation requires completed patching.
  The compatibility scene trigger is owned and retained for the process lifetime.
  Logging wrappers preserve per-instance events, latest-created-source capture
  and no-op disposal while Modstoke owns native callbacks and Unity subscriptions.
  Component creation also routes to the owned Core module. Packaging overlays
  the DLL and XML, removes stale symbols and records replacement provenance.
  Original IL2CPP startup, generator orchestration and native engines are absent;
  RedLoader/SonsSdk are still transitional engine dependencies. These
  source changes need fresh runtime acceptance in both supported startup modes.
- The legacy `BaseChainloader<TPlugin>` batch delegates activation to
  `CompatibilityPluginBatch` in the owned preloader. Virtual discovery,
  load ordering, plugin construction and event callbacks retain their contracts,
  including unconstrained `TPlugin`. Dependency bookkeeping, per-batch assembly
  caching and module initialization no longer execute in the BepInEx Core body.
  The compatibility batch preserves legacy failure and returned-list behavior;
  it does not reuse the default host's stricter activation rules. The default
  metadata reader, process/version selection and dependency ordering now use
  `CompatibilityPluginCatalog`. Virtual overrides remain honored.
- `TypeLoader` discovery, binary metadata caching and fallback search now use
  `CompatibilityTypeDiscovery`. The public resolver/reader fields and
  replaceable search-directory set keep their identities and lifetime. Resolution
  events retain multicast semantics; the owned patcher resolver is not substituted
  for this shared resolver. Lazy typed bindings do not initialize runtime services.
  Legacy cache names, format, enable setting and failure behavior are preserved,
  as are generic constraints and selector/filter behavior. These bindings require the matching
  Modstoke preloader and do not provide standalone BepInEx hosting.
- `Modstoke.Compatibility.BepInEx.Core` supplies the unsigned 6.0.0.0
  `BepInEx.Core.dll` compatibility assembly. Configuration, logging, metadata,
  console, paths and utility implementations remain upstream-derived support
  code with their licenses and attribution intact. This includes recursive
  filesystem resolution helpers and platform console implementations, not just
  passive API declarations. Chainloader initialization, logger setup and
  execution forward to `CompatibilityChainloaderLifecycle`; discovery,
  ordering and activation retain the owned engines above. Virtual callbacks,
  configuration entry identity and initialization order remain unchanged.
  Packaging replaces the DLL/XML and removes stale symbols. Assembly identity,
  friend access, generic constraints and dependency type scopes are retained;
  the informational-version commit now identifies the suite source. This
  assembly needs the matching Modstoke preloader and fresh runtime acceptance.
- `Modstoke.NativeHost` as the default Rust `winhttp.dll`
  hostfxr/CoreCLR native host. The UnityDoorstop fork remains a diagnostic
  comparison path through `-UseDoorstopForkNativeHost`. The native host now
  generates its WinHTTP proxy stubs/load table from `native_host_exports.def`
  during the Rust build, keeping the exported ABI pinned to the DEF file.
  The CRT's link-only DllMain entry jumps to the Rust callback without declaring
  another DLL export. This avoids the public-export semantics of
  [Rust no_mangle](https://doc.rust-lang.org/reference/abi.html#the-no_mangle-attribute)
  while preserving the 82-symbol proxy contract.
  For starts on DirectX 12, whether from the Loader's graphics API option or
  `-force-d3d12` in the launch options, the host wraps the engine's
  `D3D12CreateDevice`. Before the device exists, the wrapper forces device-removed
  extended data (DRED) on: breadcrumbs, breadcrumb contexts and page-fault tracking.
  A fence armed at its largest value then wakes a thread when the device is
  removed. The thread writes the removal reason, the unfinished command lists
  around where the GPU stopped, and any page fault's resources to
  `Modstoke/UserData/dx12-device-removed-<time>.txt`. The engine leaves its command
  lists unnamed, so the host also hooks pipeline creation, `SetPipelineState` and the
  draw and dispatch methods. It records each pipeline's shader DXBC checksums, and the
  pipeline each draw of a command list ran with over that list's last four recordings.
  A report then names the stalled draw's shaders and saves their bytecode next to it
  as `dx12-shader-<stage>-<checksum>.dxbc`.
  The first such report stopped in HDRP's forward-lit pixel shader. Its punctual,
  area and reflection-probe loops run as many times as the light count it reads
  from the fine-pruned tile light list, a 16-bit value. A tile holds at most
  127 lights (`2 * dwords per tile - 1`), so a larger count can only come from a list
  that was not built when the draw read it. Each pixel then loops up to 65535 times
  and the GPU watchdog removes the device. On DirectX 12 starts the host now hands
  pipeline creation a copy of every shader that reads those counts, with a `umin`
  after each read that keeps the count within a tile, and recomputes the DXBC
  checksum (`src/dxbc.rs`). Copies are made once per shader. If the runtime
  refuses a copy, that pipeline is created from the stock shaders.
  `MODSTOKE_DX12_STOCK_SHADERS=1` turns the change off. The removal report says how
  many shaders were bounded and marks the stalled draw's shaders that were.
- `Modstoke.Bootstrap.dll` as the Doorstop target assembly.
  It records OpenACAI bootstrap and chainloader state. The default
  `openacai-native` path now owns preloader orchestration and the IL2CPP
  chainloader adapter boundary. The adapter also runs an OpenACAI chainloader
  port-readiness probe that confirms the IL2CPP runtime invoke export,
  native-detour factory surface, BasePlugin compatibility type,
  ManualLogSource, packaged core assembly, and direct core runtime entrypoint
  are resolvable. It now arms an OpenACAI-owned `il2cpp_runtime_invoke`
  trigger, waits for `Internal_ActiveSceneChanged`, preloads IL2CPP interop,
  creates the core log source, and invokes `RuntimeEntryPoint.Load`
  directly. Upstream BepInEx chainloader execution remains a measured fallback
  only before the owned trigger attempts activation and when no owned live hook
  remains. The owned path publishes an adapter `IL2CPPChainloader.Instance`
  for legacy plugins, without upstream initialization, discovery or native hooks.
  Selected BepInEx IL2CPP compatibility components are still used as
  transitional dependencies while those internals are ported into
  OpenACAI-owned modules.
- Core startup has explicit not-started, starting, ready and failed states.
  Its original `Load(ManualLogSource)` entrypoint is unchanged. Successful
  duplicate calls are no-ops; concurrent/reentrant calls fail clearly, and a
  failed attempt cannot rerun partially initialized services. Early service
  initialization and later mod hosting share one failure boundary. Diagnostic
  publication is best-effort and cannot replace the original exception, which
  reaches either the owned adapter or BepInEx's per-plugin failure handler.
  Bootstrap readiness means preloader completion only. After the native-path
  handoff, the adapter writes the top-level chainloader state: `ARMED` while
  waiting, `RUNNING` during execution, then `READY` or `FAILED`. Bootstrap no
  longer overwrites this asynchronous outcome on return. The running snapshot
  does not claim the direct port is complete. Compatibility initialization
  retains its existing readiness meaning; it does not prove every plugin loaded.
  Failure files remain best-effort; actual failure and duplicate-call behavior
  has not been exercised in-game.
- Failed IL2CPP runtime initialization attempts each owned cleanup step separately:
  runtime disposal, assembly-resolver removal, database-location restoration and
  log-source removal/disposal. Cleanup warnings cannot replace the startup error.
  The phase runner preserves original exception stacks when unwrapping reflection
  failures and still attempts logging shutdown if failure diagnostics throw.
  Bootstrap failure reporting is also best-effort. This does not guarantee cleanup
  inside third-party runtime/generator code or make failed startup retryable.
- `Modstoke.Preloader.dll` is a late-loaded, typed startup
  module. Bootstrap remains dependency-free until assembly resolution is ready.
  Bootstrap selects its services through the BCL-only `GetSharedInstance`
  factory. Direct construction remains available, but only one services instance
  may own logging and native startup in the loaded runtime assembly. The factory
  reuses an owner selected by an earlier direct caller. Resolver installation is
  serialized and interop initialization is single-attempt, including failure;
  ownership is not reset after cleanup. State reports the selected owner and
  whether it was requested through the factory. The IL2CPP facade uses this same
  owner. Other service operations are not generally thread-safe.
  The module owns the GameAssembly resolver, managed interop resolver, runtime
  creation, logging adapter, and direct-path preloading. It checks essential
  wrapper metadata without loading those assemblies before patchers run.
  Startup failures detach its managed resolver and restore the prior database
  environment setting. `InteropSettings` owns typed configuration bindings,
  retaining the existing keys, defaults and path overrides. `InteropConversion`
  uses public Cpp2IL APIs and the owned interop pipeline. It scopes logging
  subscriptions, restores the LibCpp2IL log writer and resets analysis state after
  materializing dummy assemblies. Owned conversion cleanup attempts each reset,
  detach, removal and disposal step
  separately. It preserves the first failure and its stack; a cleanup-only
  failure still fails conversion. Secondary cleanup diagnostics are best-effort.
  The owned interop logger also contains formatter, exception-rendering and
  listener failures so they cannot abort runtime work. Cpp2IL
  forwarding uses the same best-effort diagnostic path, retaining source labels
  and level mapping; a null message has an empty body. This does not isolate
  other subscribers installed directly on the third-party logging buses.
  Preload warnings cannot stop later assemblies, and a failed completion log
  cannot reclassify completed preloading. Load counts and statuses still reflect
  actual assembly-load outcomes; enumeration and other operation failures still
  fail the phase. Patcher failure details tolerate a throwing exception message
  getter without replacing the original exception. These are owned diagnostic
  boundaries, not evidence of fault-path or fresh-game startup acceptance.
  `InteropAssemblyGeneration` owns pass sequencing and implementations, retaining
  the installed pass order, including awaiter support and optional Unity
  unstripping. `InteropGenerationOptions` owns the consumed options and rename-map
  parser, retaining installed defaults, prefix sets and plain/gzip parsing rules.
  The owned path no longer constructs the upstream generator host or changes
  Common's global scanner/logger during generation. Runtime initialization and
  the explicit BepInEx fallback are unchanged. The preloader no longer has a typed
  generator dependency, and owned cache identity no longer includes that assembly.
  The legacy marker reads its version without loading it; the generator remains
  packaged for explicit BepInEx compatibility. AsmResolver parsing and
  shared Common/runtime services also remain dependencies.
  `InteropScanState` clears the caller map, live-method set,
  obfuscation flag and owned metadata-init RVA before and after each owned run.
  These values belong to the run; neither the upstream caller/live-method maps nor
  its obfuscation flag are read or reset. Method-name finalization consumes the
  owned reachability set after scanning. An entry reset failure stops generation.
  This does not establish concurrent safety for the conversion or runtime services.
  `InteropRewriteContext` and the assembly/type/member contexts own graph creation,
  identity maps and two-phase member construction. Restored types remain excluded
  from ordinary generation traversal. Unity metadata disposal stays with the
  context, while orchestration separately disposes game metadata.
  `InteropAssemblyMetadata` loads the ordered game and Unity assembly collections.
  `InteropAssemblyResolver` supplies their resolver and the rewritten graph's
  resolver without probing host runtime directories. Failed loads clear partial
  state while preserving the original error. The owned path no longer uses the
  generator's metadata-access providers; assembly parsing still uses AsmResolver.
  `InteropTypeNames` owns the two-pass type-name groups and previous-name snapshot
  within each run. `InteropNaming` and `InteropNameUniquifier` own identifier cleanup,
  obfuscation checks, prefix selection, signature names, stable hashes and ranked
  name fragments. UTF-8 byte cleanup, score ties and option precedence retain the
  installed rules so this ownership change does not intentionally rename wrappers.
  `InteropTypeGeneration` owns type creation, struct classification,
  base types, generic constraints, member traversal and enums. Nested registration
  order, naming heuristics, offset rejection, generic-base ordering and enum metadata
  retain the installed generator's rules. Rename warnings use isolated diagnostics.
  This is not full independence from Il2CppInterop or proof of fresh-generation
  compatibility.
  `InteropTypeReferences` owns signature rewriting and cached type classification
  for the owned generation passes. Array selection, destination-module generic
  parameters, pointer/by-reference recursion and original-type identity lookup
  retain the installed rules. Multidimensional arrays still use the object-base
  fallback; this does not expand supported signatures. Owned context constructors
  and member finalization use the same rewriting implementation.
  The descriptor value-type predicate and generic constraint mask are also owned;
  `System.Enum` keeps its existing special treatment. `InteropAttributeValues`
  decodes source offsets, RVAs, tokens and attribute strings, preserving duplicate
  rejection, missing-value conversion and hexadecimal width/error rules.
  `InteropRuntimeReferences` builds the runtime types, method references and
  attribute constructors used by owned passes. Its module cache belongs to the
  rewrite context, keeps generated-corlib lookups lazy and reads write-barrier
  selection at use time. Generic-array caches compare type-signature identity,
  rather than treating equal hash codes as equal types. The runtime methods
  themselves remain shared dependencies.
  `InteropMethodDeclarations` owns method-finalization traversal, caller-count
  attributes, per-run diagnostic counts and parameter copying. The upstream
  cumulative dead-method counter is no longer updated. `InteropMethodContext`
  owns declaration construction, name finalization and generic method stores.
  Parameter names, type rewriting, flags and constants retain their existing rules. Small internal
  parameter helpers are replaced with direct AsmResolver operations. Finalization
  completes for all methods before parameter copying begins. No wrapper signature
  change is intended; fresh-generation compatibility remains unverified.
  Params forwarding overloads for ordinary rewritten and restored Unity methods
  are generated by owned code. Eligibility,
  generic constraints, parameter attributes and string/struct/reference array
  conversion follow the installed generator, including its System.Enum exclusion
  from value-type array selection. Argument instructions reference the overload's
  own parameters. Generic calls now instantiate the target with the overload's
  method type arguments, fixing the missing instantiation observed in the cached
  `Task.WhenAll<TResult>` params wrapper. This uses the MethodSpec representation
  described by [ECMA-335, II.22.29](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
  Existing cached wrappers are not patched in place; the correction applies during
  owned regeneration. Live execution of the corrected overload remains unverified.
  `InteropMethodRestoration` owns optional Unity method discovery, signatures,
  generic constraints and property restoration. Its type resolver retains the
  installed generator's managed-corlib, array and Unity assembly lookup rules.
  Pending method bodies belong to one generation run and are cleared even when
  translation throws; a failed run cannot leave jobs in a process-wide queue.
  Unity value math (`Vector2/3/4`, `Vector2Int/3Int`, `Quaternion`, `Mathf`,
  `Color`, `Rect`, `Bounds`, `Matrix4x4`, `Plane`, `Ray`) is the one place where
  existing native wrappers are replaced: `InteropManagedMath` admits a method only
  when Unity's managed body is arithmetic over those types, primitives and
  `System.Math` (no native calls, statics written, arrays, strings, exceptions or
  virtual dispatch), and the translated body replaces the `il2cpp_runtime_invoke`
  wrapper, which also boxed every result on the IL2CPP heap. A body that fails to
  translate keeps its native wrapper. About 440 methods qualify in 2022.2.16,
  including the operators, `Lerp`, `normalized`, `Cross`, `Dot` and the value
  constructors; engine-implemented ones such as `Slerp`, `Euler`, `TRS` and
  anything reading `Time` stay native. Translated bodies call `System.Math` and
  `System.MathF` on the CLR instead of the game corlib's native wrappers. Setting
  `MODSTOKE_NATIVE_UNITY_MATH=1` keeps every wrapper native; the value is part of
  the interop input hash, so changing it regenerates the assemblies.
  `EngineBootConfig` adds `memorysetup-temp-allocator-size-preload-manager`
  (64 MB) and `memorysetup-temp-allocator-size-main` (32 MB) to the game's or
  server's `boot.config` when they are absent; Unity's defaults (256 KB and 4 MB)
  overflowed to the shared heap about 370,000 and 5,000 times in one world load,
  and the measured peaks were 63 MB and 31 MB. Values the Loader wrote earlier are
  raised; any other value is never changed, the rest of the file is kept byte for
  byte, and the settings apply from the next start; Steam's file check restores the
  original, so the Loader checks again at every start.
  `Modstoke.Loader.runtimeconfig.json` runs the managed heap with server collection
  on four heaps that are not pinned to cores, a fixed heap count and background
  collection; the default single-threaded collector paused the game thread 8-17 ms
  per gen-0 collection. The Loader also sets `SustainedLowLatency`, so the game
  thread does not wait for a blocking full collection. `LoadingPerformance` widens Unity's
  background-integration and async-upload slices from the loading screen until the
  player is in the world, then restores the game's values. It also skips the world
  loader's fixed 0.2 s and 1 s timers. The world loader asks for the game scene only
  after its shader warm-up has finished. That warm-up compiled a few variants and
  then waited frames before the next batch, so at the loading screen's 30 fps it
  held the load for over 4 s while every loading thread sat idle. It now compiles 64
  variants every frame. The game's frame limiter keeps the menus and the loading screen
  at its 30 fps menu limit. That limit is raised to 60 fps in the menus and to 240 fps
  while a world loads, set directly when no limiter is running on the loading screen.
  Pausing the managed collector for the load was tried and removed. The world
  load's time in collector code turned out to be the cost of allocating, not
  collecting, so activation stayed at 4.2 s, while the heap grew by 1.7 GB
  before collection resumed. Once the loader allowed its 23 scenes to activate, captures showed
  4.6 s of idle frames before the game scene appeared: Unity's async texture and mesh
  uploads and its scene integration each advance once per frame, and a scene cannot
  activate until its uploads finish. The game's own values come back once the player
  is in the world.
  `GcWriteBarrierPatch` changes the incremental collector's write barrier,
  `il2cpp_gc_wbarrier_set_field`. It used to mark the written page with a locked
  compare-exchange even when the page was already marked, and engine bindings call
  it on nearly every call that touches a managed object. It now tests the bit first
  and takes the locked path only for an unmarked page. The 16 replaced bytes are
  checked against the known build, written in one aligned store, and read back to
  confirm. `MODSTOKE_NATIVE_GC_BARRIER=1` leaves the barrier as built. The game then keeps the screen black for a
  fixed 3 s of game time before the fade-in (`MainSceneLoadingTrigger`), and that
  wait is dropped once ten frames in a row take under 50 ms. The game's player
  activation check still decides when the fade-in starts.
  `InteropNativeCallStubs` owns restored native-call delegates, static
  initialization and invocation bodies. Reference-typed ref/out temporaries are
  written back after the call instead of discarded; a typed local preserves any
  return value during writeback. Delegate signatures and native resolution names
  retain their existing rules. Object/pointer conversion and writeback emission
  are owned; runtime conversion methods still come from Il2CppInterop.
  `InteropFieldGeneration` owns ordinary value-type field layouts, boxing methods
  and native-backed field accessors. Explicit offsets, one-byte Boolean storage,
  enum-excluding value classification and native conversion/write-barrier helpers
  retain the installed generator's rules. Static pointer getters now return the
  pointer copied by IL2CPP without dereferencing it again; instance pointer
  getters still read from the field address. Existing cached wrappers are not
  patched in place. Setter conversion behavior is unchanged.
  `InteropMethodBodies` owns ordinary native invocation bodies: constructor
  initialization, argument storage, generic out parameters, virtual dispatch,
  exception propagation and reference writeback. Null generic-element params arrays
  are allocated from the runtime element class and wrapped through `Il2CppArrayBase<T>` instead of
  assuming reference-array constraints. A missing native element class is
  rejected before allocation; a null native allocation retains the constructors'
  rejection behavior. Non-null arrays and nongeneric initialization keep their
  previous behavior. Fresh-generation and native-call acceptance remain
  unverified.
  `InteropNativeValues` owns argument/result conversion, field-store and ref/out
  emission for ordinary methods, accessors and restored native-call stubs.
  It preserves the installed null, unboxing, generic-array, pooling and write-barrier
  rules, including non-blittable ref-value unboxing missing from the older source
  checkout. Arguments and temporary locals belong to the generated method; generic
  array return signatures retain the callee's type parameter. Runtime allocation,
  object conversion and garbage-collector helpers remain shared. No native ABI or
  supported-signature expansion is claimed by this ownership change.
  `InteropConstructorGeneration` owns native class/field/method pointer setup,
  generic method stores, pointer constructors and non-blittable value-type
  constructors. Lookup rules, generic parameter numbering, base constructor
  references and native class-initialization timing are unchanged. Generic type
  and method construction share the same native type-array emission. Tokenless
  method counts are local to each generation run, with isolated diagnostics.
  `InteropConversions`, `InteropAwaiterGeneration` and `InteropPropertyGeneration`
  own string/primitive/delegate operators, managed continuation wrappers and
  property/indexer metadata. Delegate addition now preserves null results, as
  subtraction already did. Pointer and by-reference delegate returns are excluded
  from Action/Func conversion generation, matching the existing parameter rule.
  Awaiter eligibility, generic target references, property naming and accessor
  mapping retain their installed behavior. These changes apply on regeneration;
  existing cached wrappers are untouched and live compatibility is unverified.
  `InteropMethodTranslator` owns restored IL, member references, branch targets
  and exception regions. It preserves the source local-initialization flag and
  maps exclusive exception-region ends to the new body's end label. Instruction
  expansion retains the first emitted instruction as each branch/region target;
  AsmResolver recalculates stack limits. Static member declarers can use rewritten
  corlib types, while nested signatures retain managed primitives. Unsupported
  instructions and typed catches still produce the existing failure stub;
  unexpected translation errors still abort generation.
  Concrete value-type `newarr` instructions now use the existing
  `Il2CppStructArray<T>(long)` constructor, matching restored array signatures.
  String and reference-array allocation retain their existing constructors.
  Runtime element-class requirements still apply; generic allocation, element
  address operations and field-token array initializers are not expanded.
  This removes an allocation blocker in restored frustum-plane and audio-analysis
  helpers, but regenerated wrappers and live execution remain unverified.
  Primitive `box` instructions use the existing generated conversions to
  `Il2CppSystem.Object`, which allocate native boxed values. This avoids passing
  CLR-boxed floats to restored `GUILayoutOption` constructors. Missing primitive
  conversions leave a failure stub; generic, enum and other struct boxing are
  not covered by this change. Cached wrappers are unchanged until regeneration.
  `InteropTypeRestoration` and `InteropFieldRestoration` own Unity type and field
  discovery, enum copying and field signatures. Fields reuse the owned type
  resolver. Missing assemblies use one narrowly typed internal registration call
  to keep the shared context's lookup maps consistent.
  Struct restoration supports scalar fields, pointers stored as IntPtr, enums
  and compatible named value types. A dependency plan rejects recursive inline
  layouts, unavailable enclosing types and unsupported reference/generic storage
  before creating missing types. Source packing, size, field order and explicit
  offsets are preserved, including fields in newly restored nested structs.
  Simple marshal descriptors are copied; Boolean fields without a descriptor use
  the same one-byte convention as ordinary rewritten structs. Complex marshal
  descriptors remain unsupported. An unresolved restored instance field fails
  generation rather than emitting an incomplete layout.
  Existing types are not reconstructed, and nonconstant static fields are still
  excluded because their initialization cannot be recovered. Reference-type
  layout normalization and constant handling otherwise retain their prior rules.
  The explicit BepInEx fallback is unchanged.
  Fresh-generation and restored-method execution remain unverified.
  `InteropMethodScanner` owns native-file mapping, method scan orchestration and
  reachability. It releases the acquired view pointer after all scan workers
  finish, then independently disposes the view and mapping, preserving the first
  failure. GC write-barrier marker searches stay within the mapped file, including
  markers crossing span-size boundaries. General xref decoder ranges are bounded
  by the file; invalid offsets and unsupported lengths fail generation. Metadata
  discovery finishes before parallel scanning. Caller/callee edges, root rules,
  address conversion and disabled-cache behavior retain their existing semantics;
  an iterative traversal replaces recursive reachability. Caller/callee list writes
  use owned locking with the same list-monitor scope.
  The method-start list and write-barrier flag are directly owned context state.
  `InteropMetadataScanner` owns metadata-init candidate discovery and per-method
  token/flag scanning. Its immutable pointer belongs only to the current mapped
  scan; the upstream metadata helper and its shared pointer/RVA fields are no
  longer used or reset. The existing candidate order and instruction predicates
  are retained, including the byte-store rule's lack of an IP-relative check.
  Each metadata decoder keeps the 1,000-byte limit, clipped to the mapped file.
  These bounds cover instruction reads, not the validity of the addresses they
  describe or other native reads elsewhere in the loader. Runtime regeneration,
  metadata-output parity and mapping-release acceptance remain unverified.
  `InteropXrefScanner` owns decoder construction and generation-time xref iteration.
  Iced remains the instruction decoder, fed by an owned byte reader that borrows
  only the supplied mapped range and returns EOF at its limit. Decoder bitness,
  options and starting IP retain their existing rules. The private Common decoder
  and iterator calls are removed. Call/jump targets, move-based globals, stop rules
  and shared xref record identities are retained. Generation does not perform the
  runtime global-class filter. Global-operand inspection errors keep their existing
  skip behavior, with best-effort owned diagnostics. Live runtime xref scanning
  and the explicit compatibility fallback are unchanged; output parity is unverified.
  Cleanup attempts to release completed metadata/context objects and reset scan
  state on failure as well as success, preserving the original error. Resources hidden inside a
  throwing third-party constructor or pass remain outside that guarantee.
  `InteropMethodDatabases` owns xref-cache and method-pointer-map generation,
  including unmanaged record serialization with the existing layouts and per-thread
  scratch buffer. Xref attributes are emitted before
  wrapper writing; method tokens are captured afterward. Shared native bodies
  reuse the first recorded method's outgoing and caller ranges independently,
  retaining the earlier fix without a repair pass. Metadata-init fields, record
  order, zero-address behavior, blacklists and disabled-cache output retain their
  existing rules. Pointer maps retain signed address-only sorting and duplicate
  addresses. Both writers retain the shared binary structs, their layouts and
  verbose text formats. Assembly-table offsets are checked
  before narrowing to Int32. Generated output still passes the owned validator
  before promotion. This applies during owned regeneration, not to existing
  wrappers or the explicit BepInEx fallback generator. Normal cache freshness
  policy remains in force; fresh generation and in-game xref results are unverified.
  `InteropAssemblyWriter` owns final wrapper preparation and emission through
  AsmResolver. It imports the framework attribute before normalizing core-library
  references, matching the installed generator's ordering. Existing wrapper
  framework metadata and System.Runtime 6.0 reference identity are retained;
  these do not select the loader's runtime. Reference objects stay in place and
  copied hash/key arrays remain independent. Every manifest module is prepared
  before output filtering, with macro optimization and method-token allocation
  in the existing traversal order. Blacklists and serial/parallel writing options
  are unchanged. Method-map generation follows completed writes; build failures
  still propagate through the existing staged-generation cleanup and validation
  boundary. Individual file writes are not atomic. The explicit BepInEx fallback
  is unchanged; fresh-generation and output-parity acceptance remain unverified.
  The old private conversion/config bridge is removed. The libraries themselves
  remain dependencies. Interop detours now use the owned provider
  described below. The BepInEx fallback accepts instruction sets already registered by
  OpenACAI instead of failing its static initialization on duplicate keys.
- `NativeHookStatusWriter` owns native-hook diagnostic serialization, path
  selection and file replacement on the owned preloader path. It registers
  before preloader logging/interop and publishes the current shared snapshot,
  using the original schema and plugin-root-based file location. Each UTF-8
  snapshot is fully written and closed in a unique same-directory temporary
  file before replacing the public file. A failed write leaves the prior
  complete file and attempts to remove only its own temporary file; no retries,
  stale-file sweeps, crash-durability or reader-availability guarantee.
  Installation and write failures are diagnostic only, with installed/path/
  write-count/failure-count/last-error facts in preloader state. Publication
  does not log or call hook APIs while holding the writer lock. BepInEx
  patch0021 preserves the status model and public marking APIs as a shared
  compatibility contract, with a one-writer friend bridge. Compatibility
  fallback in the same process uses that writer too; standalone BepInEx
  retains its original writer when none is registered. No second runtime-root
  file is introduced, and hook/provider execution semantics are unchanged.
  Concurrent read/write and forced-I/O-failure runtime acceptance remain pending.
- `InteropGeneration` owns cache decisions and generated-file lifetime.
  SHA256 fingerprints cover native code, metadata, Unity version and libraries,
  cached library archives, rename maps, generation options, and dependency build
  identities, including the owned conversion module build. Cache policy v3 excludes
  the unused upstream generator. An older fingerprint requests regeneration when
  updates are enabled; existing validated output remains eligible for failure
  fallback, and updates-disabled behavior is unchanged. The manifest schema is
  still 1, so existing output manifests remain readable.
  `openacai-interop-cache.json` records the output DLLs and both
  method databases together. Validation checks assembly metadata, address-map
  assembly/token bounds, and xref database header/record sizes without loading
  wrappers. Method-address keys must be nonempty and sorted in signed order;
  duplicate addresses remain valid for shared methods. Counts must also fit the
  consumer's signed index and table-offset arithmetic. Generated cached-scan
  attributes are decoded through the metadata API without executing constructors
  or resolving referenced types. Their caller and outgoing-reference ranges must
  be nonnegative, ordered and contained in the xref record set. Missing fields
  keep their zero defaults; an empty range at the record-count boundary is valid.
  Malformed or duplicate range fields are rejected. These are file/metadata
  consistency checks, not proof of runtime compatibility, correct reference
  semantics or valid native addresses and metadata-initialization pointers.
  Generation uses a process-exclusive file lock and a separate staging
  directory. Replacement rolls back on move failure and retains the previous
  directory as a backup on success. A flushed, atomically published replacement
  intent records its transaction ID before either move. At startup, under the
  generation lock, interrupted replacements restore their specific backup (or
  finish a first installation) before cache validation. Recovery never scans
  unrelated backups or overwrites an existing destination; malformed or
  unrecoverable records fail without deleting the remaining files. The old
  age-based backup sweep is not used.
  A failed regeneration can use existing output only after validating it again;
  state explicitly labels that output potentially stale without claiming it is
  byte-for-byte unchanged. Generation and recovery diagnostics cannot invalidate
  successful promotion or an accepted fallback. Owned staging, intent and
  download cleanup remain best-effort; cleanup warnings cannot replace the
  operation's error. Failure formatting is guarded as well. These protections
  do not cover cleanup hidden inside third-party conversion or stream APIs.
  Fault-injected conversion/cleanup acceptance remains open. Process-interruption
  recovery is implemented but not yet exercised in game; filesystem/power-loss
  durability is not claimed.
- `UnityBaseLibraries` downloads/extracts into owned temporary storage and
  validates managed libraries before replacement, preserving the current cache
  on download/extraction failure. Existing local archives and offline sources
  remain supported; an empty source leaves existing libraries alone. Config
  values and keys are retained; binding descriptions are now OpenACAI-owned.
  With updates enabled, a legacy interop set without an
  OpenACAI cache manifest is regenerated once to establish full provenance.
  With updates disabled, structurally valid existing files are kept without a
  freshness claim; broken existing output produces an actionable failure.
  The legacy `assembly-hash.txt` is written for the explicitly selected BepInEx
  bootstrap mode when its generator identity is available. Its version is read
  from an already loaded assembly or the packaged DLL's metadata, without loading
  the generator. An unavailable or invalid identity omits that marker with a
  warning rather than failing owned generation. The legacy hash byte order and
  format remain unchanged; the owned cache never trusts this marker. The upstream
  mode retains its own generator requirements and missing-marker behavior.
- `AssemblyPatcher` owns discovery, patch execution, loading and Cecil
  lifetime. Original BepInEx `BasePatcher`, context and attribute identities
  remain compatible; a friend declaration permits setting the context's dump
  path. AssemblyPatcherMetadata owns ancestry traversal and declared identity
  extraction. An iterative, reference-identity cycle check replaces recursive
  ancestry lookup; unresolved assemblies retain their existing skip behavior.
  Exact full-name matching, first declared metadata attribute, positional
  string arguments, and the shared attribute constructor's version parsing are
  retained. Named attribute properties are not applied during metadata discovery.
  Declared-only extraction no longer resolves a base type unnecessarily. Method
  targets use reflection directly with inherit=true, retaining inherited target
  behavior and public attribute identities. Discovery follows each eligible
  type's ancestry even when its assembly has no direct BasePatcher type reference,
  allowing concrete patchers to inherit from a shared external base class.
  The patcher assembly-reference filter, top-level-only type enumeration,
  declared identity requirement and per-type error boundaries remain unchanged.
  Discovery retains the accepted GUID, name and parsed version alongside each
  type name. Before construction, the resolved runtime type must be a concrete,
  closed BasePatcher with matching declared metadata from the actual compatibility
  attribute type. Runtime metadata inspection uses CustomAttributeData rather
  than executing candidate attribute constructors. A mismatch skips that type
  through the existing discovery failure path, without constructing or publishing
  it. This is an identity consistency check, not file attestation, an isolated
  load context or a guarantee against assembly/module initialization side effects.
  Discovery validates each target/signature together.
  Loading uses the live post-patch definitions, including initializer additions
  and `ref AssemblyDefinition` replacements, and only loads patched output.
  Finalizers run before Cecil cleanup, including when the execution pass fails.
  Definitions replaced during callbacks are retained for cleanup, not leaked.
  Dump filename retries are bounded and restricted to sharing/lock errors;
  dump failures are logged and fall back to in-memory loading. Assemblies loaded
  too early are reported and skipped instead of loading a second copy.
  Patched assemblies use the default load context so later name-based preload
  reuses them. The `interopServices.patching` state records applied/skipped
  counts, early loads, and discovery/lifecycle/patch/load/dump/cleanup failures;
  recoverable failures permit startup with an explicit completed-with-errors
  result. Patcher diagnostics are best-effort, including message formatting:
  a throwing listener cannot abort discovery, interrupt later finalizers or
  definition cleanup, or reclassify a completed load as a failed load. Actual
  operation failures retain their counters and existing handling. No retries,
  side-effect rollback or logging-framework changes are added. This port is
  compiled but awaits ordinary in-game acceptance.
  Removing the patcher log source can call other registered sources' equality
  or event accessors. A failure increments CleanupFailureCount and still permits
  log-source disposal, without replacing an earlier patcher error. It can leave
  logger registration or subscriptions partially changed; no retry or repair is
  attempted. Missing resolver-directory diagnostics are also best-effort, so a
  listener cannot prevent searching later roots or calling the compatibility
  resolver. Actual lookup and compatibility-callback failures are unchanged.
  The fallback BepInEx chainloader retains its compatibility preload path.
- InteropSettings owns the --unhollowed-path override lookup: skip argv[0],
  compare the exact case-sensitive token, take its first following argument,
  and ignore a final token without a value. No equals-form parsing, normalization
  or new value validation; config fallback, substitutions and path expansion
  remain unchanged. These metadata/argument changes await runtime acceptance.
- Preloader `AssemblySearch` owns recursive managed assembly probing for interop
  and patcher dependencies. The root precedes descendants, with DLL before EXE
  in each directory. Missing roots return no result and rejected images permit
  later candidates. Root probing is now lazy, so a valid root image does not
  require enumerating lower-priority directories first. Enumeration failures
  still propagate; no new identity matching, filesystem snapshot or retry policy
  is introduced.
  `AssemblyPatcherResolver` retains Cecil's default-directory/runtime lookup
  before the compatibility roots (core, plugins, patchers, managed, then public
  TypeLoader.SearchDirectories). A small BepInEx friend bridge (patch 0020)
  preserves TypeLoader.AssemblyResolve and its ordinary multicast semantics.
  The owned patcher has separate reader parameters and a phase-scoped resolver;
  it does not mutate or reuse TypeLoader's global reader/resolver objects.
  Root definitions and acquired dependencies share the engine's identity-based
  cleanup set, including when patchers place a dependency into their context.
  Definitions supplied by the compatibility event are borrowed, not acquired
  for disposal by resolution alone. The cache and owned definitions last through
  patcher finalizers and are released afterward. Upstream APIs and fallback
  resolution remain available. Cross-thread metadata use and retention beyond
  the patching phase are not supported; fresh startup remains unverified.
- `NativeDetourFactory`, `NativeDetour`, and `NativeDetourLibraries` own the
  typed IL2CPP detour provider. Default selection remains Dobby then Funchook;
  an explicit choice does not fall back. The existing config enum identity is
  retained through friend access so later compatibility binding cannot fail a
  ConfigEntry cast. Modules load from the packaged core directory or legacy
  lookup, bind the required Cdecl exports, and stay loaded for process lifetime.
  Prepared hooks root callbacks and trampoline delegates until native cleanup
  reports success. Initial trampoline-conversion failure cleans up its new
  preparation because the caller may never receive the handle. A failed later
  conversion leaves an already-prepared or applied hook and its earlier
  delegates intact; the owner must still dispose it when appropriate.
  Dobby preparations are destroyed even before Apply; failed
  unhooking preserves resources/state and blocks another owned hook at that
  target. Funchook error strings are borrowed pointers, not marshaler-owned
  strings. Owned operations and the BepInEx compatibility factory share a lock;
  this does not synchronize independent RedLoader/native hooks or establish
  callback quiescence.
  `interopServices.nativeDetours` reports selected/available providers, rooted
  and applied hooks, uncertain application, failures and retained resources.
  Provider-selection and failure logs are best-effort: listener or exception-
  formatting failures cannot skip owned cleanup or replace the hook error.
  Failure counters still advance, with a plain operation message when exception
  formatting fails. Native cleanup failures retain their existing resource-
  retention and aggregate-error behavior; this adds no retry or unhook guarantee.
  Bootstrap's runtime-invoke startup trigger now uses this provider through
  its BCL-only delegate boundary. Preparation returns the trampoline before
  activation, and Bootstrap publishes it before any native caller can enter.
  Matching callbacks wait for adapter initialization to finish; startup is
  claimed once even if core initialization fails. The gateway remains rooted
  and installed until process exit, forwarding later calls directly to the
  original without reflection or state writes. This avoids reclaiming storage
  still reachable by native callers; it is not a quiescence guarantee, and the
  persistent forwarding cost has not been measured. The preparation handle
  can be disposed only before activation is attempted. An activation error
  retains resources and blocks compatibility fallback; a preparation error
  permits fallback only when no owned hook remains at the target. Adapter state
  distinguishes trigger retirement from hook disposal and reports retention
  and blocked fallback without changing the compatibility hook-state schema.
  Source references: [Dobby 1.0.5 prepare/commit](https://raw.githubusercontent.com/BepInEx/Dobby/v1.0.5/source/InterceptRouting/Routing/FunctionInlineReplace/FunctionInlineReplaceExport.cc),
  [Dobby destroy](https://raw.githubusercontent.com/BepInEx/Dobby/v1.0.5/source/dobby.cpp),
  [Funchook API](https://raw.githubusercontent.com/kubo/funchook/master/include/funchook.h).
  Native calls, forced failures and game compatibility remain unverified.
- BepInEx's `INativeDetour` factories bind a typed factory in the owned preloader.
  The public interface, provider enum and configuration identity remain intact;
  the old BepInEx detour classes and native bindings are excluded from compilation.
  Default and explicit compatibility startup both use this route. The paired
  Modstoke preloader is required; this is not a standalone BepInEx distribution.
  `Undo` leaves the handle reusable, while `Free` and `Dispose` perform terminal
  cleanup. Dobby undo removes its registry entry, so reapplication prepares a new
  trampoline; callers must reacquire it after undo. Funchook keeps its prepared
  storage for reinstallation. Failed removal retains resources and uncertainty
  instead of reporting a clean unhook. Process-lifetime startup gateways cannot
  be undone or disposed after an activation attempt. The legacy `MethodBase`
  overload retains its supplied-signature behavior; it does not emit a method.
  These changes remove one embedded engine implementation, not the remaining
  IL2CPP startup, component, registry or logging dependencies.
- `HarmonyBackendInitializer` owns the early Harmony/MonoMod backend
  selection phase. It preserves BepInEx `[Preloader] HarmonyBackend` config
  compatibility, supports OpenACAI environment overrides, sets
  `MONOMOD_DMD_TYPE` for explicit non-auto backends, and verifies that
  `MonoMod.Core` exposes `Core110Runtime` before .NET 11 Harmony work is marked
  safe. The old reflected
  `BepInEx.Preloader.RuntimeFixes.HarmonyBackendFix.Initialize` call is no
  longer used in the OpenACAI preloader path.
- `PreloaderLogging` owns the preloader source, synchronized startup
  buffer and console listener. Typed services use that source instead of the
  BepInEx PreloaderLogger singleton; the category remains `Preloader`. The
  BCL-only Bootstrap boundary attaches it after console setup. Startup records
  retain the existing console-level filtering even when no console is shown.
  Once the direct trigger is armed, the same listener switches to owned disk
  delivery and replays the sealed buffer only to disk, avoiding console repeats.
  This supplies the disk listener previously missing from direct-core startup.
  Logging state is reported under `interopServices.logging`.
- `DiskLogListener` preserves log formatting, UTF-8 without BOM, the
  existing Logging.Disk config keys and compatibility source blacklist.
  `InstantFlushing=true` flushes every entry; false uses a two-second timer.
  Sharing/lock failures try bounded numbered LogOutput files; other open errors
  are reported without retrying unrelated paths. Write/flush failures are
  captured and close the listener rather than escaping through log callers or
  timer callbacks. Normal process exit and preloader abort dispose owned sinks.
  A compatibility-chainloader fallback transfers buffered events to its legacy
  replay list and detaches owned listeners before BepInEx initializes logging.
  The separate remove-preloader-listener phase is therefore gone; the logging
  handoff occurs when the adapter chooses its startup path. BepInEx retains
  logging source registration, collection/config identities and fallback sinks. Runtime output
  and forced-I/O-failure acceptance remain pending.
- `LogEventDispatcher` owns listener filtering and delivery once typed
  preloader logging is attached. BepInEx patch 0025 adds an optional one-time
  dispatcher without replacing existing collection objects or subscriptions.
  Registration order, duplicate listeners, publisher-thread delivery and the
  existing aggregate log-level filter remain. A failed listener or filter no
  longer interrupts later listeners on the owned path. Same-thread recursion
  skips only listeners already active in that call chain; other listeners can
  receive nested events. Failure counts and the last exception type appear in
  `interopServices.logging.dispatcher`, without logging another event or retaining
  its payload. These are snapshot diagnostics, not live acceptance evidence.
  Selected dispatch remains active if the owned startup later uses compatibility
  fallback. Explicit upstream startup without selection keeps BepInEx behavior.
  Direct subscribers to a source's event, source registration and collection
  mutation errors remain outside this delivery boundary. No public logger type,
  config key, file format or source identity is renamed.
- `RuntimeLogSources` attaches owned Trace and HarmonyX sources after
  the direct trigger is armed. It preserves severity mappings and the original
  Harmony log-channel enum/config identity, reuses existing compatibility
  sources, and detaches owned subscriptions at shutdown. Trace attachment and
  removal run outside the sink lock to avoid a lock inversion during logging.
- `IL2CPPLogSource` attaches at the scene-activation execution boundary,
  before wrapper preload and Core activation. This avoids using the interop
  API's domain-reading static initializer during early log setup. Its Cdecl
  callback copies native message text synchronously, prevents recursive
  forwarding and contains managed listener exceptions. The callback stays
  rooted for process lifetime; disposal stops forwarding without writing a
  null pointer to IL2CPP's single callback slot. Registration failure is recorded
  without preventing Core startup. The adapter's `runtimeLogging` snapshot
  includes source ownership, registration and callback errors. Native IL2CPP
  messages are distinct from Unity application logs. Runtime callback behavior
  remains unverified.
- `UnityLogSource` attaches during Core startup, after the scene trigger
  and wrapper preload, before hosting mods. It uses the generated
  `add_logMessageReceivedThreaded`/`remove_logMessageReceivedThreaded` methods
  with the same retained delegate, without assigning Unity's shared callback
  fields or clearing its global callback flag. The existing Unity source name,
  severity mapping and `Logging.UnityLogListening` setting remain compatible;
  `Logging.Disk.WriteUnityLog` continues to control disk output separately.
  Existing fallback capture is reused. Exception stack traces are retained.
  The callback contains listener failures and recursive logging, uses no Unity
  APIs and updates its count atomically. Unity documents that the
  [threaded event also receives main-thread messages](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-logMessageReceivedThreaded.html),
  so only that event is subscribed. The owned driver's quit handler removes
  this subscriber on the Unity thread; process-exit cleanup only stops managed
  forwarding. A failed removal leaves the retained callback inert. Loader state
  reports `unityLogging` ownership, count and setup/callback/detach errors.
  RedLoader's optional Debug prefixes are unchanged; the installed
  `redirect_unity_logs=false` setting prevents that second forwarding path.
  Actual delivery and shutdown ordering remain unverified.
- `ConsoleOutputRedirector` owns the early console output redirect
  phase. It creates the compatibility `Console` log source, installs an
  OpenACAI logging `TextWriter`, detects .NET's synchronized writer wrapper,
  and records that the old reflected
  `BepInEx.Preloader.RuntimeFixes.ConsoleSetOutFix.Apply` call was not used.
  On .NET 10/11 it deliberately skips a Harmony detour of `Console.SetOut`
  because early framework-method detours stay behind OpenACAI safety gates.
- `UnityInfoInitializer` owns the Unity metadata/path phase. It
  validates the executable and game-data paths, determines the Unity version
  from Unity manager files or safe fallbacks, and populates the existing
  `BepInEx.Unity.Common.UnityInfo` compatibility type by reflection without
  calling its internal `Initialize` method.
- `ConsoleManagerInitializer` owns the console manager compatibility
  phase. It selects the owned Windows driver (the Unix driver remains upstream), binds
  `BepInEx.ConsoleManager.Driver` by reflection, and records that the old
  reflected `BepInEx.ConsoleManager.Initialize` path was not used. An existing
  driver is still reused. `consoleManagerDriverOpenAcaiOwned` distinguishes
  driver ownership from initialization ownership; these fields describe the
  initialization phase, before the logging phase creates the console.
- `WindowsConsoleDriver` implements the shared IConsoleDriver contract
  in typed Preloader, without constructing configured preloader services early.
  WindowsConsoleSession owns CONOUT$ and only a console it allocated. Captured
  standard handles are borrowed; detach restores only slots still holding this
  session's handles, preserving later redirects such as the error log. Writer
  cleanup precedes handle cleanup; native restoration failure retains the
  session for cleanup before a subsequent creation attempt. There is no claim
  of quiescence for external native writers or thread-safe console replacement.
  Synchronized autoflush writers use an owned synchronous Win32 output stream,
  not the private .NET WindowsConsoleStream constructor. Redirected files/pipes
  continue partial writes; a closed receiving pipe ends output without a new
  failure. Asynchronous native output handles are not a supported contract.
  Existing Enabled/PreventClose/StandardOutType settings, title/color behavior,
  managed stdout wrapper and UTF-8-without-BOM output remain. As before, the
  normal managed-encoding mode does not change the console codepage; the
  alternative mode uses the requested codepage and now reports setter failure.
  Forced StandardOut with no valid original handle now suppresses only that
  writer, not the separate visible console. Forced ConsoleOut never closes the
  borrowed original. Reused consoles are not freed on detach. Windows runtime,
  encoding, redirection and visual acceptance remain pending.
  Handle ownership and synchronous writes follow Microsoft's
  [GetStdHandle](https://learn.microsoft.com/en-us/windows/console/getstdhandle),
  [console handles](https://learn.microsoft.com/en-us/windows/console/console-handles)
  and [WriteFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)
  contracts; the legacy private-stream reflection was confirmed in local source.
- `StdErrRedirector` owns the Windows native stderr phase, retaining
  the compatibility `BepInEx/ErrorLog.log` destination. It records file-open,
  redirect, and Win32 error results; a failed redirect closes its unused handle.
  The reflected BepInEx `RedirectStdErrFix.Apply` call is no longer used.
- `EnvironmentBannerWriter` owns the startup banner/platform-facts
  phase. It reads OpenACAI package manifests, sets the console title, logs the
  loader/version/commit/platform/process-bitness facts, and records that the
  old reflected `BepInEx.Preloader.Core.Logging.ChainloaderLogHelper` path was
  not used. This removes the banner phase's dependency on BepInEx's
  `MonoMod.Utils.PlatformHelper` usage.
- BepInEx IL2CPP runtime assemblies from the OpenACAI BepInEx fork.
- RedLoader/SonsSdk support assemblies for binary compatibility.
- Curated RedLoader/SonsSdk managed dependencies (`Tomlet`,
  `SemanticVersioning`, `Alt.Json`, and `GLTF`) are packaged with the
  compatibility assemblies because they support config, metadata, and SDK
  features OpenACAI will either own directly or expose through the new SDK.
- `Modstoke.Loader.dll` as the packaged assembly for the public
  Modstoke Loader runtime.
- HarmonyX/MonoMod patching is now treated as a probed OpenACAI patch-host
  capability instead of a permanently assumed BepInEx side effect.
- The package script stages the BepInEx Dobby win-x64 native detour backend
  into `BepInEx/core` and the `win-x64` native runtime folder so the
  transitional IL2CPP bridge can patch `il2cpp_runtime_invoke` before managed
  OpenACAI modules start.

This is acceptable as a staging point because it lets us test .NET 10,
hostfxr startup, interop generation, and the in-game UI while we replace
upstream-shaped pieces with OpenACAI-owned ones.

## Target Layers

1. Native Host
   - Own hostfxr/CoreCLR bootstrap diagnostics.
   - Validate runtime config, architecture, file hashes, and dotnet root before
     managed startup.
   - Keep Doorstop fork patches only until OpenACAI has its own native host
     entrypoint or a clearly maintained Doorstop-based host.

2. OpenACAI Core Runtime
   - Own config, logging, path layout, state file, file integrity, and update
     manifest behavior.
   - Own plugin discovery and deterministic dependency resolution.
   - Expose loader capabilities through OpenACAI APIs, not BepInEx internals.

3. OpenACAI IL2CPP Runtime
   - Own interop generation policy.
   - Generate into a temp directory, validate output, then atomically swap into
     the live interop folder.
   - Keep known-good interop assemblies when regeneration fails.
   - Track Unity/game metadata versions in loader state.

4. OpenACAI Patch Host
   - Probe native detour providers explicitly.
   - Gate Harmony and native detours by phase and safety.
   - Surface hook failures as capability state instead of crashing early.

5. Compatibility Shims
   - BepInEx shim: load BepInEx-style plugins and map key BepInEx APIs into
     OpenACAI services where possible.
   - RedLoader shim: load RedLoader-style manifests/mods, map RedLoader
     lifecycle callbacks, and translate SonsSdk expectations into Modstoke SDK
     services.
   - Shims should be isolated so failures in one compatibility mode do not take
     down OpenACAI Runtime.

6. Modstoke SDK
   - Own the preferred modding surface for new mods.
   - Use SonsSdk source as a feature map, not as a direct implementation base.
   - Prioritize stable lifecycle, in-world state, input/keybinds, save data,
     native config UI, inventory/items, multiplayer-safe messaging, assets,
     sound, and building helpers.

## Migration Rules

- Do not copy upstream code wholesale when a small adapter or clean
  reimplementation is possible.
- Keep upstream credit and license notices anywhere behavior or redistributed
  assemblies come from BepInEx, RedLoader, RedManager, SonsSdk, or Doorstop.
- Prefer OpenACAI-owned namespaces for new APIs.
- Keep compatibility behavior behind explicit shims with clear capability flags.
- Avoid broad static global state that cannot reset after returning to the main
  menu.
- Avoid per-frame scans unless they are bounded and rate-limited.
- Keep the private anti-cheat/admin mod out of public loader source and packages.
- Treat the admin/anti-cheat panel as a first-party consumer of public SDK
  providers, not as the reason the public Mods menu exists.

## Current Implementation State

Captured logs and state values below are historical evidence. Their original
product names and technical identifiers are retained verbatim.

- Owned RedLoader activation now keeps reporting outside the activation attempt.
  Failed and rejected candidates receive their load report before trace/warning
  delivery; each diagnostic sink is isolated. Accepted mod instances and their
  successful report are retained before success diagnostics. Type-load details
  ignore null loader exceptions and use the original message when none remain;
  throwing exception formatting falls back to the exception type. Candidate IDs
  are still claimed before activation, without retrying a failed ID later in the
  batch. This isolates owned-host diagnostics, not arbitrary mod/SDK side effects
  or logging inside the compatibility loader. No activation rollback is claimed.
- Startup package discovery isolates file-enumeration failures. The resolver's
  native-package directory walk now uses its existing recorded-failure path.
  RedLoader discovery guards each candidate and each manifest-directory scan;
  other candidates/roots continue after a failure. A manifest directly in the
  Mods/Libs root no longer makes package-root lookup climb to the drive root.
  Host state records `discoveryComplete` and `discoveryErrors`; incomplete scans
  make an enabled RedLoader shim Warning without undoing successfully hosted mods.
  The inventory scanner owns its diagnostics per invocation, includes matching
  completion/error fields, and does not alter the existing mod-issue counts.
  BepInEx startup scans root DLLs and immediate package subtrees independently.
  An enumeration error stops that subtree scan, retains files already discovered,
  and permits later packages. It does not promise discovery beyond an unreadable
  subtree. Lazy DLL/marker lookup, existing depth/disabled/existence rules and
  permissive manifest parsing remain; parse failures are recorded even when the
  existing fallback can produce a candidate. Missing optional roots remain empty.
  Dynamic BepInEx LoadPlugins still finishes its strict discovery pass before any
  activation; it does not use this tolerant startup helper. No filesystem snapshot,
  new symlink policy, retry, or runtime-failure acceptance is claimed.
- RedLoader activation now passes the discovered manifest path into a typed
  SonsSdk loading overload (RedLoader patch 0013). Previously the owned host
  discarded that path and the SDK looked only for `<DLL directory>/<DLL name>/manifest.json`,
  even when discovery selected a sibling or package-level manifest. Null retains
  the legacy lookup; an explicit selected path is not silently replaced by a
  different manifest. Existing parsing, platform/version checks and registration
  remain in the compatibility SDK. This fixes the source-level handoff mismatch;
  package-layout loading and third-party menu acceptance still need runtime evidence.
- Bootstrap now records early OS/process facts through [.NET RuntimeInformation](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.runtimeinformation)
  and registers Cecil platform assembly paths without reflected upstream helper
  calls. `openacai-bootstrap-state.json` includes `platform` and per-directory
  `cecilPlatformAssemblies` results. Registration preserves existing host entries
  and their order, appends deterministic unique paths, and records failures
  without replacing the existing list after a failed enumeration. Each directory
  is independent; missing directories are reported rather than treated as errors.
  This is Cecil's existing AppDomain data contract, not a change to CoreCLR's
  native binding policy or an assembly-execution step.
  The old PlatformUtils.SetPlatform call was unnecessary on this branch: packaged
  BepInEx's PlatformHelper compatibility setter is intentionally empty and its
  getter delegates detection to modern MonoMod. OpenACAI does not override that
  detection or populate PlatformUtils' private legacy banner data. The owned
  banner already obtains its own platform facts. Compatibility-bridge startup
  remains upstream-owned. Ordering stays platform observation, compatibility
  paths, Cecil registration, then construction of the configured typed services.
  Source/metadata inspection and compilation do not prove fresh game startup.
- `CompatibilityPathInitializer` now owns the normal Bootstrap path
  calculation instead of invoking BepInEx.Paths.SetExecutablePath. It publishes
  the same public compatibility type's private static properties after resolving
  all required setters. The executable-relative data-directory fallback, macOS
  four-parent layout, supplied managed/root paths, BepInEx.Core assembly filename,
  directory names and ordinal case-sensitive DLL search order are preserved.
  No paths are published when data-directory validation or setter binding fails;
  setter invocation itself is not transactional. The BepInEx version property,
  public SetExecutablePath API and compatibility-bridge mode are unchanged.
  Bootstrap remains BCL-only, with paths ready before configured preloader
  services are constructed. This moves initialization ownership; it does not
  establish a cause or fix for third-party menu failures.
- `Hosting/Runtime.cs` is the first OpenACAI-owned runtime module.
- It tracks compatibility shim status for `bepinex.plugins`, `redloader.mods`,
  `sons.sdk`, and `harmony.patches`.
- Shim states are exposed through `Modding.Runtime`, the
  `openacai.loader.compatibilityShims` service key, `LoaderCapabilities`,
  and `openacai-loader-state.json`.
- `Configuration/Paths.cs` now owns the Modstoke runtime layout:
  `Modstoke/Runtime`, `Modstoke/Runtime/Compatibility`, `Modstoke/Mods`, and
  `Modstoke/UserData`. A legacy `OpenACAI/Mods` or `OpenACAI/UserData` is still
  used when the matching `Modstoke/` folder does not exist; Bootstrap moves
  their contents across on first start. BepInEx plugin paths are bootstrap
  mirrors only until the native IL2CPP bootstrap replacement lands.
- `Modstoke.NativeHost` now owns the default Windows native
  host path. The Rust `winhttp.dll` preserves the proxy export surface, hooks
  IL2CPP startup, reads `doorstop_config.ini`, validates hostfxr/CoreCLR native
  architecture, sets Doorstop-compatible environment variables, and invokes the
  OpenACAI managed bootstrap through hostfxr. Its proxy export stubs are
  generated from `native_host_exports.def` by `build.rs`, which also owns the
  cdylib-only DEF linker arguments.
- `Modstoke.Bootstrap` owns the managed bootstrap boundary and
  default preloader orchestration. The Rust host enters
  `Doorstop.Entrypoint.Start()` in the OpenACAI bootstrap assembly, which writes
  `openacai-bootstrap-state.json`, `openacai-chainloader-state.json`, and
  `openacai-il2cpp-preloader-state.json`, sets OpenACAI runtime/path state, and
  runs OpenACAI-owned IL2CPP preloader phases. The chainloader handoff now goes
  through `IL2CPPChainloaderAdapter`, which writes
  `openacai-il2cpp-chainloader-state.json`. The adapter now includes
  `openacai.plan-chainloader-port`, a non-invasive readiness phase that checks
  GameAssembly/`il2cpp_runtime_invoke`, the native detour factory,
  `BasePlugin`, `ManualLogSource`, and the packaged
  `RuntimeEntryPoint.Load` metadata. It also includes
  `openacai.plan-direct-core-invocation`,
  `openacai.arm-direct-core-invocation-trigger`, and
  `openacai.execute-direct-core-runtime`. A fresh 2026-06-07 launch reported
  `openAcaiDirectCoreInvocationTriggerOwned=True`,
  `openAcaiDirectCoreInvocationExecuted=True`,
  `openAcaiIl2CppChainloaderPortComplete=True`,
  `compatibilityExecutionFallbackUsed=False`, and
  `bepInExIl2CppChainloaderInstanceUsed=False`. Those phases still use
  reflected BepInEx IL2CPP compatibility components until each internal
  component is ported into OpenACAI-owned code. `compatibility-bridge` remains
  a fallback mode while the remaining BepInEx preloader and IL2CPP compatibility
  internals are replaced.
- `HarmonyBackendInitializer` is the first completed preloader-internal
  port after the direct core trigger. Fresh launch state reported
  `openAcaiHarmonyBackendOwned=True`,
  `bepInExHarmonyBackendFixUsed=False`,
  `harmonyBackendSource=bepinex-config`, `harmonyBackend=auto`, and
  `monoModCore110RuntimeResolvable=True`.
- `ConsoleOutputRedirector` is the second completed preloader-internal
  port. Fresh launch state reported
  `openAcaiConsoleOutputRedirectOwned=True`,
  `bepInExConsoleSetOutFixUsed=False`,
  `consoleWriterInstallAttempted=True`,
  `consoleWriterInstalled=True`,
  `consoleWriterWrappedByRuntime=True`, and
  `consoleSetOutPatchSkipped=True`.
- `EnvironmentBannerWriter` is the third completed preloader-internal
  port. Fresh launch state reported
  `openAcaiEnvironmentBannerOwned=True`,
  `bepInExChainloaderLogHelperUsed=False`,
  `environmentBannerWritten=True`,
  `environmentBannerTitle=OpenACAI Endnight Loader v0.4.0-net11-preview - SonsOfTheForest`,
  `environmentBannerPlatform=Windows 11 64-bit`, and
  `environmentBannerProcessBitness=64-bit (x64)`.
- `UnityInfoInitializer` is the fourth completed preloader-internal
  port. Fresh launch state reported `openAcaiUnityInfoOwned=True`,
  `bepInExUnityInfoInitializeUsed=False`, `unityInfoPropertiesSet=True`,
  `unityInfoInitializedFieldSet=True`, `unityInfoVersion=2022.2.16f1`, and
  `unityInfoVersionSource=globalgamemanagers`.
- `ConsoleManagerInitializer` is the fifth completed
  preloader-internal port. Fresh launch state reported
  `openAcaiConsoleManagerOwned=True`,
  `bepInExConsoleManagerInitializeUsed=False`,
  `consoleManagerConsoleEnabled=True`,
  `consoleManagerDriverInitialized=True`,
  `consoleManagerDriverBound=True`, and
  `consoleManagerDriverType=BepInEx.WindowsConsoleDriver`.
- `Plugin.cs` now keeps the BepInEx-facing `Plugin` class as a thin adapter
  that delegates immediately to `RuntimeEntryPoint.Load`. New runtime
  behavior should move into OpenACAI-owned services instead of the adapter.
- `Discovery/AssemblyResolver.cs` now owns managed dependency resolution for
  Modstoke-native packages and legacy compatibility roots. It publishes resolver
  state with native package and legacy root metadata instead of leaving resolver
  policy embedded inside the bootstrap plugin.
- `Compatibility/RedLoader/EntrypointlessModPorts.cs` now owns known legacy mod compatibility
  ports that do not flow through the normal RedLoader host path. The FasterLogs
  IL2CPP registration/patch port is no longer embedded in the bootstrap
  adapter.
- `Compatibility/RedLoader/LegacyLifecycleBridge.cs` now owns RedLoader/SonsSdk lifecycle
  fan-out for compatibility mods: pre-mod/application lifecycle events,
  frame/GUI/application quit events, scene lifecycle events, post-Unity
  compatibility initialization, and legacy preference saves. Runtime driver and
  Unity lifecycle code now call OpenACAI methods instead of RedLoader event
  statics directly.
- `Compatibility/RedLoader/LegacyRedLoaderShim.cs` now owns legacy RedLoader/SonsSdk
  compatibility orchestration: log bridge setup, legacy config bootstrap,
  hosting gates, optional upstream diagnostic bootstrap, native Unity-support
  probe, shim-state publishing, package discovery, allow-list parsing,
  controlled mod loading, hosted-mod IL2CPP registration, candidate state, and
  load reports. It initializes RedLoader's legacy `LoaderEnvironment` state
  through OpenACAI-owned reflection mapping instead of calling RedLoader's
  `SetExecutablePath`, which can still depend on removed
  `MonoMod.Utils.PlatformHelper` APIs in stale compatibility builds.
- `Compatibility/RedLoader/RedLoaderModActivation.cs` owns the normal host's manifest validation,
  assembly loading, first-SonsMod entrypoint selection, construction, metadata
  assignment, asset-metadata preparation and registration orchestration. The
  selected manifest path is retained; null alone requests legacy path lookup.
  Existing manifest defaults, platform/loader-version rules, nonpublic constructor
  support, constructor-before-mod-version parsing and prerelease version fallback
  remain. Registration failure propagates as a candidate failure with its reason;
  accepted instances survive game-version/log/report diagnostic failures. No
  second activation attempt or arbitrary mod-side-effect rollback is introduced.
  SonsSdk patch0014 grants friend access to existing metadata identities. Manifest
  deserialization via Alt.Json and ModBase/SonsMod registration/registry/callback
  machinery remain dependencies;
  their execution is not claimed as owned. The ordinary host no longer calls
  SdkEntryPoint.TryLoadFromPath, while explicit upstream bootstrap and SDK
  post-Unity/shared UI entrypoints stay available. Runtime acceptance is open.
- Core also owns manifest file reads, loader-version/color interpretation and
  asset-attribute metadata discovery on that normal activation path. It invokes
  the existing Alt.Json deserializer directly against the same ManifestData type,
  preserving its property attributes, defaults and serializer settings; this is
  not a replacement JSON engine. Missing-file/null-result and parse-error reports
  remain distinct. Compatibility still parses the requested semantic version
  before applying the original <= RedloaderVersion range. Log colors retain
  six-digit hex conversion, optional leading #, default fallback for other lengths
  and exceptions for malformed six-digit values.
  Asset discovery scans all assembly types and their public properties in reflection
  order, materializes the same inherited attribute types, and preserves empty and
  repeated bundle entries. Exact property and parent-attribute links are prepared
  before publishing the completed list and assigning each bundle's mod owner.
  Discovery errors still reject activation before Register; no partial recovery,
  sorting, deduplication or earlier asset loading. Existing SDK reader/loader
  entrypoints and the explicit upstream host are unchanged.
- `Compatibility/RedLoader/RedLoaderAssets.cs` owns deferred attributed-bundle loading and Unity
  property mapping for bundles discovered by the normal owned host. Patch0016 adds
  a per-attribute typed callback; Core installs it before mod registration. The SDK
  startup contract retains bundle-list scheduling, progress yields and per-bundle isolation, while
  attributes without an owned callback retain their original loader. Handler errors
  never trigger a second load through the fallback.
  Core keeps the existing assembly-data path, native LoadFromFile operation and
  shared LoadedBundle field. Already-loaded bundles succeed without remapping.
  Reference order, null/empty name defaulting (including the public AssetName value),
  static setter/non-indexed Unity-object validation, HideAndDontSave and property
  assignment are preserved. Derived wrappers use cached public IntPtr constructor
  delegates; missing constructors report a mapping failure instead of generating
  invalid IL. Individual mapping failures continue, and successful bundle loading
  remains success even if mappings or diagnostic listeners fail. No unload, rollback
  of assigned properties, retry, new cross-thread guarantee or changes to the public
  MapBundleToFile memory-bundle path. Native/visual acceptance remains pending.
- `Compatibility/RedLoader/RedLoaderCatalogs.cs` owns per-mod startup catalog enumeration and async
  loading on the normal host path. Patch0017 adds a per-mod managed coroutine
  callback, installed before registration. Shared startup retains its registry
  snapshot, stage ordering, nested-routine advancement/disposal and completion
  signals. The unmodified SDK path uses its legacy catalog implementation without a callback;
  callback failure does not run a second loader.
  Owned enumeration preserves assembly-data paths, catalog*.json selection and
  file order, skips missing directories, and checks registration before each later
  catalog. Each load uses autoReleaseHandle=false and next-frame polling of the
  generated reference-class handle. Null/invalid handles, native start/observation
  errors and failed operation status are reported without stopping later catalogs.
  A valid handle is released in finally on completion or managed-routine disposal;
  cleanup errors are reported without a retry. Log-listener failures cannot prevent
  starting a load or performing cleanup. This is not native cancellation, locator
  unloading, a timeout policy, or proof that every catalog succeeded when SDK startup
  reports completion. Already-started work can outlive mod unregistration. Runtime
  async behavior and visual asset availability remain unverified.
- `Compatibility/SonsSdk/SdkInitialization.cs` owns shared SDK startup orchestration on the
  normal host path. Patch0018 lets MainInitializer forward its title/server entry,
  event-setup completion, failures and failure query to one typed Core driver.
  Selection occurs before InitAfterUnity installs SDK hooks; a conflicting or late
  selection fails instead of replacing active state. Explicit upstream mode keeps
  the original initializer. Driver errors never fall through to another initializer.
  Core owns the managed routine stack, disposal on unwind, client bundle/resource/
  options-scene/prefab/catalog/service ordering, progress yields and claimed SDK-ready
  subscriber dispatch. Catalog callbacks are honored, with owned catalog loading as
  the default for other registered mods within this driver. Dedicated startup keeps
  its existing service-only path. Completion still requires both event setup and
  routine/service sequencing. The driver owns terminal success/failure independently
  of the public completion event; invoking or disposing that event cannot skip
  owned startup or suppress its failure handling. The original one-shot event
  remains a compatibility notification without replay. Per-mod callback errors
  remain isolated. Service implementations, resource caches, UI objects and native
  operations remain their original SDK/game contracts.
  Blueprint initialization was initially async void and outside that completion
  handshake; Patch0053 below adds task observation. This adds no retries, rollback,
  cross-thread support or native coroutine-stop
  disposal guarantee. Actual client/server startup and visible UI remain unverified.
- `Runtime/Coroutines/ManagedEnumerator.cs` owns managed iterator advancement and yield
  conversion for RedLoader wrappers during normal Core startup. Patch0020 keeps
  the public Il2CppManagedEnumerator and its native registration/constructors as
  the compatibility boundary, binding an internal driver per managed instance.
  Core selects a single RedLoader factory before mods and support startup; explicit upstream
  mode does not select it. Conflicting or late selection fails, and a selected
  factory cannot silently return to legacy behavior after an error or null result.
  Selection itself does not initialize the native wrapper type. The public
  WrapToIl2Cpp, MonoBehaviour extension, enumerable and nested-iterator paths all
  retain their existing wrapper identity and reach the same driver factory.
  Current caches one successful conversion, including null, until MoveNext or
  Reset clears it before forwarding. Conversion precedence remains native iterator,
  managed child, native object, string and the existing primitive set. Primitive
  values use typed unmanaged locals with the same class-pointer store and native
  il2cpp_value_box call; the owned path no longer emits dynamic IL or caches boxer
  delegates. Unsupported structs/enums remain unsupported. Installed metadata
  confirms signatures and primitive class-store wiring, not live native behavior.
  Scheduling, token semantics and native object ownership are unchanged.
  No Stop/disposal, thread-safety or runtime acceptance
  guarantee is added.
- BepInEx patch0022 extends the same Core implementation to newly constructed
  BepInEx managed-enumerator wrappers. Each driver's managed-child factory retains
  its originating public wrapper type; shared conversion never substitutes a
  RedLoader wrapper for a BepInEx child or vice versa. The BepInEx selector is
  separate from native type registration and is installed at the start of guarded
  Core startup, before registry callbacks and service setup. A conflicting factory
  is rejected; selected-factory exceptions or null results never use the fallback.
  Earlier instances retain their original readonly driver or legacy implementation
  and cached Current state. This matters for external patchers and plugins on the
  fallback chainloader, which can precede Core even though no built-in startup
  caller constructs this adapter. Selection affects subsequent construction only;
  it does not adopt or reset live iterators. Standalone BepInEx keeps its default
  implementation when Core is absent. Native scheduling/registration, tokens,
  disposal and supported yield types remain unchanged. Runtime acceptance is open.
- The RedLoader shim treats the legacy RedLoader/SonsSdk `MODS` menu button as
  intentionally suppressed by default because OpenACAI owns the replacement
  Mods menu surface. That suppression is recorded under
  `redLoaderCompatibility.openAcaiHost.controls.legacyModsButton` and no longer
  marks `redloader.mods` or `sons.sdk` unhealthy.
- Rejected RedLoader registration now removes the exact event entries added by
  the base/Sons loader registration code and removes that instance from both
  registries. Existing subscriptions and later replacement entries are preserved.
  SonsSdk honors Register's false result, publishes an output instance only on
  success, and discovers asset metadata before registration. The owned host now
  reports activation exceptions/rejected registration as candidate failures.
  This is loader-owned registration cleanup, not a transaction over arbitrary
  mod initialization: direct subscriptions, Unity objects and already-applied
  Harmony patches are not undone. An executing callback cannot be recalled;
  tracked callbacks that have not started also honor the registration lifetime.
  The source changes are retained in RedLoader patch 0002; runtime failure-path
  acceptance is still pending.
- RedLoader FindMelon now returns the first active exact name/author match.
  Public Unregister snapshots the registered mods sharing the same Assembly
  identity, blocks reentrant group teardown/registration, and removes each mod's
  exact loader-added subscriptions and typed/base registry entries. Registered
  becomes false before deinitialization. Deinitialization, Harmony unpatching,
  unload logging and both notification events have separate failure boundaries.
  Registration lifetime checks suppress old tracked event snapshots even if an
  instance later registers again; public untracked subscriptions retain ordinary
  snapshot semantics. Initialization rechecks self-unregistration before further
  phases and acceptance, and ExecuteList snapshots its input so callback-driven
  removals cannot invalidate enumeration. SDK startup checks registration before
  beginning later catalog/bundle work; already-started loads are not canceled or
  unloaded. Mods remain responsible for their own subscriptions, commands and
  resources. This does not unload assemblies, stop executing callbacks, promise
  successful unpatching after an error, or add concurrent registry mutation support.
  Patch 0015 records the compatibility changes; runtime acceptance remains open.
- RedLoader event snapshots and mutation now share one lock. Method-based
  removal removes all matches, while delegate-based removal matches the exact
  method/target pair. Dispatch claims first-invocation subscriptions under that
  lock before calling mod code outside it. A one-time event is marked Disposed
  when delivery is claimed, preventing reentrant or concurrent delivery; this
  flag is not proof that every callback has finished. Existing startup consumers
  can initialize late registrations directly instead of adding callbacks to an
  already-claimed event. The SDK-ready coroutine uses the same claim/invoke path
  while retaining its per-subscriber progress yields. Failed logging listeners
  cannot stop the remaining callbacks. This is patch 0003, compiled but not yet
  exercised in game; ordinary snapshot callbacks already claimed are not canceled
  by subsequent removal or disposal. Loader-tracked entries additionally check
  their mod registration lifetime immediately before dispatch (patch 0015).
- SonsSdk asset startup now validates writable static, non-indexed Unity object
  properties before mapping and checks asset validity before setting hide flags.
  Attributed and embedded bundles share this mapper: one failed reference or
  throwing setter is logged without skipping later properties. Bundle logs count
  successful mappings; IsLoaded still describes the bundle handle, not complete
  mapping. A failed embedded bundle load throws a descriptive exception.
  MainInitializer isolates individual bundle failures and synchronous catalog
  enumeration/start failures, snapshots the catalog mod list and skips optional
  missing data folders. Diagnostic-listener failures cannot escape these error
  paths. No blanket retry or rollback of partially assigned properties was
  added. Patch 0004 follows 0003; patch 0005 extends async outcome reporting below.
  Runtime asset-failure and Axel menu acceptance remain pending; these source
  defects are not established as Axel's cause.
- Shared SonsSdk readiness is now observed rather than inferred from successful
  post-Unity subscription setup. The owned bridge publishes Pending, subscribes
  before setup, and consumes the selected driver's internal completion notification
  to publish Ready or Failed and request a state refresh. The public one-shot
  OnSdkInitializationCompleted event remains available to mods without replay;
  explicit upstream mode still observes that event with its existing semantics.
  Overall loader failure takes precedence over late SDK results. Ready requires shared initialization
  and late event setup, in either order for dedicated servers; it does not imply
  that every mod callback succeeded. Existing mod SDK events are unchanged.
  Synchronous setup, late setup, managed nested initialization and server-system
  failures publish the original exception; failure attempts to dismiss the loader
  overlay and never retries partial state. Only this SDK's managed routine tree
  is advanced/disposed by the observer; native coroutine cancellation/disposal is
  not newly guaranteed by the existing RedLoader bridge.
- Catalog operations now use a typed handle with auto-release disabled, poll its
  validity/completion across frame yields, report failed status and release valid
  handles in finally. Observation/release failures are contained per catalog.
  Installed IL2CPP wrapper metadata confirms the APIs; releasing catalog handles
  follows [Unity's catalog lifecycle guidance](https://docs.unity3d.com/Packages/com.unity.addressables@1.21/manual/LoadContentCatalogAsync.html).
  This releases the operation, not the loaded catalog. No new timeout policy or
  synchronous wait was introduced. These changes are in RedLoader patch 0005
  plus the owned lifecycle bridge/shim; runtime acceptance is still pending.
- Both compatibility managed-to-IL2CPP enumerators now retain the converted yield
  across repeated Current reads, including null, and release that cached reference
  when MoveNext or Reset is called. The prior implementation created a new native
  wrapper/string/box on every read. This follows the [IEnumerator contract](https://learn.microsoft.com/en-us/dotnet/api/system.collections.ienumerator?view=net-11.0-pp)
  without advancing the child, changing scheduling, swallowing conversion errors,
  or adding yield types. Public type identities remain separate for binary
  compatibility; RedLoader patch 0006 and BepInEx patch 0018 carry matching logic.
  All six mapped SDK/loader RunCoro callers discard their returned tokens. Early
  raw starts still return null when queued; cancellation, native disposal and
  per-enumerator thread safety are unchanged and have no new runtime proof.
- SonsSdk sound registration now checks FMOD results before storing a sound ID.
  File-based failures return null; the void memory overload logs and returns.
  Neither reserves the ID after a failed load. Memory loading supplies OPENMEMORY
  with the existing CREATESOUNDEXINFO length, following the local FMOD wrapper and
  [FMOD's memory-loading contract](https://www.fmod.com/docs/2.03/api/loading-and-playing-sounds-in-the-core-api.html).
  File paths are still passed to FMOD without a restrictive File.Exists check;
  successful handles, duplicate-ID policy and caller-visible signatures remain
  unchanged. Patch 0007 captures the repair; native audio acceptance is pending.
  Installed Axel 1.3.8 requests uierror.flac and uisound.wav from its mod data
  directory, but both are absent. Its menu is created later in OnGameStart after
  settings, subscriptions and WorldEdit setup. Missing audio is not established
  as the cause of the menu failure, and no mod files were replaced or synthesized.
- SonsSdk ModInputCache polls a cached registration snapshot, invalidated only by
  registration, so callbacks can add keybinds without invalidating the current
  update. New registrations join the next update. Per-key polling and individual
  performed/released callbacks have separate exception boundaries; diagnostics
  cannot interrupt remaining handlers. RemoveAllCallbacks clears both events.
  Callback snapshots preserve invocation order and ordinary in-flight removal
  semantics. Public signatures, bindings, scope predicates, input-action lifetime
  and release suppression outside the allowed scope are unchanged. No thread
  safety or replay guarantee is added. Patch 0008 contains this repair.
- SonsSdk GlobalInput now also polls a cached registration snapshot. Successful
  registration/removal invalidates the cache; callback mutations affect the next
  dispatch and do not cancel entries already captured. Each key's poll/callback
  failure is isolated, so later keys and the Use action can still run. Diagnostics
  are best-effort. Existing duplicate-key behavior, null-action acceptance, public
  APIs, SDK subscriptions and Use-action binding are unchanged. A registered
  multicast Action retains normal invocation semantics; isolation is per key,
  not per delegate inside that action. No thread safety, replay, new scopes or
  ownership transfer into InputBindings is claimed. RedLoader patch 0010 records
  this compatibility fix. In-game behavior remains unverified.
- Deferred attributed IL2CPP type registration detaches the pending assembly
  queue before draining it. Repeated/reentrant readiness calls do not replay
  native work; recursive registration of the same active assembly is ignored.
  Managed discovery failures are isolated per assembly and attribute/injection
  failures per type. ReflectionTypeLoadException retains its usable types even
  if the diagnostic sink throws. Attribute identity, non-inherited lookup and
  LogSuccess behavior remain; public registration after readiness still runs
  immediately. Explicit later calls retain the injector's duplicate behavior.
  No automatic retry, rollback, native recovery or thread-safety guarantee is
  added. A failed injection may already have changed native state.
  Core now uses typed friend access for SetReady, the original MelonHarmonyInit
  event and UnityMappers.RegisterMappers, in their existing order. Missing internal
  contracts no longer become silent reflection no-ops. RedLoader patch 0012
  records this boundary repair; ordinary game acceptance remains pending.
- The owned host uses the typed InitAfterUnity(bool) SDK overload to omit the
  legacy manager, its indicator, cards, buttons and handlers by default. The
  existing OPENACAI_ENABLE_REDLOADER_MOD_BUTTON opt-in enables it; the no-argument
  SDK entry point still enables it for standalone RedLoader. Shared SUI startup,
  mod callbacks, settings registration and command-line save loading remain.
  Patch0023 routes title/pause button registration through a directly selected
  Core policy on the normal path. Selection is separate from SUI static state and
  asset initialization. The policy preserves the exact ordinal-ignore-case MODS
  ID match and reads the existing opt-in on each registration; other IDs,
  generators, indexes and accepted-registration order are unchanged. Rejected
  registrations return before construction, scene queries or generator execution.
  Selection affects future registrations only; it does not remove existing UI.
  The same filter is idempotent, conflicting selection rejects and filter errors
  propagate without fallback. The normal installer omits only its SUI prefixes
  once the filter is selected; explicit upstream mode retains them using the same
  policy. Standalone SDK behavior remains unchanged without a filter. Harmony
  remains available for the hosted Axel guard. Runtime/visual acceptance is open.
  ModSettingsUi now owns settings creation and Escape/scene-close behavior
  independently of ModManagerUi. Both OpenACAI renderers offer MOD SETTINGS in a
  mod's config view when live settings are registered. This opens the SDK's custom
  controls and uses its save, callback, revert and restart-prompt behavior.
  Unsaved file edits block opening live settings; a normal close reloads the file
  and restores the selected editor mode. Owner restoration is deferred past the
  close handler, skipped on scene changes, replacement or restart prompts, and
  checked against the active-scene handle. Failed opens unparent/hide their UI;
  failed closes notify the owner without requesting restoration. Registry button
  backgrounds resolve during control construction rather than metadata lookup.
  RedLoader patch 0009 records the SDK portion. Visual acceptance is pending.
- Both owned renderers expose MOD ACTIONS for registered SonsMod instances with
  ModPanelAction attributes, including mods without a config file. Inventory and
  public snapshots expose ModActionsAvailable. Identity uses the exact mod ID and
  assembly path, not a display-name match. Callbacks are bound on opening and
  receive actual SBgButtonOptions controls in a temporary SUI panel; the legacy
  manager remains disabled. Malformed actions do not hide valid actions, and one
  callback failure does not disable other buttons. RedLoader patch 0011 exposes
  the existing binding API and isolates per-method failures without inventing
  parameterless or asynchronous callback adapters.
  Panel ownership checks the registered instance and active scene on updates and
  before invocation. Closing clears callback references and removes the SUI panel.
  Hidden/destroyed panels and invalid owners close without restoring stale UI.
  Normal Back/Escape/F6 return uses the shared live-controls flow: drafts must be
  saved/reverted, config is reloaded and the prior editor mode is restored on a
  later frame. Native editor bindings are cleared when rebuilding content.
  This bridge does not implement dynamic mod unloading or prove runtime behavior.
  Axel has no ModPanelAction attributes; its separate menu remains unverified.
- Hosted RedLoader runtime errors are tracked separately from the legacy menu
  suppression. `RLog.ErrorCallbackHandler` now increments
  `redLoaderCompatibility.runtimeIssues.errorCount`, stores the last error and
  timestamp, and queues a loader-state refresh. Arbitrary mod log errors do not
  establish SDK or host failure. Actual post-Unity SDK initialization failures
  mark the SDK shim failed. Refresh requests coalesce on the Unity driver and
  survive the two-second cooldown, with a final pending refresh on shutdown.
- Unity driver stages and scene relays have separate exception boundaries, so
  failures in an OpenACAI service do not skip unrelated mod updates, scene
  notifications or shutdown cleanup. Public Modding, Sons and save-lifecycle
  events invoke a captured subscriber list in order, isolating each subscriber
  and rate-limiting diagnostics even when a logging listener itself throws.
  RedLoader's existing event dispatch and public event signatures are unchanged.
  Patch0024 selects SdkLifecycle for the SDK scene, update and native
  LoadedMainScene handlers on the normal path. The original SDK subscriptions,
  priorities, native event registrations and public MelonEvent instances remain;
  the handlers forward rather than adding another publisher. Selection must
  precede event setup, including a partially failed setup. The same driver is
  idempotent; conflicting/late selection rejects and driver errors do not fall
  back. Standalone and explicit upstream paths keep their existing handlers.
  Core preserves title initialization then after-spawn reset then typed scene
  publication; game activation subscription then typed Game publication then the
  pause-menu wait. Updates poll mod input before LocalPlayer.IsInWorld, claim
  after-spawn before invoking it, and then publish in-world update. Native
  LoadedMainScene remains the automatic trigger for SDK game-start; item hooks flush
  afterward. Pause-menu attachment still precedes its availability event. No new
  world predicate, scene replay, activation deduplication, wait cancellation or
  native lifetime guarantee is introduced. Owned world-state observation remains
  separate from SDK game-start. Native event/visual acceptance is still open.
  Patch0025 extends that selected driver to register the five native SDK game
  events at the same setup point: pickup, LoadedMainScene, crafting, consumption,
  then armor. Global scene/update subscriptions still precede registration, and
  SDK gameplay/save patch setup still follows it. The normal path registers Core
  callbacks only; the SDK's original callbacks remain for standalone/upstream
  use. Pickup/consumption retain their typed TryCast and failed-cast skip;
  crafting retains integer Unbox and its exception behavior. Armor ignores the
  payload, and LoadedMainScene calls the existing owned game-start path with
  item-hook flushing afterward. Public events/subscriber order are unchanged.
  Registration errors propagate at the same point without fallback or new retry,
  cleanup, native callback-retention or thread-safety guarantees. No save behavior
  or native event-emission rule changes.
  Patch0026 gives that same driver ownership of the SDK gameplay patch setup.
  Core uses the original ConfiguredPatcher helper and sonssdk_harmony owner to
  install the six postfixes in order: world actor added/removed, pause opened/
  closed, world exit, then multiplayer cheat state. Arguments and public events
  are unchanged. The cheat-state cache is cleared by the typed Title subscriber,
  registered after those patches, and updates after notifying subscribers;
  reentrant queries retain the original behavior. Save hooks still install next
  in the SDK. Standalone/upstream mode retains the original gameplay patches.
  Partial installation errors propagate without fallback, rollback or a new
  retry policy. This does not establish native hook delivery, callback counts,
  multiplayer behavior or Axel menu compatibility.
  Patch0027 corrects SavingCallbackPatches.AfterSave from a prefix to a postfix
  and routes the four save/load callback bodies through the selected driver.
  Core retains the HasInstance guards, original diagnostics, selected save ID
  assignment before BeforeLoadSave, and existing event arguments. Public SDK
  patch methods and their registration path remain in place, including direct
  callers of SavingCallbackPatches.Patch; no second save patch set is added.
  Without a driver, the existing callback bodies remain, with the corrected
  after-save postfix in both modes. These are entrypoint hooks: the installed
  Save wrapper returns void and Load returns bool, but after-load still ignores
  that result. They do not establish serialization success or durable disk IO.
  The separate owned save lifecycle service and its existing legacy-relay policy
  are unchanged. No serializer, save format, sidecar routing or native completion
  callback registration is changed, and driver errors do not fall back.
  Patch0028 records the SDK's load and save hook installation attempts separately.
  The existing public prefix/postfix methods are applied as a pair for Load, then
  a pair for Save, under the same SDK Harmony owner. Setup no longer disables all
  legacy relays merely because SDK initialization was requested. Core relays each
  target's events only while its SDK patch has not been attempted. Applying or
  failed attempts suppress fallback because native state may already have changed;
  a successful registration return disables that target's fallback. An untouched
  Save target therefore remains eligible if Load setup fails. Status records both
  target outcomes; the existing aggregate relay Boolean means at least one target
  is eligible. Status refreshes during bootstrap, SDK completion/failure and owned
  save/load notifications, with each notification capturing its routing choice
  before invoking typed subscribers. This is first-party setup tracking, not a
  live native hook inventory. It does not cover external patch/unpatch operations
  bypassing the SDK entrypoint, arbitrary mutation during an invocation, native
  callback success or durable IO. No recovery patch pass or serializer change is
  introduced. Harmony registry entries alone are not used as installation proof.
  Patch0029 extends the same lifecycle owner to MiscPatches bootstrap setup.
  Ownership is now locked when either bootstrap or event setup begins, including
  failure, while repeated selection of the same driver remains harmless. On the
  normal path Core installs the launch prefix under sonssdk_harmony and preserves
  its title-loader delay assignment. Dedicated mode then calls the unchanged SDK
  player-limit memory patch before installing the server-creation prefix and
  server-name prefix. These still call MainInitializer.InitDedicatedServer and
  prepend the existing MODDED marker respectively. The original ConfiguredPatcher
  target resolution and prefix signatures remain. Standalone/upstream behavior
  keeps the SDK implementation, and errors do not fall back or add retries.
  Patch0062 below subsequently owns the player-limit scanner/NOP operation. Its
  instruction span and runtime safety are not proven by these migrations or
  their builds. No native memory operation was executed for verification, and
  dedicated-server/visual acceptance remains open.
  Patch0030 moves the SdkEntryPoint late-start, SDK-initialized, game-start and
  cheat-state callback bodies into the same Core lifecycle owner. SdkEntryPoint
  captures the driver before registering callbacks and retains the original
  instance delegates, registration order, game-start priority999 and per-instance
  legacy-manager setting. Core preserves the late-start failure check and report,
  shared settings UI followed by --savegame handling, command initialization,
  offline cheat enablement, console state, blur setup and boot.txt dispatch order.
  The singleton keeps the original process-wide cheat cache separate from the
  gameplay patch's cache; notification still precedes updating that state.
  Boot lines are read once per invocation and sent unchanged, including empty
  lines, with the original whole-operation error boundary. No replay, retry or
  command filtering is added. Entry-point setup also locks lifecycle selection;
  standalone/upstream mode retains the original callback bodies. This source
  ownership change does not establish native console/UI or mod-menu acceptance.
  Patch0031 routes GameCommands.RegisterFromType and SonsMod's protected direct
  command registration through the same lifecycle owner. Core retains the original
  reflection flags/order, DebugCommand attribute lookup, MonoMod fast invocation,
  Boolean return forwarding and true for other return values. The native console
  registration call and its per-command Instance lookup are unchanged. Automatic
  SonsMod registration now supplies the mod instance instead of a null target,
  fixing attributed instance command binding while preserving static commands.
  Public registration signatures and post-registration callback order remain.
  Registration locks owner selection before attempting commands, including a
  partial failure. There is no added native duplicate policy, retry, unregister,
  callback retention store or exception suppression. Native command execution
  and lifetime acceptance remain open.
  Patch0032 exposes a startup-overlay renderer through the existing lifecycle
  driver. Core now owns its native canvas, background, text and progress-bar
  construction plus visibility, offset and foreground updates. SDK shared fields
  and the ProgressBarContainer type remain in place, including its clamped
  AddProgress state and SetProgress presentation-only semantics. Construction
  preserves the original object names, persistence/hide flags, hierarchy, layer,
  sorting order, anchors, dimensions, colors and field-publication sequence.
  Startup callers still create on late start and hide on completion/failure;
  hiding does not destroy or clear the retained objects. No new replay, partial
  creation cleanup or duplicate-creation guard is added. Standalone/upstream
  mode keeps the SDK renderer, and selected-driver errors do not trigger it.
  Both startup implementations now add 0.3 divided by bundle count for each
  bundle and 0.4 divided by subscriber count for each initialized subscriber.
  Previously they added an increasing cumulative fraction for every item,
  causing nonempty stages to underfill or reach the clamped maximum early.
  Empty stages still perform no iteration or added progress, and each processed
  item retains its existing frame yield. Callback and completion order remain.
  Native visual acceptance is still pending.
  Patch0033 makes failed overlay construction clean up its own returned objects
  in Core and the standalone SDK path. Each new object is tracked before native
  configuration or parenting, including the text clone and progress-bar children.
  Shared references publish only after the hierarchy is complete. On failure,
  reverse-order cleanup attempts deactivation and native destruction separately
  for every tracked object; cleanup and diagnostic failures do not replace the
  original construction exception. Previously published objects are not included
  in that attempt's cleanup set. This does not promise immediate native deletion
  or recovery from native allocation failures that return no managed object.
  Successful construction, repeated successful creation and ordinary hiding keep
  their prior behavior. Native failure/visual acceptance remains unverified.
  Patch0034 routes panel-blur material lookup, radius updates and setup through
  the existing lifecycle owner. The public SDK methods and shared cache/pass
  fields remain the compatibility boundary; no second owner selector or failure
  fallback is added. Core retains the color-keyed material cache, shader name,
  first native custom-pass selection, camera parenting and radius of 25. Both
  Core and standalone setup now publish the pass only after configuration
  succeeds. Failed configuration independently attempts to deactivate and
  destroy its newly instantiated GameObject, without replacing the previous
  pass reference or masking the initialization diagnostic with cleanup errors.
  Native custom-pass ownership is not inferred from the managed wrappers, and
  no separate pass disposal, scene cleanup, retry or immediate-deletion guarantee
  is introduced. Native rendering and failure-path acceptance remain unverified.
  Patch0035 moves SUI prefab acquisition, copying and preparation into Core
  through the existing lifecycle owner. The SDK keeps its control factory APIs,
  shared prefab fields and native toggle/masked-image/tab constructors. Embedded
  bundle setup still precedes prefab work; sprite diagnostics, viewport creation,
  readiness publication and the same scene callback follow it. Selection locks
  before setup and failures do not invoke a second implementation. Native lookup
  paths, last-matching title button selection, copy order, persistence flags,
  component preparation and layout values are retained. Both implementations now
  delete scroll-template children from last to first; the previous forward loop
  rechecked a shrinking count and could leave game controls in new containers.
  No rollback, retry, prefab disposal or scene-lifetime policy is introduced.
  Native visual and third-party-menu acceptance remains pending.
  Patch0036 routes SUI panel registration and removal through the existing
  lifecycle owner. Duplicate IDs reject before allocation instead of silently
  orphaning the old panel. Public factories, the shared dictionary, input
  components and their configuration stay compatible. Inherited Remove() now
  releases registered-panel identity and input subscriptions before the same
  deferred Unity destruction call. Toggle-key release matches both key and
  callback identity, so a replacement binding is not removed. Config rebinding
  replaces only this panel's previous callbacks; removal does not disable shared
  input actions. Captured toggle callbacks verify current panel identity, and
  config callbacks verify that a binding and live root remain. This does not
  cancel arbitrary in-flight handlers, intercept direct native destruction,
  unbind visibility observers or change event-system deactivation timing.
  Native input and third-party-menu acceptance remain unverified.
  Patch0037 moves SUI menu-button registration and replay into the existing
  lifecycle owner. The SDK retains public APIs, registration records and scene
  callback identity. The legacy MODS policy still runs before factories or scene
  access and does not remove existing UI. Same-scene IDs now keep the first
  accepted registration, using case-sensitive equality; title and pause may
  share an ID. Replay snapshots tolerate registrations from factories, and
  pause insertion checks live children instead of an outdated name snapshot.
  Registrations made after pause-menu creation attach immediately to that menu;
  its cached reference clears on SDK scene transitions. A tentative record is
  published before factory execution to prevent recursive duplicates and removed
  if immediate creation fails. Previously created native objects are not rolled
  back. Factory exceptions during ordinary replay still propagate as before.
  Native parents, sibling ordering, activation-list insertion and OpenACAI's
  separate MODS bridge are unchanged. No unregister API, native visibility
  guarantee or in-game acceptance claim is introduced.
  Patch0038 moves SUI viewport construction and stored-event-system activation
  into the existing lifecycle owner. Canvas, raycaster and event-system references
  publish only after all components and persistence are configured. Both owned
  and standalone paths track the two returned objects, deactivate and destroy
  them independently on failure, and rethrow the original construction error.
  Previous shared references remain intact. Public getter contracts, successful
  object/component order, layout and initial activation state are preserved.
  The SDK's native EventSystemEnabler identity, callbacks and shared counter are
  unchanged. Key rebinding and the OpenACAI menu's separate event system retain
  their existing behavior; this is not global event-system arbitration. Cleanup
  is deferred Unity destruction, not rollback of unreturned native allocations.
  Native input, rendering and third-party-menu acceptance remains pending.
  Patch0039 isolates each SUI keybinding capture from subsequent widget changes.
  The capture retains its operation, cached keybind, original action-enabled
  state and event system. Reentrant SUI captures are ignored while one is active,
  and an existing game input-binding state also prevents a new capture. Config
  replacement, per-control revert and direct keybind removal cancel and dispose
  the old operation first. Callback identity checks reject stale completions.
  Startup/apply failures and completion use the same cleanup path; icon or config
  exceptions cannot skip input restoration. Cleanup failures are reported without
  preventing the remaining restoration steps or replacing the original error.
  The 0.55-second follow-up retains its original event system and checks capture
  generation, root activity and game input state before restoring anything.
  A previously disabled action is no longer unconditionally enabled. The existing
  five-second capture timeout, control filter, binding path and icon layout stay.
  Parent hiding/destruction still relies on the existing timeout; external config
  mutations, key serialization and global event-system arbitration are separate.
  Native rebinding/input/visual acceptance has not been established by compilation.
  Patch0040 preserves device identity when saving keybindings. SUI records the
  exact path accepted by the capture, saves it before refreshing the icon, and
  retains the existing short form for a simple Keyboard control. Other paths
  remain qualified, including nested controls. The no-argument SaveToConfig API
  reads the effective path of a single binding without requiring a connected
  device; for multiple bindings it maps the first resolved control through the
  Input System's binding lookup. Composite roots and empty paths are not saved.
  Cached ModKeybind instances subscribe once to config changes before ordinary
  listeners; refresh skips an already-matching action and preserves the existing
  public constructors and action identity. The subscription shares the cache's
  process lifetime, not a UI widget's lifetime. Bulk settings reset now uses the
  keybind control's reset path, including capture cancellation and icon refresh.
  There is no schema migration, composite serialization format or new per-widget
  listener. Standalone uncached ModKeybind instances retain explicit refresh via
  RevertToDefault. Native device round-trip and config-reload acceptance is pending.
  Patch0041 moves input icon database lookup, cloning, attachment and keybind
  replacement into the existing lifecycle owner. The SDK retains public APIs,
  its database cache, and first-resolved-control selection. Get still returns a
  caller-owned clone; keybind replacement now uses that clone directly instead
  of cloning it again and abandoning the first allocation. Missing icons leave
  the previous keybind icon intact. Replacement validates the layout template
  and both RectTransforms, completes parenting/layout before retiring previous
  icons, and cleans up its own candidate on failure. If the active icon is also
  the layout template, it is deactivated but retained for subsequent refreshes.
  Default text, clone naming, parent/sibling layout and attachment offset are
  preserved. Text/attachment failures now clean up newly allocated icons too.
  Standalone paths mirror these fixes; selected-owner failure never falls back.
  This does not establish native template aliasing, callback timing or visual
  acceptance, and it adds no native field reassignment, retry or observer.
  Patch0042 moves mod-keybind registration, cached polling and callback dispatch
  into the lifecycle owner's Keybinds service. The SDK keeps the shared registry,
  snapshot and public ModKeybind/InputAction identities; its adapters lock owner
  selection before use and never fall back after an owned call fails. Config
  subscriptions retain their process lifetime and priority, scope predicates
  retain their order, and registration during a callback joins the next poll.
  Performed/released callbacks still use separate invocation snapshots, with
  per-binding and per-callback failure isolation. A throwing registration log
  listener no longer prevents the newly cached action from being enabled, in
  either path. No action disposal, registration rollback, event replay, synthetic
  release or concurrency guarantee is added. The separate public InputBindings
  KeyCode registry is unchanged; compatibility actions are not converted to it.
  Native input and third-party menu acceptance remains pending.
  Patch0043 moves GlobalInput key registration/removal, initialization and key/Use
  polling into the lifecycle owner's GlobalInput service. The SDK retains the
  registration list, cached snapshot, Use action reference and mutable public
  OnUsePerformed event. Initialization subscribes the same SDK callback methods
  in the same order and at the same priority, preserving event deduplication.
  Those callbacks route to Core; no second update subscription is introduced.
  One registration per KeyCode, exact callback removal, snapshot mutation timing,
  key-before-Use ordering and isolated failures are unchanged. Use is refreshed
  from the native default input mapping at game activation as before. This adds
  no input consumption, scene reset, action disposal or concurrency guarantee.
  Standalone behavior remains available when no owner is selected; native input
  and third-party menu acceptance is still pending.
  Patch0044 moves settings registration and reflected close-callback binding
  into the lifecycle owner's Settings service. All four public CreateSettings
  overloads route through that owner. The SDK keeps the registry, public entry
  types, lookup APIs and type-specific widget generator. Static close callbacks
  retain their existing binding; instance callbacks now bind to settingsObject.
  Missing instance targets and invalid callback signatures fail before UI
  allocation or registry replacement in both owned and standalone paths.
  Patch0065 checks for the optional close callback before looking it up, avoiding
  a missing-method warning when it is absent. Existing overload selection,
  inherited/private method lookup and delegate binding are unchanged.
  Successful creation preserves entry-field injection, caller content parenting,
  duplicate-ID replacement and explicit-before-reflected close callback order.
  Later widget generation, field injection and parenting failures retain their
  existing behavior; this does not add general construction rollback or retire
  replaced entries. Native settings UI acceptance remains pending.
  Patch0045 moves type-specific settings widgets and reflected member discovery
  into Core. The SDK generator is an adapter to the existing Settings owner and
  retains a standalone implementation. Both paths now skip indexed properties
  and properties with no getter, which cannot be read by this argument-free
  generator. Ordinary getter failures still propagate. Field-before-property
  order, visibility flags, opt-in/out attributes, exact supported config types,
  null-entry skipping, control layout, headers, spacing and tooltips remain.
  Config/observable subscriptions retain their order and direct Value writes;
  the ToObservable helper is not substituted because its dirty-tracking behavior
  differs. Integer conversion, validation feedback, color behavior and persistent
  subscription lifetime are unchanged. This does not add failure rollback or
  dispose replaced UI; native presentation and binding acceptance remains open.
  Patch0046 corrects generated settings feedback in both implementations.
  Controls bind before config writers, so a nested validated-value correction
  cannot be overwritten by an older control notification. Writes and config
  notifications refresh from the current accepted Value, including validation
  that returns the existing value without firing an event. A refresh guard
  prevents native control feedback from writing back into config. Integer
  sliders use IntStep. Failed writes attempt to restore the accepted value
  without replacing the original exception. Public Observable behavior,
  config dirty tracking, keybind handling and subscription lifetime remain.
  Native control ranges and representations still apply; this does not add
  support for out-of-range slider values, unknown options or color alpha.
  Native settings and third-party menu acceptance remain pending.
  Patch0047 fixes temporary change-tracking suppression in RedLoader config
  loading. Load restores the prior flag in finally, so handled conversion errors
  and unexpected exceptions cannot leave later edits untracked. Restoring the
  prior value also preserves an outer load's suppression during reentrant calls.
  The same three conversion exceptions remain handled; other exceptions still
  propagate. Validation, change notifications, existing HasChanged values and
  the separate ToObservable helper retain their behavior.
  Patch0048 moves the settings-panel and entry lifecycle into the existing
  Core Settings service. ModSettingsUi and SettingsEntry methods select that
  owner before use, retaining their standalone paths without fallback after an
  owned failure. The SDK retains its registry, public entry/config/widget types,
  panel name, raycaster, shared active entry/callback/container/title state and
  category dividers. SDK subscriptions preserve callback assembly ownership,
  scene-before-update order and default priorities; no extra polling is added.
  Core handles layout construction, opening/replacement/closing, Escape polling,
  parenting, category visibility, delayed color refresh, save/revert and callbacks.
  Close clears active state before unparenting and saving. Category-save events
  still precede explicit and reflected close callbacks, then restart evaluation;
  the owner callback retains its return-to-owner meaning and runs in finally.
  Keybind revert, the 0.1-second color refresh, public lookup/character-limit
  helpers and existing dirty flags remain. This does not add save-success
  guarantees, transactional creation, replacement disposal or coroutine
  cancellation. Native layout, input and reentrant callback acceptance is open.
  Patch0049 adds Dialogs and Tooltips facets to the existing lifecycle driver.
  Core owns modal setup, show/open/close, option registration and completion,
  plus tooltip setup, display and raycasting. GenericModalDialog remains the
  SDK's injected MonoBehaviour with the same singleton, native button methods,
  public setters and return type; its text/reset and callback-storage helpers
  stay with that type. New managed helpers are hidden from IL2CPP injection.
  Dialog opening clears old callbacks. Option/cancel completion captures the
  selected delegate, clears and hides the old dialog, then invokes it. This
  prevents callback-driven replacement from being closed by the older action
  and leaves the old dialog closed if a callback throws. Additive registration
  within a dialog and multicast exception propagation remain. Standalone mode
  receives the same fixes. Tooltip state remains shared, with the SDK update
  delegate, native TooltipInfo/FollowMouseHandler types, layout and visibility
  behavior unchanged. No extra poll, setup rollback or lifecycle arbitration is
  introduced. Modal partial publication and tooltip early initialization/stale
  visibility remain separate source leads; native acceptance remains pending.
  Patch0050 completes tooltip publication only after its canvas, container and
  bound label are constructed, parented and hidden. Failures leave the previous
  shared references and initialized flag untouched; cleanup unbinds the candidate
  label and deactivates/destroys captured roots in reverse order, preserving the
  original exception. This is best-effort cleanup of objects returned by the
  existing factories, not a general native allocation transaction. Raycast
  activation and stop hide the previous tooltip; null/destroyed raycasters stop
  the existing update subscription. Hide/stop tolerate missing native roots.
  Shared observable identity, SDK update delegate/priority, layout, hover order
  and FollowMouseHandler remain. Show and valid raycast activation still require
  successful setup; no lazy setup, destroyed-canvas reconstruction or owner
  arbitration was added. Both paths match; native acceptance remains pending.
  Patch0051 stages the modal component locally until its native initialization
  finishes, then publishes the shared SDK singleton. Failures after cloning
  leave the previous singleton untouched and attempt both deactivation and
  destruction of the candidate root; cleanup and logging cannot mask the original
  failure. Both implementations retain the SDK component/button callback identity,
  option listeners, cancel listener replacement, SUI parenting and layout. Native
  destruction remains deferred and best-effort; this does not provide a general
  allocation transaction or add prerequisite initialization. Native acceptance
  remains pending.
  Patch0052 adds an owned blueprint import/registration facet. Directory loading,
  file-name recipe IDs and registration orchestration execute in Core; the exact
  SDK registration type, LoadedObjects dictionary, imported prefab and mutable
  OnPrefabLoaded event stay shared. Duplicate IDs fail before event publication.
  Existing directory enumeration, extension/name rules, material processing on
  clients, await behavior, imported object flags and messages are retained.
  Scene subscriptions, async-void startup, processed recipes, ingredient import
  plugin and downstream crafting-node construction are unchanged. This is distinct
  from the planning-only Building API; no runtime placement/book gate
  is enabled. No new import cleanup, retry, startup-await, rollback or thread-safety
  guarantee is added. Native blueprint acceptance remains pending.
  Patch0053 replaces async-void blueprint initialization with a returned Task.
  Client startup observes that task before progress and SDK-ready subscribers;
  fault/cancellation reaches the existing startup failure path through GetResult
  only after completion. Dedicated startup still runs synchronous services inside
  the bootstrap prefix, completes inline for an already-completed task, and uses
  the existing managed routine runner when imports are pending. Both service
  completion and event setup are required for successful one-shot completion.
  No worker thread, blocking wait, new completion event or retry was added.
  Standalone startup uses the same sequencing and stops its runner after failure.
  This does not pause the original server creation method while imports are
  pending, cancel an import when a routine stops, or change native object lifetime.
  Native client/server startup acceptance remains pending.
  Patch0054 fixes GLTF load-resource cleanup for scene and individual-asset entry
  points. Only the call that claimed the importer may clean up or reset its running
  flag; rejected overlapping calls retain their failure result without releasing
  the active load. Failed loads attempt cache cleanup and primary-stream/native-
  buffer release independently, preserving the original failure even if cleanup,
  the failure callback or diagnostics throw. Primary-stream release detaches the
  reference, tolerates no stream and still attempts native-buffer disposal after
  a stream-close error. Successful cleanup retains the first resource error and
  holds the running flag until cleanup finishes. Existing scene/native-asset
  ownership and aliasing inside asset caches remain; this is not comprehensive
  native rollback, deduplicated cache disposal or concurrent Dispose support.
  GLTF build references now accept ReferencePath, and direct game copying requires
  DeployToGame=true with an existing destination, matching the SDK build pattern.
  Native import acceptance remains pending.
  Patch0055 replaces LoadJson's raw loader/parser threads and synchronous polling
  with awaited Task.Run operations, following the importer's existing mesh/file
  threading pattern. Stream and root results publish after their worker succeeds;
  worker failures propagate through the load task into the existing cleanup path.
  The two private busy-wait helpers and thread-priority alias are removed. The
  public opt-in/editor/WebGL rules, sequential default path, loader fallback,
  load-before-parse ordering and null-root check remain. Threaded work now uses
  the task pool instead of dedicated highest-priority threads. Await retains the
  caller's available synchronization context; no Unity dispatcher, cancellation
  of an already-running parser or general thread-safety guarantee is added.
  Native threaded-import acceptance remains pending.
  Patch0056 extends the blueprint driver to game-scene activation, crafting-node
  construction, built-prefab preparation, ingredient lists and recipe creation/
  database registration. SDK public signatures, shared template fields, processed
  recipe cache and mutable events retain their identities. The scene subscription,
  native registration order, recipe/prefab IDs, callback-before-cache ordering and
  existing-recipe re-registration path are unchanged. Both owned and standalone
  template setup now publish only after the replacement recipe is assigned;
  failure attempts to discard just the candidate clone and its new recipe while
  preserving the original error and borrowed source recipe. Direct helper calls
  retain their existing initialization requirements. Mesh preprocessing, display
  names, interaction setup and book/localization helpers remain SDK-owned, and
  Building remains planning-only. This adds no reentrancy guard,
  crafting transaction, native registration rollback or thread-safety guarantee.
  Native blueprint construction and repeated-session acceptance remain pending.
  Patch0057 moves the remaining public blueprint helpers behind the same driver:
  freeform/mesh preparation, ingredient lookup, interaction UI, display names,
  recipe localization and book-page insertion. Public signatures and return
  types remain; each facade uses the existing first-use lifecycle selection.
  Renderer filters, active/inactive traversal, ingredient validation order and
  native component types are preserved. Interaction setup resolves child objects,
  its native UI list and locator before publishing fields, with the initialized
  marker last. Failed clones are deactivated/destroyed best-effort without masking
  the original error; native field writes are not transactional. Book pages still
  mutate the shared held prefab, localize top then bottom recipes, read the current
  SDK title key and append without deduplication. Localization uses the live SDK
  table; display-name parsing and current-culture case handling are unchanged.
  No eager book/interaction initialization, new scene hook, native transaction or
  planning-API placement support is added. Native visual acceptance is pending.
  Patch0058 adds a network-prefab driver for EntityManager initialization,
  registration overloads, unregister and clearing. The existing SDK entity list
  remains shared; Core installs its lookup postfix with the same Harmony owner
  at the original service initialization point. Standalone setup retains its
  callback. Both lookup paths now continue past an already-present native prefab
  instead of returning before later registrations; neither appends to the native
  Prefabs array. Entity-based duplicate checks, serializer-before-lookup-before-
  list ordering, native IDs and ordered lookup replacement are retained. Clear
  and unregister still affect only the managed registry, not native lookup entries
  or patches. No new scene hook, unpatch, ID validation, destroyed-object policy,
  synchronization or public planning-API networking support is added. Native
  registration and repeated lookup acceptance remain pending.
  Patch0059 adds an owned packet driver for SDK registration, packet construction,
  dispatch/relay and receive/delivery/chat prefixes. The event registry, captured
  reader delegates, OnChatReceived event, NetEvent, EventPacket and IPacketReader
  remain SDK identities. NetEvent hash caching and generic entity relay stay in
  the SDK. The same Harmony owner selects one implementation at the existing
  startup point. Header marker6969, seven/eleven-byte allocation overhead, target
  encoding, reader-before-relay order, full-buffer writer-based cloning, recipient
  selection and fallback cursor resets remain unchanged. Unknown event IDs still
  return without relay. No length/cursor-unit correction, new authority checks,
  exception isolation, wire version or planning-API transport is introduced.
  Chat dispatch now snapshots subscribers and stops at the first true result,
  matching its documented contract; all-false/no handlers still allow the original
  callback, and handler exceptions still propagate. Standalone chat dispatch gets
  the same fix. Native packet, relay and chat acceptance remain pending.
  Patch0060 adds an owned server-command driver for registration, execution, chat
  dispatch, result text, server-only chat sending and user-role checks. The public
  mutable Commands dictionary is read directly rather than captured or replaced;
  Command, CommandArgs and EExecutionResult remain SDK types. The SDK static
  constructor retains its sole chat subscription, including first-use timing.
  Command spelling/culture-sensitive lookup, space splitting, argument helpers,
  replacement semantics and action delegate identity remain. User commands still
  check Command.CanExecute before invocation; the permission helper retains its
  native role comparison. Sender identity comes from RaisedBy, and replies retain
  Sender/RaisedBy routing, result text and color formatting. Non-user bypass,
  exceptions and invalid-command results are unchanged. No new command, parsing
  scheme, authority policy, subscription, automatic cleanup or planning transport
  is added. NetUtils status properties and NetExtensions remain SDK helpers.
  Native command/permission/chat acceptance remains pending.
  Patch0061 moves GameManagers activation setup, instance creation and generic
  lookup behind the lifecycle driver. The shared SDK instance/type/root field,
  parameterless internal constructor and GetCharacterManager convenience method
  remain; an internal root-taking constructor lets Core own root acquisition.
  Initialization subscribes the same SDK Create delegate at the original service
  point. Activation captures the debug-console parent and replaces the instance
  only after construction, with no extra hook or lazy refresh. Lookups retain
  unavailable-instance/destroyed-root null results and now return null for a
  missing child instead of dereferencing it. The character dump caller reports
  an unavailable manager and returns before file output. Other dump modes and
  successful output remain unchanged. Missing-console creation still fails as
  before; no retry, scene-lifetime or concurrency guarantee is added. Native
  manager activation and lookup acceptance remain pending.
  Patch0062 moves the player-limit patch into PlayerLimitPatch. Core
  bootstrap calls it at the existing dedicated-only point, before the creation
  and server-name prefixes. MiscPatches forwards direct calls through the same
  lifecycle owner; its standalone fallback and the public Reflow utility remain.
  The generated Validate method-info field and its first native pointer retain
  their existing meaning. Checked current-process reads replace raw dereferences;
  scanning uses a 500-byte snapshot and keeps both the signature and nine-byte
  patch within that window. The original ?x?xxx signature, first-match policy,
  +3 offset and NOP bytes are unchanged. Unsupported platforms, missing pointers,
  unreadable memory and unsuitable patch spans fail before writing. The Windows
  x64 writer requires one committed executable region with uniform protection,
  checks VirtualProtect, flushes the instruction cache and restores protection
  in finally. Write/flush failure and restoration failure are both retained when
  they occur together. No rollback, repeat-call cache, retry, instruction decoder,
  configuration edit or concurrent patch safety guarantee is added. The inherited
  500-byte window is not a known method length, and the nine-byte instruction span
  and signature uniqueness still require native acceptance. API contracts follow
  Microsoft documentation for [ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory),
  [VirtualQuery](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualquery),
  [VirtualProtect](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualprotect)
  and [FlushInstructionCache](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-flushinstructioncache).
  Patch0019 separates the public GlobalBehaviour coroutine host from lifecycle
  ownership. When Core selects its compatibility driver, it first selects external
  lifecycle mode in RedLoader; the later support-module initializer calls Init
  directly and cannot create a second event publisher. GlobalBehaviour keeps its
  native identity, Instance field, persistence, sibling maintenance, non-quit
  recreation and optional script updates. It suppresses late-start, update,
  fixed/late/GUI and quit publication in that mode. Legacy SceneHandler callbacks
  also become inert, including any installed before native initialization failed.
  Core keeps preference saving during quit and publishes the existing one-shot
  definite-quit event when its quitting driver is destroyed. An ordinary driver
  destruction does not publish it; a resumed Update clears the quit marker.
  The compatibility host does not save a second time, sleep or force-kill the
  process. Native/standalone behavior remains unchanged without external selection.
  Source and prior startup logs show both objects could coexist, but duplicate
  dispatch counts and its possible effect on Axel's menu are not runtime-proven.
  Patch0021 routes the host's initialization, creation and sibling maintenance to
  CoroutineHost on the normal owned path. Selection happens before support
  initialization and does not register a native type early. Once the host has been
  used, its owner cannot change; repeating the same selection is harmless. The
  owned implementation retains GlobalBehaviour's registered type and public
  Instance, persistent DontSave GameObject, component creation/publication order
  and the same two native sibling calls. Non-quit recreation reuses the selected
  owner. Lifecycle guards, script updates, quit tracking and coroutine tokens are
  unchanged. Explicit upstream mode and standalone RedLoader retain the original
  implementation. Selected-owner errors propagate without fallback; this adds no
  rollback, native disposal, concurrency or scheduling guarantee.
  Coroutine draining checks support readiness before removing each queued item,
  leaves unattempted work queued, and bounds each drain to the starting count.
  Exceptions from an attempted start are logged without replaying an enumerator
  that may already have advanced. Owned SDK startup carries an optional failure
  observer with its managed iterator through Coroutines.Start and the readiness
  queue. Patch0022 makes Start/Stop direct dispatch facades for Coroutines
  on the normal path. Core selects this driver before patch installation; the
  installer then omits only its coroutine prefixes, retaining the SUI patches and
  Harmony instance used for hosted-mod guards. Explicit upstream mode keeps the
  prefixes and uses the same readiness helpers. Standalone RedLoader without a
  selected driver keeps its original behavior. Selection affects future calls;
  it neither replaces native tokens nor replays prior starts. The same driver is
  idempotent, conflicting selection rejects and selected-driver errors do not
  fall back. Queue order/bounds, null results for deferred starts, blocked stops
  without a host, wrapper identity and native StartCoroutine/StopCoroutine calls
  remain unchanged. Immediate and deferred start exceptions notify the observer once;
  handler failure does not interrupt later queued starts. The SDK publishes its
  existing failure event instead of remaining pending, and stops further managed
  advancement at its next iterator boundary if native scheduling retained it.
  Cleanup then uses the existing managed unwind. A start exception does not prove
  native ownership was released, so the queue neither disposes nor retries the
  iterator. A null token alone is not considered failure. Unobserved legacy callers
  keep their existing behavior. This does not guarantee native stop/disposal,
  detect a scheduler that never becomes ready, or establish in-game acceptance.
- The observed `AxelModMenu.ToggleMenu()` early-title-key failure has a guarded
  adapter tied to the hosted assembly and known method/field contract. It does
  not modify the mod DLL or its selected-panel state. The subsequent in-world
  failure was a SonsSdk call to removed MonoMod `GetFastDelegate`; SDK command
  and member-debugger calls now use `GetFastInvoker`. The normal game-start
  event was observed, so the speculative world-entry fallback was removed.
- RedLoader legacy core preferences now initialize through a non-fatal
  OpenACAI warning path. Missing or stale legacy config dependencies should not
  prevent the OpenACAI Unity driver, state file, or Mods menu bridge from
  starting.
- `Compatibility/CompatibilityDependencies.cs` now probes the curated
  RedLoader/SonsSdk compatibility dependency set before the legacy config
  bootstrap runs. It publishes `openacai.loader.compatibilityDependencies` and
  records packaged/runtime/mirror versions for `Tomlet`, `SemanticVersioning`,
  `Alt.Json`, and `GLTF` in loader state so missing SDK-support DLLs are visible
  before they become static-constructor crashes. The probe normalizes three-part
  assembly/file versions to four-part baselines and records already-loaded
  binding conflicts separately from OpenACAI package readiness.
- Compatibility dependency state is now a public SDK, server-policy, and
  attestation signal. `CompatibilityDependencies` exposes `Snapshot`,
  `Ready`, `Entries`, `Warnings`, and `Refresh()`; `Modding` exposes
  `CompatibilityDependencies`; the built-in server policy requires
  `compatibility-dependencies`; and runtime attestation digests sanitized
  dependency name/version/readiness/binding-conflict evidence without absolute
  paths. The snapshot also publishes top-level dependency, ready, packaged,
  warning, and binding-conflict counts so managers and server-policy consumers
  do not need to re-count the dependency array.
- `build.ps1` now aligns `SemanticVersioning.dll` in the
  transitional `BepInEx/core` bridge with the OpenACAI compatibility baseline.
  This lets older BepInEx assemblies bind successfully while RedLoader/SonsSdk
  compatibility sees the `3.0.0.0` assembly it expects, removing the previous
  `2.0.2.0` early-load binding conflict from runtime attestation.
- `Modstoke.Loader.csproj` resolves compile-time game/Unity
  wrapper references from `BepInEx\interop`, or an explicit `GameInteropRoot`.
  A missing wrapper set fails with an actionable build error. Unity base
  libraries and an incomplete cached SDK output are not wrapper substitutes.
- Required external builds in `build.ps1` fail immediately on
  nonzero exit codes, before package staging can copy stale build outputs.
  `-DotnetRoot` can explicitly select the already-installed game runtime instead
  of automatically picking a newer workstation runtime.
- Latest fresh-run validation reached `SonsMain` through
  `Single Player -> New Game -> Normal` and reported the pause menu bridge as
  attached in loader state, but the helicopter intro did not expose the pause
  menu before exit. Treat this as attach-state coverage only; the next visual
  pause-menu click validation needs an already in-world local save or explicit
  hosted-session permission.
- RedLoader hosting can now report `ready`, `warning`, `inventory-only`, or
  `failed` without making BepInEx or RedLoader the runtime identity. On
  .NET 10/11, even explicit `OPENACAI_ENABLE_REDLOADER_MODS=1` diagnostics now
  require `OPENACAI_REDLOADER_MOD_ALLOWLIST`; use ids/names/assembly names for
  targeted tests or `*` for an intentional load-all diagnostic run.
- RedLoader host discovery now scans both `Mods` and `Libs`. `Libs` candidates
  default to `Library`, are visible in host state, and remain non-hostable so
  OpenACAI can track legacy SDK/helper assemblies without trying to boot them as
  mods. The mod inventory also surfaces manifestless top-level `Libs` DLLs as
  RedLoader library shims. Inventory-only mode publishes candidate-level skipped
  load reports so manager/runtime state can explain why each candidate was not
  hosted. In the in-game menu these library shims live under their own
  `Libraries` tab instead of `All Mods` or `RedLoader Shim`. The native menu
  tab row is now count-anchored so adding the library tab does not push later
  built-ins, including `Credits`, offscreen.
- Modstoke-native manifests can now declare helper-library intent with
  `Library`/`OpenACAILibrary`-style types. These entries use
  `openacai-library`, appear in `Libraries`, and stay excluded from the
  loadable/user-facing mod views and SDK `GetMods()` result.
- Compatibility catalog sections now mean loadable legacy RedLoader/BepInEx
  mods/plugins only. Legacy `Libs` support assemblies remain visible through
  `Libraries` and runtime-library status, not through mod or compatibility
  views.
- `Diagnostics/LibraryInventoryStatus.cs` adds the built-in `Runtime Libraries`
  status provider (`openacai.menu.runtime.libraries`) so library inventory is a
  first-class runtime surface under `Ext`, with boundary, catalog-section, and
  per-library rows sourced from the public OpenACAI inventory snapshots.
- `Runtime/Mods/ModHost.cs` owns the first Modstoke-native mod host gate. It
  validates native package manifest/entrypoint intent, publishes
  `openacai.loader.nativeModHost`, writes `openAcaiNativeModHost` into loader
  state, contributes `capabilities.nativeModHost`, and deliberately keeps
  Modstoke-native libraries excluded from candidate hosting. It now also reads
  PE/metadata tables without loading assemblies so declared entrypoint types,
  optional public static methods, assembly references, and native execution-plan
  shape can be validated through OpenACAI resolver roots before runtime
  execution is enabled. Manifest lifecycle hooks now resolve to explicit
  per-hook methods or the static entry method default. The manifest reader now
  accepts string values, JSON arrays for hook lists, and JSON objects for
  hook-to-method maps, then validates those hook methods as public static
  zero-argument methods before the plan is hostable.
  Manifest-declared OpenACAI assembly paths are bounded to the package root
  before they can become host candidates.
- `Runtime/Mods/ModRuntime.cs` owns startup execution. It binds synchronous static callbacks, retains
  constructor instances, validates exact assembly path/module identity, rejects
  duplicate IDs/assemblies, and isolates package failures. The existing owned
  failure boundary guards exception formatting as well as logging: a throwing
  formatter falls back to the exception type and cannot abort later packages,
  frame callbacks or shutdown hooks. Failure flags and lifecycle order are
  unchanged; there is no retry or rollback of a mod's prior effects.
  Unity driver delivers start/frame/GUI/shutdown hooks. Native Unity-support mode
  remains deferred; diagnostic refreshes never run code. Actual outcomes live in
  `openAcaiNativeModRuntime` and the `openacai-native-runtime` policy row. Native
  package gameplay acceptance is still pending; see the public mod platform
  document for signatures, hook order, cleanup and restart semantics.
  `ModRuntimeSnapshot` also carries each mod's bound hooks and a total
  `HookCount`, which the Native Host status provider, the
  `openacai-native-runtime` policy row and runtime attestation read.
- `Runtime/Mods/LifecyclePlanners.cs` and `LifecyclePlanners.Snapshots.cs` hold
  the obsolete `Lifecycle*` planner classes and snapshot types. They no longer
  plan anything: one cached view per `ModRuntime.State` snapshot backs every
  getter, and nothing refreshes them on loader state writes. They publish no
  services, loader state blocks, capability blocks, status providers or policy
  rows.
- `Runtime/Interop/HarmonySupport.cs` owns the current HarmonyX/MonoMod dependency
  probe. It publishes `openacai.loader.harmony`, records the tested dependency
  baseline, gates Sons menu Harmony patches by IL2CPP hook readiness, and keeps
  forced/disabled states explicit through environment variables.
- `UI/Input.cs` owns the first OpenACAI input/keybind slice. It publishes
  `openacai.loader.input`, writes `openAcaiInput` into loader state, contributes
  `capabilities.input`, registers a built-in `Input Bindings` status provider
  under DIAGNOSTICS, and supports mod-owned keybind registration with explicit
  title/loading/game-scene/in-world/not-in-world gates plus optional suppression
  while Modstoke UI is visible. The existing loader Mods-menu toggle and back
  action remain compatible defaults.
- `Configuration/Config.cs` owns file-backed config validation, simple key/value
  parsing, friendly edit application, and saving for the current native mod
  config UI. It also owns the first structured config-provider slice through
  `IConfigProvider`, typed `ConfigValueSnapshot` rows,
  `TrySetRegisteredValue`, `openacai.loader.config` provider state, renderer
  apply plumbing, and an automatic menu adapter that gives registered
  providers a page in the Mods menu nav.
- `Configuration/RuntimePreferences.cs` owns the first Modstoke-native structured
  config provider. It persists loader UI preferences under
  `Modstoke/UserData/openacai-loader.settings.cfg`, applies the menu/back
  keybind enabled states through `InputBindings`, and provides a live in-game
  smoke-test surface for config-provider editing.
- `UI/Ui.cs` owns mods menu and mod config surface state. Rendering is
  still transitional, but visibility, active view, and tooltip state are now
  exposed through `openacai.loader.ui`.
- `UI/Menus/Menu.cs` owns the first public menu provider registry. It publishes
  `openacai.loader.menu`, includes built-in provider metadata for Mods/config/
  credits/protected-runtime surfaces, and allows future mods or private admin
  tooling to register menu providers. The filter chips in both renderers take
  their labels and tooltips from the built-in tab provider metadata. In the
  nav, built-in settings and command providers sit under LOADER, built-in
  status providers under DIAGNOSTICS, and external
  providers under their owning mod, PINNED, or MOD PAGES. Each provider opens
  a structured page through `IMenuContentProvider` without provider code
  referencing Unity UI.
  It also exposes `ICommandProvider`, command snapshots, safe command
  invocation, and command-provider counts in `openacai.loader.menu`. Public
  provider registration still cannot claim built-in ids; OpenACAI-owned runtime
  pages use an internal built-in hydration path when a metadata placeholder
  needs real content.
- `Hosting/RuntimeCommands.cs` owns the first built-in safe command provider.
  `Runtime Commands` appears under LOADER, renders in both native and
  fallback menu paths, and currently exposes runtime-state and inventory
  refresh actions without making renderers own direct action delegates.
- `Diagnostics/ProtectedRuntimeStatus.cs` owns the public protected-runtime status
  surface. It hydrates the built-in `Protected Runtime` provider with
  structured Server Policy, Runtime Attestation, Timing, File Integrity,
  Runtime, Compatibility, Dependency, Patch Host, and Runtime Path sections
  while keeping private admin/anti-cheat implementation details out of the
  public package. `Server Policy` renders first so protected server readiness is
  visible without scrolling past lower-level diagnostics.
- `Configuration/ServerPolicy.cs` owns the public protected-runtime/server policy
  registry. It publishes `openacai.loader.serverPolicy`, writes
  `openAcaiServerPolicy` into loader state, and includes the built-in OpenACAI
  runtime baseline policy for loader readiness, SDK service exposure,
  compatibility shim state, file integrity, runtime attestation, library
  separation, the dedicated runtime library surface, the OpenACAI native host
  gate, Sons
  environment/save-data/item/item-registration/inventory/networking/asset/save
  service status, private payload boundaries, and the explicit RedLoader
  hosting gate.
- `Discovery/FileIntegrity.cs` owns the public installed-file verification
  surface. It publishes `openacai.loader.fileIntegrity`, writes
  `openAcaiFileIntegrity` into loader state, contributes
  `capabilities.fileIntegrity`, verifies the primary and bootstrap mirror file
  manifests match, checks installed payload files by size and SHA-256, and feeds
  the protected-runtime File Integrity section plus the built-in server-policy
  `file-integrity` requirement.
- `Diagnostics/RuntimeAttestation.cs` owns the public disclosed runtime evidence
  surface. It publishes `openacai.loader.runtimeAttestation`, writes
  `openAcaiRuntimeAttestation` into loader state, contributes
  `capabilities.runtimeAttestation`, and builds an unsigned evidence digest from
  loader version, .NET runtime, manifest hashes, verified file counts,
  mod/library separation counts, hosted counts, and compatibility shim states
  without exposing absolute local paths or private admin/anti-cheat logic.
- `Diagnostics/Timings.cs` owns bounded timing diagnostics for the public bridge.
  It exposes `openacai.loader.timings` and writes `openAcaiTimings` into loader
  state so `.NET 10` and `.NET 11` test runs can be compared with measured
  startup/menu data. Timing state now includes process start metadata,
  process-to-OpenACAI-entry elapsed timing, aggregate metric/event counts, and
  per-event sequence/elapsed offsets so hostfxr, BepInEx entrypoint, menu
  attach, and UI transition timing can be compared without scraping logs. The
  state is exposed through a typed `TimingStateSnapshot` and rendered
  in the public `Protected Runtime -> Timing` section.
- `Sons/Runtime/Sons.cs` now owns the first Sons SDK runtime state and environment
  slices. It exposes `openacai.sons.state`, writes `openAcaiSons` into loader
  state with scene/world/pause state plus lifecycle/scene/world/pause event
  counters, and exposes `openacai.sons.environment` with game root, executable,
  Unity data, persistent/save data, Modstoke-native folders, legacy
  compatibility folders, runtime versions, and dedicated-server detection. This
  lets new mods ask OpenACAI for lifecycle state and paths instead of depending
  on RedLoader globals or `LoaderEnvironment`. It registers the built-in
  `Sons Runtime State` and `Sons Environment` status providers under
  DIAGNOSTICS and contributes the optional `sons-runtime-state` and
  `sons-environment` protected-runtime policy rows.
- `InventoryEvents` registers directly with the native core event
  registry during OpenACAI initialization. Pickup, crafting, consumption and
  armor notifications reach new mods as immutable managed snapshots without
  requiring legacy SDK setup. Native callback roots and partial registration
  state are owned by OpenACAI. Legacy registrations stay at their existing setup
  point to preserve their relative position in the native callback list; this
  independent stream does not invoke them. Notifications are local observations,
  not remote inventory authority or cancellable requests. Gameplay acceptance
  remains open.
- Sons lifecycle, scene, world and pause publications request a coalesced manager
  snapshot refresh after updating the service and notifying subscribers. The
  existing Unity-driver pump collects state after callbacks, retains requests
  through its two-second cooldown and flushes the quit publication at shutdown.
  This closes the gap where the live service changed but the disk snapshot stayed
  at an earlier scene transition. Generic SetService and RefreshEnvironment do
  not enqueue writes, avoiding a writer-driven refresh cycle. Pause observation
  compares both menu existence and visibility, so a closed menu appearing or
  disappearing is published. The LocalPlayer-based world predicate is unchanged;
  publication after these changes still needs ordinary game acceptance.
- `Sons/Saves/SaveData.cs` owns the first save-data isolation slice. It
  exposes `openacai.sons.saveData`, writes `openAcaiSonsSaveData` into loader
  state, creates the OpenACAI save-data roots, and gives mods safe per-mod
  directories under `Modstoke/UserData/Saves/Mods/{normalized-mod-id}` while
  still reporting the real Endnight save directory separately. It registers the
  built-in `Sons Save Data` status provider under DIAGNOSTICS and contributes
  the optional `sons-save-data` protected-runtime policy row.
- `Sons/Saves/SaveLifecycle.cs` owns the first save/load lifecycle slice. It
  exposes `openacai.sons.saveLifecycle`, writes `openAcaiSonsSaveLifecycle`
  into loader state, patches `SaveGameManager.Load`/`SaveGameManager.Save`
  through the OpenACAI Harmony safety gate, and relays to legacy
  `SonsSdk.SdkEvents` only when RedLoader/SonsSdk did not install its own save
  hooks. It also registers the built-in `Sons Save Lifecycle` status provider
  under DIAGNOSTICS and contributes the optional `sons-save-lifecycle`
  protected-runtime policy row.
- `Sons/Saves/SaveRegistry.cs` owns the first custom-save serializer slice. It
  exposes `openacai.sons.saveRegistry`, writes `openAcaiSonsSaveRegistry` into
  loader state, lets mods register `ISaveable` or
  `RegisterJson<T>` sidecar save handlers, stores data under each mod's
  OpenACAI save-data directory when `SaveLifecycle` observes a Sons
  save/load event, and now exposes serializer storage intent through
  `SaveStorageMode`. `RegisterEmbeddedJson<T>` lets mods declare
  embedded-preferred or embedded-required save data without forcing an unsafe
  write into Sons save files before the game save embed hook is verified.
  Embedded-preferred saveables fall back to sidecar JSON; embedded-required
  saveables are skipped and reported while embedded storage is not installed.
  It registers the built-in `Sons Save Registry` status provider under
  DIAGNOSTICS and contributes optional `sons-save-registry` and
  `sons-save-serializer-routing` protected-runtime policy rows.
- `Sons/Runtime/Assets.cs` owns the asset/audio SDK slice. It exposes
  `openacai.sons.assets`, writes `openAcaiSonsAssets` into loader state,
  contributes `capabilities.sonsAssets`, and registers a built-in
  `Sons Assets` status provider under DIAGNOSTICS. The service creates safe
  per-mod roots under `Modstoke/UserData/Assets/Mods` and
  `Modstoke/UserData/Audio/Mods`, exposes rooted file helpers, publishes
  discovered file counts, and routes Unity AssetBundle load/unload through
  OpenACAI-owned helper methods with loaded-bundle snapshots and counters. It
  also routes non-blocking Unity AudioClip load/unload through
  `UnityWebRequestMultimedia.GetAudioClip`, pumped by the OpenACAI runtime
  driver, with pending/loaded clip snapshots and counters. Audio banks remain
  discovery-only until a verified bank/event path is added. The
  protected-runtime policy reports `sons-asset-paths`, `sons-asset-loading`,
  and `sons-audio-loading`.
- `Sons/Inventory/Items.cs` owns the first Sons item metadata slice. It exposes
  `openacai.sons.items`, writes `openAcaiSonsItems` into loader state,
  contributes `capabilities.sonsItems`, and registers a built-in `Sons Items`
  status provider under DIAGNOSTICS. The service is read-only: it snapshots
  Sons item ids, names, UI metadata, caps, spawn/hotkey/quick-select flags,
  prefab presence, and tags without registering or mutating items. It now also
  exposes OpenACAI-owned item lookup/search/filter helpers over that snapshot
  so mods can query item metadata without referencing RedLoader/SonsSdk item
  helper internals. Typed Sons.Item reads replace reflection/member-name guesses.
  Capture and uncached ID lookup require the initialized Unity lifecycle thread;
  rejected refreshes do not replace the shared catalog. Cached queries remain
  managed-only. Source order and the null-source initialization attempt remain,
  but failed native getters no longer become default-valued item fields. Native
  IL2CPP enumeration/disposal reads tags that the former managed IEnumerable
  fallback could not traverse; item/tag bounds and stored UI text are preserved.
  The game initializer/getters and resulting metadata still require runtime
  acceptance; generated wrappers do not prove native side effects or purity.
- `Sons/Inventory/ItemRegistration.cs` owns the first safe item-registration
  planning slice. It exposes `openacai.sons.itemRegistration`, writes
  `openAcaiSonsItemRegistration` into loader state, contributes
  `capabilities.sonsItemRegistration`, and registers a built-in `Sons Item
  Registration` status provider under DIAGNOSTICS. The service validates
  mod-owned custom item definitions, checks item-id collisions against the Sons
  item catalog, validates optional base-item clone intent, records accepted
  definitions in an OpenACAI-owned in-memory registry, and explicitly reports
  that runtime item database mutation is disabled until the item creation and
  prefab path is verified. The protected-runtime policy reports
  `sons-item-registration-gate`.
- `Sons/Inventory/Inventory.cs` owns the first Sons inventory readiness slice. It
  exposes `openacai.sons.inventory`, writes `openAcaiSonsInventory` into loader
  state, contributes `capabilities.sonsInventory`, and registers a built-in
  `Sons Inventory` status provider under DIAGNOSTICS. The service is
  read-only and reports LocalPlayer/world/inventory availability plus
  item-catalog backing status. Its live read helpers use typed game bindings for
  AmountOf, Owns and TryGetEquipped, preserving separate count/ownership/equipped
  semantics. Each query checks the initialized lifecycle thread and current
  in-world inventory, returning explicit failure separately from zero/false.
  No retained native objects, private storage traversal, per-query publication
  or catalog-membership assumption. Native behavior is not proven by binding
  metadata; in-world acceptance remains pending. It also exposes owned inventory
  action planning helpers for add/remove/equip/spawn/craft-result/register/
  model-replacement intent. These helpers validate item/action requests,
  publish plan counters and the last reason/status, and explicitly report that
  runtime inventory mutation is disabled until the in-world mutation path is
  verified. The protected-runtime policy reports
  `sons-inventory-action-gate`.
- `Sons/Crafting/Crafting.cs` owns the first Sons crafting planning slice. It
  exposes `openacai.sons.crafting`, writes `openAcaiSonsCrafting` into loader
  state, contributes `capabilities.sonsCrafting`, and registers a built-in
  `Sons Crafting` status provider under DIAGNOSTICS. The service validates
  craft-result, recipe, and ingredient-check plans against the OpenACAI item
  catalog and inventory gate, publishes plan counters and last reason/status,
  and explicitly reports that runtime crafting execution is disabled until the
  in-world execution path is verified. The protected-runtime policy reports
  `sons-crafting-plan-gate`.
- `Sons/Crafting/CraftingRecipes.cs` adds explicit result-item queries over the
  current game recipe database. It returns every matching declared recipe,
  not just the first result slot, and preserves ordered/duplicate result IDs,
  ingredient counts and reusable flags in managed read-only snapshots. Reads
  require the initialized Unity lifecycle thread and current in-world player;
  no native references are cached, no partial result is published on failure,
  and no automatic scan is installed. Native recipe identities, generated
  results, craftability and consumption are not inferred from this metadata.
  Initialization, result generation and execution are not invoked. Native
  getters and actual snapshots remain unverified in game.
- `Sons/Building/Building.cs` owns the first Sons building/blueprint planning
  slice. It exposes `openacai.sons.building`, writes `openAcaiSonsBuilding`
  into loader state, contributes `capabilities.sonsBuilding`, and registers a
  built-in `Sons Building` status provider under DIAGNOSTICS. The service
  publishes OpenACAI-owned blueprint roots under
  `Modstoke/UserData/Blueprints/Mods`, keeps legacy `Mods/Blueprints`
  discovery visible for compatibility, lets mods register blueprint
  definitions, validates blueprint item/custom/file placement plans, and
  explicitly reports that runtime placement and blueprint book mutation are
  disabled until the in-world construction path is verified. The
  protected-runtime policy reports `sons-building-plan-gate`.
- `Sons/Networking/Networking.cs` owns the first Sons networking planning slice. It
  exposes `openacai.sons.networking`, writes `openAcaiSonsNetworking` into
  loader state, contributes `capabilities.sonsNetworking`, and registers a
  built-in `Sons Networking` status provider under DIAGNOSTICS. The service
  probes Bolt/session state through reflection, lets mods register
  OpenACAI-owned message channels, validates target/payload/server-authority
  message plans, and explicitly reports that packet/chat transport is disabled
  until the Bolt packet path is verified. The protected-runtime policy reports
  `sons-networking-plan-gate`.
- `Sons/UI/MenuBridgeState.cs` owns the first OpenACAI state slice for
  title/pause Mods button injection. It exposes `openacai.sons.menuBridge`,
  records attach attempts and failures, and lets state refreshes capture
  whether the bridge attached or found an existing button.
- `Sons/UI/MenuBridge.cs` owns the title/pause Mods button injection logic
  and pause-menu Harmony callbacks. `Plugin.cs` now calls into this bridge
  instead of carrying the menu injection implementation inline.
- `Hosting/RuntimeDriver.cs` owns the Unity tick driver registered with IL2CPP.
  It replaced the old RedLoader/BepInEx-shaped driver name while preserving
  lifecycle dispatch behavior through OpenACAI-owned lifecycle services.
- `Runtime/Unity/UnityLifecycle.cs` owns Unity scene hook installation, deferred scene
  initialization dispatch, and throttled lifecycle exception logging. The
  BepInEx-facing adapter now asks this service to install scene hooks instead
  of carrying scene queue state itself. Scene events feed both the public
  Modstoke Loader event surface and the Sons runtime state provider.
- `Hosting/LoaderCapabilities.cs` now owns the public capability/path snapshot
  consumed by the loader state JSON, reflection callers, and companion tooling,
  including the public file-integrity and runtime-attestation snapshots.
- `Diagnostics/LoaderState.cs` now owns `openacai-loader-state.json`, the legacy
  bootstrap state mirror, mod inventory publishing, hosted-mod state snapshots,
  the compact `openAcaiModInventoryCatalog` state block, the
  `openAcaiFileIntegrity` and `openAcaiRuntimeAttestation` state blocks, and
  throttled refresh requests from UI/menu events.
- `UI/Menus/ModMenuDispatcher.cs` owns native/fallback mods menu renderer
  selection for show/hide, falls back around native renderer dispatch failures,
  and records menu open/close timing/state refreshes.
- `UI/Menus/ModMenuState.cs` owns shared mods menu inventory, chip filtering,
  the open page and its history, session view state, metadata, selected config
  document, edit mode, config text, status, friendly settings, provider detail
  state, structured provider value application, save, and revert state so
  renderers no longer own core list filtering or config editing behavior.
  `UI/Menus/MenuViewBuilder.cs` builds the nav and page view model from it
  (`UI/Menus/MenuView.cs`), and `UI/Menus/MenuText.cs` holds the text both
  renderers share.
- `Discovery/ModSources.cs` owns the public source-family model for
  Modstoke-native packages, legacy RedLoader compatibility packages, legacy
  BepInEx compatibility plugins, and unknown entries.
- `Discovery/ModInventory.cs` now owns the inventory DTOs, native OpenACAI
  package scan, legacy RedLoader/BepInEx compatibility scans, config discovery,
  status classification, and Vortex deployment metadata detection.
- `BepInExPluginHost` is the current execution path for eligible IL2CPP
  BasePlugin candidates. RuntimePluginLoadingEnabled is always true; the host loads
  assemblies with Assembly.LoadFrom into the default load context, constructs
  plugins, invokes Load, retains successful instances and
  records per-plugin reports. Its diagnostic callbacks are best-effort: a trace,
  log listener or warning callback cannot stop the next load or turn a completed
  Load into a failure report. No plugin retry or side-effect rollback was added.
  Selection uses owned Cecil metadata extraction before loading plugin assemblies.
  The first declared plugin identity and inherited process, dependency and
  incompatibility attributes retain the compatibility full-name matching rules.
  Attribute constructors from candidate assemblies are not executed; the shared
  BepInEx data types still provide version and dependency-range parsing. Named
  attribute properties are not applied. Ancestry is traversed iteratively with
  a reference-identity cycle check. It picks the highest
  process-compatible version per exact GUID, removes declaring plugins with
  present incompatibilities, and topologically orders all hard and soft edges.
  Dependency traversal is owned and iterative, preserving ordinal root order,
  declared edge order and dependency-before-dependent results. A cycle rejects
  the selected batch before activation. Hard dependency versions
  are checked against successful loads (plus the running Core); missing, failed
  or rejected prerequisites block dependents transitively. Optional dependencies
  do not prevent loading when absent or failed. No lower-version retry occurs.
  Runtime identity is checked against selected metadata before construction.
  An existing upstream fallback IL2CPP chainloader retains execution ownership;
  the owned registry is distinguished from that instance, avoiding nested loading
  of the fallback's already-planned plugin batch.
  Selection/dependency GUID equality is ordinal/case-sensitive; incompatibility removal
  retains upstream's sequential, declaring-plugin behavior. LoadEligibleCount
  still describes the discovery gate, not the post-selection count.
  Source/build evidence does not establish real-plugin runtime acceptance.
  Discovery emits a candidate for each directly attributed type, including nested
  types with CLR-qualified names. Per-type execution-plan failures do not replace
  sibling results. Assembly references and module-initializer scans are shared;
  assembly-level totals are not multiplied by the number of plugin types.
  CandidateId is a stable digest of normalized full path, exact type, GUID and
  declared version. Loaded membership, host reports and automatic plans use that
  identity; ModId remains the inventory association and GUID remains the dependency
  identity. Aliases are accepted only when unambiguous, with CandidateId resolving
  first. Automatic plans now round-trip the same key without an unmatched prefix.
  File-level inventory rows remain file-level; metadata readiness does not imply
  selection or execution. Existing candidate limits, disabled-file handling and
  metadata-only validation probes remain separate from runtime hosting.
  BasePlugin execution plans describe the inherited public virtual Load contract,
  not a same-name method declared on the concrete class. The host's typed virtual
  call handles inherited overrides, overloads and hiding according to the CLR;
  metadata readiness does not certify a concrete implementation or valid IL.
  External nested base references retain the declaring type in their lookup name.
  Constructed generic base TypeSpecification signatures are decoded with the .NET
  metadata decoder. Ancestry follows the named generic definition through existing
  local/external resolution, including nested names; argument substitution and
  generic constraint validation remain the CLR's responsibility. Displayed base
  names identify the definition, not a reconstructed closed runtime type.
  Open generic plugin definitions are rejected, and BasePlugin candidates require
  the public parameterless constructor actually used by Activator.CreateInstance.
  Parameter counts come from method signatures, not optional parameter table rows.
  Local ancestry cycle keys include module identity and preserve type-name case;
  external lookups require exact qualified names, not a namespace-free alias.
  Existing ancestry depth limits remain, with bounded recursive TypeSpecification
  decoding. These metadata improvements do not prove runtime generic compatibility.
  Ordinary inventory BasePlugin candidates may declare module initializers. The
  runtime host requests RunModuleConstructor after identity checks and registry
  publication, before construction. Like upstream BepInEx, a failed request emits
  a warning and allows construction to proceed; the CLR retains initialization
  success/failure state. No manual initializer calls, retries or side-effect rollback
  are introduced. The CLR can initialize a module earlier during runtime loading,
  so this ordering is not a promise to intercept all initializer side effects.
  PE discovery still executes no plugin code. Parser/candidate validation probes
  keep the default initializer rejection, and the separate activation policy still
  blocks initializer-bearing assemblies. Detection includes <Module>..cctor even
  without source attributes. Counts retain attributed-method totals, with one as
  the minimum when an unattributed module constructor exists, not a doubled count.
- `BepInExRegistry` publishes the original public Instance/Plugins contract
  before Core runtime services or compatibility callbacks. It preserves an existing
  upstream fallback instance. Core has a real BasePlugin object sharing the direct
  bootstrap log source; its public parameterless constructor and the original static
  bootstrap Load entrypoint remain available. BepInEx patch 0019 exposes protected
  event raisers, makes dynamic discovery overridable, and grants Core access to the
  original PluginInfo setters without replacing the public types.
  Metadata is registered before construction; PluginLoad runs after construction,
  before Load, with PluginInfo.Instance still null. Instance is assigned only after
  Load returns. PluginLoaded then sees the successful host bookkeeping. Constructor,
  pre-load observer or Load failure removes only the exact entry inserted by the host;
  a callback's replacement entry is preserved. Selection/dependency rejections are
  also available through DependencyErrors. Post-load observer failures are warnings,
  not activation failures: loaded plugins remain registered. Multicast semantics
  remain normal, so one throwing subscriber can prevent later subscribers of that
  same event from running; there is no per-subscriber isolation or replay.
  Core publishes its successful instance and PluginLoaded only after startup is
  Ready, followed once by Finished. Failed Core startup withdraws its own entry,
  leaves already-running plugins intact, and does not publish Finished or retry.
  Initialize and Execute remain rejected. Dynamic LoadPlugins now discovers all
  supplied directories before activation, reuses owned selection/dependency rules,
  and returns only successful entries from that invocation. Reentrant batches
  recheck registration and incompatibilities before activation; an attempted GUID
  is not retried in this process. Direct LoadPlugin retains its low-level contract:
  construct, PluginLoad, Load, return, without registry/Instance assignment or
  PluginLoaded/Finished. Both APIs require the active Core startup thread.
  A completed direct LoadPlugin call records its copied GUID/version against the
  caller's PluginInfo without retaining that info or adopting its instance.
  If the caller subsequently registers that same info in the active registry,
  matching GUID/version metadata can satisfy later hard dependencies. A registry
  entry alone is insufficient. Owned completed versions take precedence, and an
  owned GUID still loading or previously attempted without success cannot use
  this fallback. Version-range checks, counts and load-event behavior are unchanged.
  Failure reports guard reads of virtual Exception.Message, including individual
  type-loader exceptions. A throwing getter falls back to the exception type
  without replacing the load outcome.
  Dynamic directory registration publishes immutable resolver snapshots; existing
  root order and exclusions remain intact. No upstream hook, logger setup or
  Preloader.Chainloader assignment is introduced. Historical InstanceUsed fields
  continue to describe upstream execution selection, not this data facade, while
  host state reports legacyRegistryPublished separately. Runtime/event acceptance
  still requires an ordinary game run; compiled contracts alone do not prove it.
- `Compatibility/BepInEx/BepInExPluginCompatibility*.cs` owns BepInEx plugin
  discovery and the load gate the host reads. `BepInExPluginCompatibility.cs`
  holds state, the public API and refresh. `.Discovery.cs` reads
  `BepInEx.BepInPlugin` attributes and assembly references from PE metadata,
  builds execution plans and candidates, and reads the parser and candidate
  probes under `Modstoke/ValidationProbes`. `.Planning.cs` records load plans,
  diagnostic load attempts, invocation preflight, pipeline stages and the
  activation policy. `.Menu.cs` is the `Modstoke BepInEx Gate` status provider.
  None of it loads plugin code. Preflight and stage text describes what
  `BepInExPluginHost` does: `Assembly.LoadFrom` into the default load context,
  `Activator.CreateInstance`, then `Load()`. The activation policy is
  diagnostic and the host does not consult it. The stage executor and the
  assembly-load-context chain it modelled are obsolete shells in
  `BepInExPluginStageSnapshots.cs`, kept for binary compatibility and no longer
  recorded. The provider has no self-test commands, so
  `BepInExPluginActivationProbe` state stays empty unless a caller uses its
  API. Libraries, Modstoke-native mods, and RedLoader compatibility mods stay
  outside the BepInEx plugin view.
- `Modding` now exposes public inventory SDK helpers and compact
  catalog state so mods/tools can request `Mods`, `Libraries`, compatibility
  entries, BepInEx plugin compatibility candidates/plans, hostable/hosted
  entries, and issue entries without depending on RedLoader/BepInEx-shaped
  filters or scraping the Mods menu.
  Compatibility entries intentionally exclude libraries; support assemblies are
  queried through `Libraries`.
- `UI/Menus/MenuController.cs` owns the Mods screen lifecycle, keybinds, and
  live controls with their resume onto the mod page.
- `UI/Menus/ModMenuFallback.cs` owns the transitional IMGUI fallback. It draws
  the same view model as the native menu in one column, nav list then page,
  with settings, form inputs, RUN, raw config text with SAVE and REVERT, and
  live controls, so fallback mode is not metadata-only.
- `UI/Menus/ModMenuRenderer.cs` contains the native full-screen mods menu
  renderer, with its nav and page panes, typed controls for provider and
  config settings, and the FRIENDLY/TEXT config editor. Its rows live in
  `UI/Menus/MenuRows.cs` and its scroll panes in `UI/Menus/MenuScroll.cs`.
- UI backend strategy is captured in `ui.md`: native
  Unity UI/TextMeshPro is the default player-facing path, Unity IMGUI is an
  emergency fallback, and Dear ImGui is reserved for optional future
  developer/admin tooling.
- Public mod platform direction is captured in
  `mod-platform.md`: the Mods menu and SDK should be useful to
  third-party mods, while the private admin panel plugs in as a provider.
  The first provider registry slices are implemented in `UI/Menus/Menu.cs`,
  `Configuration/Config.cs`, `Hosting/RuntimeCommands.cs`, and
  `Configuration/ServerPolicy.cs`. The installed-file integrity slice is implemented
  in `Discovery/FileIntegrity.cs`, and the public runtime-attestation slice is
  implemented in `Diagnostics/RuntimeAttestation.cs`.

## Security/Admin Position

- Modstoke Loader may provide a required, disclosed client runtime layer for
  protected servers and OpenACAI-specific features.
- Keep private anti-cheat/admin source out of public loader packages.
- Prefer server policy, signed package identity, file hashes, runtime
  capability state, and disclosed attestation over covert behavior.
- The public SDK should expose stable security-relevant signals without turning
  the public runtime into a hidden anti-cheat payload.
- Server management and admin chat should be implemented as private or
  privileged SDK providers so the public loader UI remains generally useful.

## First Implementation Slices

The next feature priority is the [server-first capability contract](mod-platform.md#server-first-capabilities):
real messaging for unmodified clients, connection identity and permissions,
then verified server-side request validation. Compatibility ownership remains
necessary, but copying old SDK surfaces or adding status-only planning cannot
satisfy the broader platform goal. Keep private toolkit policy separate from
public loader primitives, and preserve the no-client-download baseline.

1. Present the public runtime as Modstoke Loader while splitting
   the current core assembly into OpenACAI Runtime modules. Preserve legacy cleanup
   paths only where needed to avoid duplicate loads during migration.
2. Done: move loader state, inventory, lifecycle, and Sons scene/world/pause
   events behind OpenACAI APIs with visible runtime-state providers.
3. In progress: replace SonsSdk lifecycle dependencies with OpenACAI-owned
   event dispatch. Scene/world/pause and save/load events are now OpenACAI
   surfaces; remaining RedLoader/SonsSdk calls are compatibility relays.
4. Add OpenACAI input/keybind and config UI services.
5. In progress: BepInEx compatibility now has owned metadata discovery, version
   selection/dependency ordering, IL2CPP BasePlugin runtime hosting, public plugin
   registry/events, and runtime module initialization. Discovery recognizes
   inherited entrypoints and constructed generic ancestors without executing code.
   Validation/probe commands remain separate from the real host and are not
   acceptance evidence unless explicitly exercised. Legacy BaseUnityPlugin shapes
   remain metadata-only. Dynamic LoadPlugins and direct LoadPlugin now route
   through the owned host; broad real-mod/runtime acceptance is still open.
6. Add RedLoader manifest/mod compatibility as a shim with explicit failure
   isolation.
7. Split native host and IL2CPP interop hardening into OpenACAI-owned modules.
8. Rewrite the BepInEx-shaped patch/plugin host into OpenACAI-owned code:
   start with the dependency probe, plugin inventory, resolver, lifecycle, and
   Harmony/native-detour gateway, then leave BepInEx APIs as an adapter layer
   for existing plugins.
9. Rewrite the RedLoader-shaped mod host into OpenACAI-owned code:
   use RedLoader/SonsSdk source as a behavioral map, keep manifest/lifecycle
   compatibility where practical, and route new mods through Modstoke SDK
   events and services.

## Success Criteria

- New mods can target Modstoke SDK without referencing BepInEx, RedLoader, or
  SonsSdk.
- Public server APIs provide real operations beyond compatibility relays, with
  explicit authority and client requirements. The paired server toolkit can
  use verified messaging and enforcement hooks without requiring players to
  install a mod. Client-only capabilities are not mislabeled as server-only.
- Existing BepInEx and RedLoader mods can still be detected and loaded through
  compatibility shims when safe.
- Loader startup exposes clear capability state before risky patches run.
- Manager can repair loader-owned files and identify mismatched runtime/package
  metadata.
- If a compatibility shim fails, the loader can still start, report the failure,
  and keep Modstoke SDK services available where possible.

