Modstoke Loader Runtime Patches
========================================

These patches capture the OpenACAI runtime fork changes that are used when
building the packaged loader.

The public GitHub account now has repositories for `openacai-loader`,
`openacai-mod-manager`, `openacai-bepinex`, `openacai-unitydoorstop`, and
`openacai-redloader`. These patch files still keep the bundled runtime source
trail visible inside the loader repository.

Patch sets:

- `bepinex/` applies on top of `BepInEx/BepInEx`.
- `bepinex/0025-Allow-owned-log-event-dispatch.patch` adds a one-time optional
  listener dispatcher. Existing logger, source and collection identities stay
  intact. Future events use the selected owner with the existing ordered listener
  snapshot; conflicting owners reject, and owner failures do not replay through
  the default route. Without selection, standalone dispatch is unchanged. The
  typed Modstoke preloader selects its owned dispatcher during logging setup.
- `unitydoorstop/` applies on top of `NeighTools/UnityDoorstop`.
- `redloader/0001-Use-current-MonoMod-fast-invokers.patch` records the SonsSdk
  fast-invoker ABI repair and local reference-resolution build adjustment.
- `redloader/0002-Roll-back-rejected-mod-registrations.patch` removes exact
  loader-added subscriptions and registry entries when registration fails,
  preserves pre-existing subscriptions, and propagates rejected registration
  through the SonsSdk loader. It also completes multicast priority insertion
  and performs asset metadata discovery before callbacks are registered.
- `redloader/0003-Make-event-dispatch-snapshots-and-one-shot-claims-atomic.patch`
  follows 0002. It synchronizes subscriber snapshots, removal and disposal;
  claims one-shot delivery before callbacks; isolates failing diagnostic
  listeners; and uses the same dispatch contract for SDK-ready progress.
- `redloader/0004-Isolate-mod-asset-startup-failures.patch` follows 0003. It
  validates static asset properties, checks loaded objects before setting flags,
  continues mapping after individual failures, and reports actual mapped counts.
  Bundle and catalog preparation failures no longer abort unrelated mod loading;
  optional missing data folders are skipped. Async catalog handling is unchanged.
- `redloader/0005-Report-shared-SDK-startup-and-catalog-outcomes.patch` follows
  0004. A one-shot completion event reports shared client/server startup after
  initialization and late event setup both finish. Nested managed SDK routines
  report failures and dispose their children; partial startup is not retried.
  Catalog handles are explicitly observed and released, with per-catalog errors.
  The matching Core bridge initially consumed that event to publish shim status;
  the owned driver now supplies that result directly, alongside patch 0063.
- `redloader/0006-Preserve-converted-coroutine-yield-identity.patch` follows 0005.
  The managed-to-IL2CPP enumerator retains its converted Current value until
  MoveNext or Reset, including null. Scheduling and supported yield types remain
  unchanged. BepInEx patch 0018 applies the same contract to its public adapter.
- `redloader/0007-Validate-FMOD-sound-registration-results.patch` follows 0006.
  Both sound registration overloads publish only successful FMOD loads. The
  byte-buffer overload supplies OPENMEMORY with the existing length metadata.
  Failures log their result and leave the ID available for a later registration.
- `redloader/0008-Isolate-keybind-dispatch-and-clear-release-callbacks.patch`
  follows 0007. ModInputCache polls a cached registration snapshot, isolates each
  keybind and callback failure, and clears both performed and released callbacks.
  Input scope rules, bindings and public signatures remain unchanged.
- `redloader/0009-Separate-legacy-manager-from-shared-mod-settings.patch` follows
  0008. A typed SDK startup overload can omit the legacy manager while preserving
  shared settings UI. Settings own their Escape/scene-close lifecycle and expose
  TryOpen with a close notification for an external manager. Registry metadata
  queries no longer cache a missing background sprite before SUI initialization.
  The owned Core supplies the policy and native/IMGUI live-settings entry points.
- `redloader/0010-Isolate-global-key-dispatch.patch` follows 0009. GlobalInput
  uses a cached registration snapshot and isolates each key's polling/callback
  failure, so mutation or failure cannot skip later keys or the Use event.
  Diagnostics are best-effort; public APIs, duplicate-key policy, SDK initialization
  and normal multicast semantics within each registered action are unchanged.
- `redloader/0011-Expose-and-isolate-mod-panel-actions.patch` follows 0010.
  SonsMod exposes freshly bound action callbacks to the owned manager. Invalid
  attributes or delegate bindings are isolated per method with best-effort logs;
  existing instance-method binding and duplicate-name overwrite semantics remain.
  Core supplies temporary real SUI buttons and owns the panel's lifetime.
- `redloader/0012-Isolate-deferred-IL2CPP-type-registration.patch` follows 0011.
  Deferred registration detaches its queue before draining and does not replay it
  on repeated readiness calls. Assembly discovery and individual type failures
  are isolated, usable types survive partial reflection failures, and recursive
  registration of an active assembly is ignored. Diagnostics are best-effort.
  Friend access lets Core call readiness, Harmony initialization and Unity config
  mapping directly, retaining the original contracts and initialization order.
- `redloader/0013-Preserve-host-selected-manifest-during-activation.patch` follows
  0012. A typed TryLoadFromPath overload accepts the manifest chosen by the owned
  host, retaining package layouts beyond the legacy DLL-named subdirectory.
  The original overload and null-manifest fallback retain legacy lookup; parsing,
  compatibility checks, entrypoint selection and registration are unchanged.
- `redloader/0014-Allow-owned-mod-activation-to-bind-SDK-metadata.patch` follows
  0013. SonsSdk grants Core friend access to its existing manifest, mod and asset
  metadata contracts. Core owns ordinary manifest validation, construction and
  registration orchestration without calling SdkEntryPoint.TryLoadFromPath.
  Public SDK entrypoints and the explicit upstream bootstrap remain unchanged.
  Shared registration/callback machinery, manifest parsing and asset-attribute
  binding are still compatibility implementations, not newly owned engines.
- `redloader/0015-Implement-registered-mod-lookup-and-teardown.patch` follows
  0014. FindMelon performs exact name/author lookup in the active registry.
  Public Unregister tears down the registered assembly group, retaining exact
  loader callback cleanup after successful registration. Lifetime checks suppress
  old tracked event snapshots, including across re-registration. Teardown isolates
  deinitialization, Harmony and diagnostic failures; assembly-group reentry is
  blocked. Initialization cannot accept a self-unregistered mod, and SDK startup
  skips unregistered owners before beginning later catalog/bundle work. This is
  not assembly unloading, cancellation of in-flight work or disposal of arbitrary
  mod-owned resources/subscriptions. Public event subscription behavior is unchanged.
- `redloader/0016-Route-owned-asset-attributes-through-Core-bundle-loader.patch`
  follows 0015. Each bundle attribute can carry an internal typed load callback.
  Core binds it only on attributes discovered by the owned host; SDK startup keeps
  its scheduling and the default loader remains for other callers. Handler failures
  do not fall through to a second loader. AssetName's setter becomes internal so
  Core can retain the existing observable default-to-property-name behavior.
- `redloader/0017-Allow-owned-per-mod-catalog-coroutines.patch` follows 0016.
  SonsMod has an internal managed catalog-coroutine callback. Shared startup
  dispatches it for an owned-host mod instead of running the legacy catalog path
  as well; mods without a callback retain that path. Nested coroutine advancement,
  disposal, ordering and SDK completion/failure signals remain SDK-owned.
- `redloader/0018-Allow-owned-shared-SDK-initialization-driver.patch` follows 0017.
  MainInitializer becomes a typed facade when Core selects its startup driver.
  Title/server entry, event-setup completion, failure reporting and the failure
  query forward to the same owner. Selection is one-time and must precede startup;
  repeating the same driver is harmless. Without a driver, original behavior stays
  intact. Core selects its driver only for the normal owned host, not the explicit
  upstream mode. Existing SDK service implementations and public event identities
  remain unchanged.
- `redloader/0019-Separate-coroutine-support-from-lifecycle-publication.patch`
  follows 0018. Core can select external lifecycle ownership while retaining the
  public GlobalBehaviour/Instance coroutine host. That host then keeps maintenance,
  recreation and optional script updates but stops publishing loader events and
  running legacy definite-quit save/sleep/process-kill logic. SceneHandler also
  stops installing or publishing native scene callbacks in this mode, including
  callbacks attached before a failed native-support attempt. Default standalone
  behavior is unchanged. Core supplies definite-quit publication from its own
  quitting driver destruction, without forced process termination.
- `redloader/0020-Allow-owned-managed-coroutine-adapters.patch` follows 0019.
  The public Il2CppManagedEnumerator wrapper can bind an internal managed driver
  for Current, MoveNext and Reset. A separate factory avoids eager native wrapper
  registration when Core selects ownership. Selection is one-time, before the
  first managed enumerator construction; repeating the same factory is harmless.
  Factory failure or a null driver never falls back to legacy execution. Without
  selection the existing implementation remains. Public wrapper/constructor and
  IL2CPP interface identities are unchanged; this does not add native disposal.

- `redloader/0021-Allow-owned-coroutine-support-host.patch` follows 0020.
  An internal GlobalBehaviour host contract routes initialization, creation and
  sibling maintenance to Core while retaining the public/native component and
  Instance field. Selection precedes host use; the same owner is idempotent and
  conflicting or late selection rejects. Selected-owner errors do not fall back.
  Non-quit recreation uses the selected owner. Lifecycle publication, quit state,
  script updates and native coroutine tokens retain their existing behavior.
  Core selects this only on the normal owned path; standalone/upstream defaults
  remain unchanged. No native registration occurs merely by selecting ownership.

- `redloader/0022-Allow-owned-coroutine-dispatch.patch` follows 0021.
  Public Coroutines.Start/Stop can delegate to one internal driver, preserving
  their native Coroutine signatures, CoroutineToken and RunCoro behavior.
  Selection changes future calls only; the same driver is idempotent, conflicting
  ownership rejects and driver exceptions do not fall back. Core's normal path
  selects its driver before patch installation and omits only the coroutine
  prefixes. Explicit upstream mode retains them with shared readiness helpers.
  Standalone behavior remains unchanged without selection. This adds no native
  scheduler, token rebinding, deferred-token delivery or disposal guarantee.

- `redloader/0023-Allow-owned-menu-button-registration-policy.patch` follows 0022.
  The SDK title/pause registration methods consult an internal string-ID filter
  before constructing registrations or touching scene/UI state. Selection does
  not initialize SUI or its assets. The same filter is idempotent, conflicting
  selection rejects and filter errors do not fall back. Existing registrations
  are not removed. Core selects the policy on its normal path and omits the SUI
  Harmony prefixes; upstream mode retains them with the same predicate. Other
  button arguments, accepted-registration behavior and standalone defaults remain.

- `redloader/0024-Allow-owned-SDK-lifecycle-dispatch.patch` follows 0023.
  SDK scene/update/LoadedMainScene handlers can forward to a selected internal
  driver without changing public events or installing duplicate subscriptions.
  Ownership is selected before event setup starts, including partial failure;
  same-driver selection is idempotent, late/conflicting selection rejects and
  driver exceptions do not fall back. Core preserves existing readiness and
  callback order. Standalone/upstream defaults retain the original implementation.

- `redloader/0025-Allow-owned-native-game-event-registration.patch` follows 0024.
  The selected SDK lifecycle driver can register the five native game events at
  the original setup point, between global subscriptions and gameplay/save patch
  installation. Without a selected driver, the original callbacks register in
  their existing order. Core owns normal-path payload conversion and public-event
  forwarding. Registration errors do not start a second registration path or add
  cleanup/retry behavior; public events and save hooks remain unchanged.

- `redloader/0026-Allow-owned-SDK-gameplay-patches.patch` follows 0025.
  The selected lifecycle driver installs SDK gameplay patches at the original
  setup point, before the unchanged save hooks. Core installs the same six
  postfixes under the SDK's Harmony owner and publishes the same public events.
  The cheat-state cache resets on Title and updates after notification as before.
  Without a selected driver, the SDK retains its original patch path. Failures
  do not trigger fallback, rollback or an additional patch pass.

- `redloader/0027-Fix-save-postfix-and-allow-owned-save-callbacks.patch` follows 0026.
  AfterSave is a postfix, correcting its previous before-entrypoint delivery.
  The four public SavingCallbackPatches methods retain their identities and
  registration path, forwarding to the selected lifecycle driver when present.
  Core preserves the manager guards, selected save ID update and public events;
  standalone/upstream mode retains their original bodies. After-load still does
  not inspect the native Boolean result. Neither postfix proves durable IO or
  successful completion, and driver failures never invoke a second handler.

- `redloader/0028-Record-save-hook-installation-outcomes.patch` follows 0027.
  SavingCallbackPatches.Patch applies the existing load and save prefix/postfix
  pairs in order and records each target's NotAttempted, Applying, Applied or
  Failed state. Applied means the Harmony registration call returned, not native
  execution proof. Failed attempts remain uncertain; untouched targets can still
  use Core's legacy relay. Public callback identities, Harmony owner and target
  signatures are unchanged. These records do not track third-party patching or
  unpatching that bypasses this SDK entrypoint.

- `redloader/0029-Allow-owned-SDK-bootstrap-patches.patch` follows 0028.
  MiscPatches.Init can dispatch through the existing lifecycle driver. Beginning
  bootstrap or event setup locks driver selection, including failed setup; the
  same selected driver remains idempotent. Core owns the launch and dedicated
  server prefix setup and handlers, retaining their order and SDK Harmony owner.
  The dedicated player-limit memory patch is still the original SDK operation,
  called before server creation/name hooks. No native patch bytes or memory helper
  behavior change. Standalone/upstream mode retains the original bootstrap path;
  a selected driver's failure does not start a second path.

- `redloader/0030-Allow-owned-SDK-entrypoint-callbacks.patch` follows 0029.
  SdkEntryPoint captures the existing lifecycle driver before registering its
  callbacks. The four original instance methods remain the subscribed delegates,
  with the same order and game-start priority. Their bodies dispatch to Core,
  passing the instance's current legacy-manager setting. Core retains the shared
  cheat state, console calls, UI/setup ordering and boot.txt command dispatch.
  Entry-point setup now also locks owner selection. Standalone/upstream callbacks
  retain their original bodies; a selected driver failure does not run them.

- `redloader/0031-Allow-owned-SDK-command-registration.patch` follows 0030.
  Public GameCommands.RegisterFromType and protected SonsMod.RegisterCommand
  dispatch through the existing lifecycle driver. Core retains reflection order,
  attribute lookup, MonoMod invokers, Boolean results and the native registration
  call. Automatic mod command registration now passes the mod instance, allowing
  attributed instance methods to receive their target. Static handlers remain
  supported. Command registration also locks lifecycle-owner selection; legacy
  standalone registration remains available without a selected driver.

- `redloader/0032-Allow-owned-SDK-startup-overlay.patch` follows 0031.
  The existing lifecycle driver supplies the startup-overlay renderer. SDK
  creation, visibility, offset and foreground updates forward to it while the
  shared fields and ProgressBarContainer identity remain available. Core keeps
  the original native hierarchy, layout, colors and publication order. Progress
  accumulation stays in the compatibility container and SetProgress still only
  updates the foreground. Standalone/upstream rendering retains its original
  body; selected-driver failures do not run a second renderer. Both startup
  implementations also correct per-item increments to a fixed stage share,
  avoiding premature completion caused by repeatedly adding cumulative fractions.

- `redloader/0033-Clean-up-failed-SDK-overlay-construction.patch` follows 0032.
  Both renderers track their new objects before configuring or parenting them
  and publish shared references only after construction succeeds. Failure tries
  deactivation and destruction independently in reverse allocation order, then
  rethrows the original error. Cleanup diagnostics cannot replace that error.
  Previously published objects are outside the failed attempt's cleanup set.
  Successful layout and the existing hide-without-destroy behavior are retained.

- `redloader/0034-Allow-owned-SDK-panel-blur.patch` follows 0033.
  The existing lifecycle driver supplies panel-blur materials, radius updates
  and setup. Public methods, shared cache and current-pass identity stay in the
  SDK; selected-owner failures never invoke the standalone implementation.
  Both paths publish a newly configured pass only after setting its radius.
  Failed setup independently attempts to deactivate and destroy its clone,
  preserving the previous pass reference and the original error diagnostic.
  Successful camera parenting, pass selection, shader and color keys are
  unchanged. No native custom-pass lifetime or immediate-deletion guarantee
  is added.

- `redloader/0035-Allow-owned-SUI-prefab-initialization.patch` follows 0034.
  The existing lifecycle owner supplies prefab acquisition, copying and
  preparation. SDK fields retain their names and types with internal access
  for Core; public control factories and the remaining initialization sequence
  are unchanged. Shared native control constructors remain in the SDK.
  Selected-owner failures do not run the standalone path. Both implementations
  remove scroll-template children in reverse order, fixing the shrinking-count
  loop that left inherited controls behind. Bundle setup, viewport creation,
  readiness publication and scene subscription keep their existing order.

- `redloader/0036-Own-SUI-panel-registration-and-input-lifetime.patch` follows 0035.
  The existing lifecycle driver owns panel registration and removal. Duplicate
  IDs now fail before allocation, respecting the API's unique-ID contract.
  Panel removal, including inherited Remove(), releases its exact toggle-key
  callback and config-key callbacks without removing another owner's binding.
  Config rebinding replaces the panel's previous subscription. Captured input
  callbacks check current panel/binding state, and native destruction timing is
  unchanged. Public dictionaries, method signatures and input component types
  remain compatible; direct native destruction and visibility subscriptions
  are outside this cleanup path.

- `redloader/0037-Allow-owned-SUI-menu-button-registration.patch` follows 0036.
  The existing lifecycle owner supplies menu registration and replay. The first
  accepted registration wins for each case-sensitive scene/ID pair; the same ID
  may still appear in both title and pause menus. Replay uses snapshots and live
  child checks. Late pause registrations attach to the known current menu,
  whose cached reference clears on scene transitions. Publication before a
  factory prevents recursive duplicate registration; an immediate failure
  removes that tentative record and propagates. Public APIs, legacy MODS policy,
  native parent paths, sibling indices and activation-list behavior remain.

- `redloader/0038-Allow-owned-SUI-viewport-construction.patch` follows 0037.
  The existing lifecycle owner creates SUI's persistent viewport and handles
  activation of its stored event system. Shared canvas, raycaster and event
  system references publish only after setup succeeds. A failed attempt cleans
  up its own objects while preserving the original exception and previous
  references. Public getters, layout, component order and the native enabler's
  identity and counter remain unchanged. Other event systems are not managed.

- `redloader/0039-Isolate-SUI-keybinding-capture-sessions.patch` follows 0038.
  Each capture retains its original operation, keybind and event system. SUI
  allows one capture at a time and respects the game's input-binding state.
  Completion and failure restore input independently of icon/config callbacks;
  stale callbacks and delayed restores cannot target a later SUI capture.
  Config replacement, per-control revert and direct removal cancel first.
  Public APIs, binding-path application, timeout and icon layout are retained.
  Parent hiding/destruction and external config synchronization are unchanged.

- `redloader/0040-Preserve-keybinding-paths-and-refresh-config.patch` follows 0039.
  Rebinding saves the applied path rather than a device control's bare name.
  Simple keyboard values retain their short form; other device paths stay
  qualified. The existing no-argument save API uses the effective binding path.
  Cached keybinds refresh when their config changes, and bulk reset follows the
  keybind control's normal reset path. No config migration or new schema is used.

- `redloader/0041-Allow-owned-input-icon-construction.patch` follows 0040.
  The lifecycle owner supplies icon database lookup, cloning, attachment and
  keybind replacement. Keybinds use the clone already returned by the icon
  manager instead of leaking it through a second clone. Replacement completes
  before old icons are retired; failed construction cleans up only the new
  icon. The layout template is retained when it also serves as the active icon.
  Public APIs, database cache, control selection and layout values remain.

- `redloader/0042-Allow-owned-keybind-registration-and-polling.patch` follows 0041.
  The lifecycle owner supplies compatibility keybind registration, polling and
  callback dispatch. Public actions, config subscriptions, scope checks and
  callback snapshots retain their identities and ordering. Registration logs
  can no longer prevent a cached action from being enabled. The standalone
  path remains available only when no lifecycle owner was selected.

- `redloader/0043-Allow-owned-global-input-dispatch.patch` follows 0042.
  GlobalInput routes registration, removal, initialization and key/Use polling
  through the lifecycle owner. Shared state, SDK event callback identities,
  registration snapshots and exact callback removal remain intact. The public
  Use event and native action lifetime retain their existing behavior.

- `redloader/0044-Allow-owned-settings-registration.patch` follows 0043.
  The lifecycle owner handles settings registration and close-callback binding,
  using the shared SDK widget generator and entry types. Instance callbacks
  bind to the supplied settings object. Callback validation precedes UI
  allocation in both paths; public overloads and lookup APIs remain intact.

- `redloader/0045-Allow-owned-settings-widget-generation.patch` follows 0044.
  Settings widget generation routes through the existing owner. Indexed and
  unreadable properties are skipped in both paths. Supported config types,
  member order, include/ignore attributes and control bindings are preserved.

- `redloader/0046-Correct-settings-validation-feedback.patch` follows 0045.
  Generated controls refresh from the value accepted by config validation,
  including when validation leaves config unchanged. Control binding precedes
  config feedback, and refreshes do not write back into config. Integer sliders
  use whole-number mode. Public observable behavior and subscription lifetime
  are unchanged.

- `redloader/0047-Restore-config-load-change-tracking.patch` follows 0046.
  Config loading restores its previous change-tracking state on success and
  failure, including handled conversion errors and nested loads. Validation,
  notifications, existing dirty flags and exception handling remain unchanged.

- `redloader/0048-Allow-owned-settings-panel-lifecycle.patch` follows 0047.
  The existing Settings owner handles panel creation/open/close, Escape polling,
  entry parenting, category visibility, save/revert and close callbacks. Public
  SDK types and panel state remain shared; standalone behavior and SDK event
  subscriptions are retained. Saving still precedes explicit close callbacks
  and restart evaluation.

- `redloader/0049-Allow-owned-dialog-and-tooltip-services.patch` follows 0048.
  Dialog and tooltip operations route through the lifecycle owner while keeping
  SDK native types, instances, callbacks and public APIs. Showing a dialog clears
  callbacks from the previous one; choosing an option or cancel closes it before
  invoking the captured callback, allowing that callback to open a replacement.
  Standalone dialogs receive the same fixes. Tooltip behavior is unchanged.

- `redloader/0050-Fix-tooltip-setup-and-raycast-lifetime.patch` follows 0049.
  Tooltip setup publishes state only after construction succeeds. Failed
  candidates release their label binding and captured objects without masking
  the original error. Starting or stopping raycasting hides the old tooltip;
  null or destroyed raycasters stop polling. Both implementations are updated.

- `redloader/0051-Fix-modal-dialog-setup-publication.patch` follows 0050.
  Dialog setup publishes the singleton only after native initialization finishes.
  A failed attempt deactivates and destroys its cloned root without masking the
  original error. Owned and standalone paths retain the same SDK callbacks,
  button listeners, parenting and layout.

- `redloader/0052-Allow-owned-blueprint-import-and-registration.patch` follows 0051.
  GLTF directory loading, recipe ID derivation and prefab registration route
  through the SDK lifecycle owner. Public APIs, shared registration objects,
  registry and events remain in SonsSdk. Standalone loading and downstream
  crafting-node construction keep their existing behavior.

- `redloader/0053-Observe-blueprint-loading-during-SDK-startup.patch` follows 0052.
  Blueprint initialization returns a task observed by SDK startup. Client ready
  callbacks wait for imports; dedicated startup reports completion only when
  imports and event setup finish. Failures use the existing one-shot failure
  path. Synchronous server service setup stays in the bootstrap prefix.

- `redloader/0054-Fix-GLTF-load-resource-cleanup.patch` follows 0053.
  Scene and individual-asset loads release their primary stream on failure and
  preserve the original error through cleanup. Rejected overlapping calls no
  longer release another load's resources or running state. The GLTF project
  accepts the SDK reference path and makes game deployment explicitly opt-in.

- `redloader/0055-Propagate-threaded-GLTF-load-errors.patch` follows 0054.
  Opt-in threaded stream loading and JSON parsing use awaited tasks, matching
  the importer's other threaded operations. Worker errors reach the load task
  and its cleanup path; raw threads and their busy-wait helpers are removed.
  Default sequential loading is unchanged.

- `redloader/0056-Route-blueprint-crafting-through-owned-runtime.patch` follows 0055.
  Blueprint activation, crafting nodes, built prefabs and recipe registration
  use the owned runtime. SDK types, caches, events and standalone behavior remain.
  Template setup publishes its shared state only after completion; failed
  candidates are cleaned up without hiding the original error.

- `redloader/0057-Route-blueprint-helpers-through-owned-runtime.patch` follows 0056.
  Mesh preparation, ingredient lookup, interaction UI, display names and book
  localization use the owned runtime while retaining the public SDK methods.
  Interaction setup resolves its UI objects before publishing the initialized
  marker and discards a failed clone without masking the original error.

- `redloader/0058-Route-network-prefab-registration-through-owned-runtime.patch`
  follows 0057. The owned runtime handles EntityManager registration, clearing
  and its lookup postfix using the shared SDK registry. Lookup rebuilding skips
  only an already-present prefab instead of skipping all remaining registrations.
  Standalone lookup gets the same fix; unregister still removes only from the
  managed registry.

- `redloader/0059-Route-SDK-packets-through-owned-runtime.patch` follows 0058.
  Packet registration, framing, dispatch, relaying and native hooks use the owned
  runtime while retaining SDK event types and registry identities. Chat handlers
  now stop at the first handled result, as documented, in both runtime paths.
  Packet headers, native clone behavior and routing semantics are unchanged.

- `redloader/0060-Route-server-commands-and-chat-through-owned-runtime.patch`
  follows 0059. Command registration, execution, replies and permission checks
  use the owned runtime. The public command registry/types and existing static
  chat subscription remain in the SDK; parsing, roles and reply targets stay
  unchanged. Command diagnostics now identify the command being executed.

- `redloader/0061-Route-game-manager-lookups-through-owned-runtime.patch`
  follows 0060. Activation and manager lookup use the owned runtime with the
  shared SDK instance and existing callback. Missing manager children return
  null; `dump characters` reports an unavailable manager without writing a file.

- `redloader/0062-Route-native-player-limit-patch-through-owned-runtime.patch`
  follows 0061. The dedicated-server player-limit patch uses owned method
  resolution, bounded reads and checked native writes. The existing signature,
  offset and NOP bytes remain; standalone SDK behavior and public Reflow APIs
  are unchanged. Native runtime acceptance is still pending.

- `redloader/0063-Decouple-initialization-ownership-from-public-completion.patch`
  follows 0062. Selecting the owned initializer checks actual startup state, not
  disposal of the public compatibility event. A conflicting driver or previously
  started, finished, failed or event-initialized SDK still prevents selection.
  Core owns terminal state and observes completion internally; the public
  one-shot notification and unowned initializer behavior remain available.

- `redloader/0064-Keep-managed-UI-callbacks-out-of-native-registration.patch`
  follows 0063. Modal callback setters and events, and the color-picker event,
  are explicitly hidden from IL2CPP registration because they use managed
  delegates. Managed signatures, subscriptions and callbacks remain unchanged;
  Unity-facing click, lifecycle and pointer handlers remain available.

- `redloader/0065-Treat-settings-close-callback-as-optional.patch` follows 0064.
  An absent optional settings-close callback no longer produces a missing-method
  warning. Existing method selection and delegate binding are unchanged.

Current BepInEx patch series:

- `0001-Port-BepInEx-runtime-projects-to-.NET-10.patch`
- `0002-Modernize-IL2CPP-runtime-hosting-for-OpenACAI.patch`
- `0003-Update-Il2CppInterop-packages-for-net10-IL2CPP.patch`
- `0004-Harden-IL2CPP-native-detour-setup.patch`
- `0005-Expose-IL2CPP-native-hook-status.patch`
- `0006-Make-IL2CPP-interop-generation-atomic.patch`
- `0007-Use-OpenACAI-loader-version-in-runtime-branding.patch`
- `0008-Add-net11-preview-IL2CPP-runtime-target.patch`
- `0009-Clean-up-net11-preview-runtime-warnings.patch`
- `0010-Modernize-Harmony-and-MonoMod-for-net11.patch`
- `0011-Wire-BepInEx-to-OpenACAI-net11-MonoMod.patch`
- `0012-Clarify-OpenACAI-loader-version-branding.patch`
- `0013-Rename-public-loader-branding-to-OpenACAI-Endnight.patch`
- `0014-Tolerate-existing-interop-instruction-set-registrations.patch`
- `0015-Allow-owned-patcher-engine-to-reuse-compatibility-contracts.patch`
- `0016-Share-native-provider-config-identity-with-owned-preloader.patch`
- `0017-Balance-log-source-collection-registrations.patch`
- `0018-Preserve-converted-coroutine-yield-identity.patch`
- `0019-Expose-plugin-registry-and-event-contracts-to-owned-host.patch`
- `0020-Expose-Cecil-resolution-event-to-owned-preloader.patch`
- `0021-Allow-owned-native-hook-status-publication.patch`
- `0022-Allow-owned-managed-coroutine-adapters.patch`

The BepInEx patches move the IL2CPP runtime track to .NET 10, update the
OpenACAI hostfxr-facing entrypoint behavior, refresh Il2CppInterop packages,
and harden native detour setup for Dobby/Funchook and `il2cpp_runtime_invoke`.
Patch `0005` also writes early IL2CPP hook diagnostics to
`BepInEx\plugins\OpenACAILoader\openacai-il2cpp-hook-state.json` so the
OpenACAI bridge and manager can see provider selection, runtime invoke patch
status, chainloader execution, and pre-plugin failures. Patch `0006` generates
IL2CPP interop wrappers into a staging directory, validates the generated
assemblies, writes the hash file there, then swaps the staging directory into
`BepInEx\interop` so failed generation does not erase the last known-good
wrappers. Patch `0007` keeps BepInEx credited as the IL2CPP runtime while using
the installed OpenACAI Endnight Loader manifest version for the console title
and first startup banner. Patch `0008` is only for the
`openacai-net11-preview` branch:
it pins the preview .NET 11 SDK, adds `net11.0` targets for the BepInEx Core,
Preloader.Core, and Unity.Common projects consumed by IL2CPP, and moves the
IL2CPP runtime project to `net11.0`.
Patch `0009` removes net11-specific warning noise by compiling BepInEx's
interpolated string handler attribute shims only for legacy targets and by
removing an unused Unity-version parse exception variable. Patches `0010` and
`0011` modernize Harmony/MonoMod support for the net11 preview path, patch
`0012` centralizes version-aware runtime branding, and patch `0013` finalizes
the then-current human-facing product name as OpenACAI Endnight Loader. These
patch descriptions and filenames preserve the historical source trail; the
current public name is Modstoke Loader. Patch `0014` allows
the compatibility manager to start after the OpenACAI public conversion path
has already registered Cpp2IL instruction sets, preserving existing registrations.
Patch `0015` gives the owned preloader friend access to the original patcher
contracts for metadata parsing and context setup without changing their type identities.
Patch `0016` shares the original native provider enum identity with the owned
preloader. Both factories can bind the same config key without an invalid cast.
Patch `0017` removes a duplicate log-source list insertion and balances event
unsubscription for every registration removed by the collection, preserving
the original public logging types and explicit repeated-registration behavior.
Patch `0018` caches each converted enumerator Current value until advancement or
reset, preserving child-wrapper/string/boxed-value identity across repeated reads.
The RedLoader counterpart is patch 0006. Neither patch introduces disposal or
thread-safety guarantees, changes cancellation, or expands supported yield types.
Patch `0019` gives owned Core friend access to the original PluginInfo setters,
protected event raisers, an overridable dynamic-discovery entrypoint, and a
BasePlugin constructor that reuses its bootstrap log source. The owned registry
can expose legacy data/events without upstream execution; ordinary BepInEx
startup and multicast event behavior remain unchanged.
Patch `0020` exposes the existing TypeLoader.AssemblyResolve event through an
internal friend bridge for the owned preloader. Legacy resolution still invokes
that same event with ordinary multicast behavior; its resolver and reader
parameters are not replaced or modified.
Patch `0021` permits typed Preloader to register one diagnostic snapshot writer.
The existing hook-status model and public methods remain compatibility APIs.
Registration publishes the current snapshot, repeat registration is idempotent,
and writer failure never falls through to a second publisher. Without a
registered writer, the original BepInEx file writer remains unchanged.
Patch `0022` gives Core friend access to an internal managed-enumerator driver
factory. The public wrapper captures its driver at construction and forwards
Current, MoveNext and Reset when selected. Selection is one-time; repeat selection
of the same factory is harmless, and failures never run a second implementation.
Wrappers created before selection retain their legacy state and implementation;
only subsequent construction uses the factory. Keeping the selector separate from
the wrapper preserves lazy native type registration. Public wrapper and native
interface identities, default standalone behavior and native disposal remain unchanged.

Patch `0023-Brand-loader-display-as-Modstoke.patch` updates the fallback console
product name and credits it as "by OpenACAI". Public branding API names, plugin
identities, version lookup, environment keys and manifest paths remain unchanged.

The RC2 dependency update is recorded in
`bepinex/0024-Use-net11-RC2-runtime-dependencies.patch`. It pins the RC2 SDK,
logging dependency, and matching `.5` MonoMod packages without changing
assembly or package identities.

The matching MonoMod update is
`monomod/0001-Port-net11-runtime-to-RC2.patch`, based on the OpenACAI fork at
`0447f29642b3aa220fd022a5d63845d4220f1139`. It updates the JIT GUID, vtable,
chunk-based allocation reader, and method-info accessor; older runtime paths
remain unchanged.
The old native allocation hook is disabled for .NET 11. This is a Windows x64
loader port, not macOS ARM64 acceptance. RC2 analyzer compatibility changes
preserve existing public APIs and older target frameworks.

Patch `bepinex/0026-Route-native-detours-to-Modstoke.patch` retains the legacy
`INativeDetour` contract and binds its factories to the owned preloader. The old
BepInEx detour implementations and native bindings are excluded from the build.
The paired Modstoke preloader supplies provider selection, callback lifetime,
reversible undo and terminal cleanup. This removes a duplicated engine path;
the rest of the IL2CPP compatibility assembly is still transitional.

Current UnityDoorstop patch series:

- Base: `NeighTools/UnityDoorstop` upstream `master` at
  `3ac0156aebc33305865fa3dd2559752ed1dcdf00`, one commit after upstream
  `v4.5.0`.
- Local branch: `openacai-hostfxr-net10`.
- `0001-Add-hostfxr-CoreCLR-bootstrap-path.patch`
- `0002-Improve-hostfxr-bootstrap-for-OpenACAI-loader.patch`
- `0003-Harden-hostfxr-bootstrap-diagnostics.patch`
- `0004-Validate-native-runtime-architecture-before-hostfxr-.patch`

The UnityDoorstop patches add `hostfxr_path` and `runtime_config_path` support
for the OpenACAI .NET 10 IL2CPP package, then harden the bootstrap by passing an
explicit dotnet root, validating hostfxr/runtimeconfig/target paths, wiring
hostfxr error-writer logging, unloading hostfxr on pre-runtime failures, and
avoiding legacy CoreCLR fallback when hostfxr is configured. Patch `0004`
validates the Windows PE machine type for configured `hostfxr.dll` and
`coreclr.dll` before loading them, so an architecture mismatch is logged as a
bootstrap failure instead of becoming an early crash.

These patches preserve upstream credit and licensing. OpenACAI-specific changes
are authored by Alex Cooper for OpenACAI Inc.
