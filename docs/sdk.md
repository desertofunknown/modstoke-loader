# Modstoke SDK

Developer: Alex Cooper

Modstoke SDK by OpenACAI is the modding surface for Sons Of The Forest.
It uses RedLoader/SonsSdk as a compatibility reference, but it should not become
a direct copy or a permanent dependency on SonsSdk internals.

The SDK is part of the broader Modstoke Loader runtime rewrite described in
`runtime.md`. New mods should be able to target Modstoke SDK APIs
without referencing BepInEx, RedLoader, or SonsSdk directly.

The public mod platform direction is captured in
`mod-platform.md`. Modstoke Admin is a first-party
consumer of the SDK, not the reason the public UI and SDK exist.

For Bolt packet policies, event payload reads, and stream, state, command and
entity lifecycle observations, see [Bolt networking](BOLT_NETWORKING.md).
The [event policy reference](BOLT_EVENT_POLICY.md) covers pre-routing event decisions.

## Direction

- Own startup and execution in Modstoke Loader, retaining legacy assembly and
  API identities where existing mods need them. Modstoke builds its own BepInEx,
  RedLoader and SonsSdk compatibility assemblies. Retained third-party libraries
  are listed in the explicit dependency inventory; neither original loader is
  a build requirement.
- Reimplement Sons-specific modding APIs under OpenACAI-owned namespaces.
- Keep UI/config/action services useful to third-party mods even when the
  private admin panel is not installed.
- Use SonsSdk source and docs to understand useful feature areas, then rewrite
  those features with safer lifecycle, clearer state, and narrower dependencies.
- Preserve upstream credits and licenses when behavior is informed by upstream
  work.

## Dedicated-server initialization

Accepted `Server` and `Universal` SDK mods receive `OnSdkInitialized`, followed
by `OnSdkLateInitialized`, after shared services and SDK event wiring are ready.
Successful `OnSdkInitializationCompleted` follows those callbacks. Reentrant
event-setup calls do not repeat initialization or report completion midway
through the callback list. Reported startup failure prevents a success result;
individual mod callback exceptions retain the existing event-dispatch handling.

Dedicated startup skips the loading overlay, client settings and manager menus,
client title-scene initialization, input polling and pause-menu waiting. The
client lifecycle is unchanged. `OnAfterSpawn` and `OnInWorldUpdate` remain player
events; SDK initialization does not mean a server world is loaded or accepting
players. Mod authors must avoid client-only services in server callbacks.

The package builder accepts a `GameRoot` containing either `SonsOfTheForest.exe`
or `SonsOfTheForestDS.exe`. `GameInteropRoot` remains a separate build-reference
directory, defaulting to `BepInEx/interop` under that root. Compilation against
existing wrappers does not qualify their native behavior on a dedicated server;
the runtime generates interop for the installation it actually starts in.

These startup paths still require dedicated-server runtime acceptance, including
callback order, service readiness and supported mod behavior. They do not
establish a higher player limit or an authenticated player identity.

## Population observations and dedicated validation

On the game thread, `ServerWorld.TryReadPopulation` copies the current
server's runtime maximum, replicated maximum and reported player count, together
with the lengths of its client, color, Steam ID and role arrays. It requires a
live, attached, server-owned information entity and checks that its binding and
the observed fields remain unchanged during the read. No array contents or
player identities are returned. Missing, changing or unreadable state returns
an explicit read status instead of an estimated total.

The result distinguishes dedicated and listen servers. `ServerInfoNetworkId`
belongs only to the current world; it is not a save identity. `ObservedAt` is
the read completion time. Reported player counts are not authenticated session
counts, and array lengths and configured maxima are not qualified capacity.
Mismatches are retained for callers to inspect.

`ServerWorld.PlayerLimitPatch` separately reports the last observation
of the existing dedicated configuration validation bypass. The patch recognizes
one instruction-aligned upper/lower guard pair with a shared error destination,
retains the lower bound, verifies its write and verifies repeated calls without
writing again. Unsupported instructions are not patched. Failed attempts are
not retried automatically; unrelated dedicated bootstrap hooks still register.
The compatibility fallback uses the same implementation. Reading the status
does not inspect memory again; `Applied` records the last successful check.

This preserves the existing validation behavior without selecting a new count
or changing a configuration file. It does not patch hosted lobby capacity,
admission, array indexing, saves or client interfaces. The 8/16/32-player stages
remain unqualified until the corresponding multiplayer behavior is observed.

## Native mod dependencies

Native manifests can opt into required executable mod IDs and exact versions
through `modstokeDependencies`. The Loader checks the graph before startup,
orders callbacks by dependency, and stops dependent runtime callbacks after a
prerequisite fails. Existing manifests and compatibility plugin dependency rules
retain their behavior. See [Mod dependencies](mod-dependencies.md) for
the format, authoring limits and distribution requirements.

## Client command replies

`ClientCommands.Submit` sends a slash-prefixed command to registered
server commands through the existing private command channel. It does not require
managed admission. The server still applies chat policy and the registered
command's authorization. Retain the returned request handle and read its immutable
`State`, or observe `Completed`; a sequence is unique only within one connection.

`SupportsReplies` reports whether the current handshake negotiated inline replies.
For those peers, a `ServerReported` state can include `Reply` and
`ReplyTruncated`. Text is limited to `MaxReplyBytes` (512 UTF-8 bytes), and the
complete frame remains within 1024 bytes. Escaping may require a shorter prefix.
Truncation preserves Unicode scalar boundaries; an empty shortened prefix is
possible. Treat reply text as untrusted display content. A null reply means no
inline text was reported, not that another reply channel delivered successfully.

On the game thread, `TryGetCurrentSession(out Guid sessionId)` returns a cache
identity while the negotiated command connection is live. It changes with each
handshake and exposes no native handle or token. Clear cached views when this
read becomes unavailable or returns a different identity. It is neither a world
identity nor proof of membership or integrity. A previously received reply can
become stale while the same connection remains live; request current information
explicitly and never treat cached text as permission to perform an operation.

The optional hello/ack capability is `CommandReplyVersion = 1`. The existing
protocol and command versions remain 1. Peers without the capability continue
receiving outcomes without inline text; existing game-chat and HUD reply behavior
is preserved for every peer. Unnegotiated inline replies are rejected before a
pending request is removed. Connection, recipient, token, sequence, timeout and
pending-request checks remain in force.

`Queued` and `SendUncertain` do not prove execution. A server-reported outcome is
the command handler's report, not proof of world state or persistence. Timeouts
must not trigger automatic retries of operations that might already have run.
Listen hosts continue using their native command route; this client API targets
a remote server.

## Transient menu forms

Menu providers may implement `IMenuInputProvider` alongside content and
commands. `GetInputs()` returns an immutable `MenuInputSnapshot` with a
nonempty scope ID and up to eight single-line fields. Each field has a character
limit and an enabled state. These drafts are held by the provider; the menu does
not write them to configuration files or publish them in the loader service.

`TrySetInput` must recheck the scope and field under the provider's own lock.
Replace the scope when a connection, form or other owning context changes, and
clear private drafts when that context ends. A stale field callback must not
modify the replacement form. Captured actions need the same protection; providers
can include their scope in command IDs and validate it before queueing work.

The native and fallback menus display content alongside commands and periodically
refresh the open provider. UI callbacks should change managed drafts or queue
intent; game operations still belong on the game thread. Input limits do not
replace domain validation, server authorization or transport byte limits.

## Provider settings

`IMenuSettingsProvider` adds editable rows to a provider's own page, above its
forms, content and commands. `GetSettings()` returns a `MenuSettingsSnapshot` with
a nonempty scope and up to 32 `MenuSettingSnapshot` rows with unique keys. Each row
has a section, key, label, kind (`ConfigValueKinds`: text, boolean, integer, number
or choice), current value, description and whether it can be edited. Choices carry
one to 32 options; integers and numbers may carry a minimum, maximum and step. The
snapshot constructors reject rows whose value does not fit their kind, and a page
whose `GetSettings()` throws hides its settings and logs the exception type once.

The menu renders booleans as a toggle and choices with previous and next buttons.
An integer or number with both a minimum and a maximum gets a slider in the native
menu; other numbers, and every number in the fallback menu, get a field with APPLY,
plus - and + buttons when the row declares a step. Text gets a field with APPLY.
Before calling `TrySetSetting(scope, key, value, origin, out result)`
the registry checks that the scope matches a fresh snapshot, the row is editable and
the value fits its kind: exactly `true` or `false`, one of the options, an invariant
number within the limits, or at most 256 characters without control characters. The
mods menu passes `CommandOrigin.ModsMenu`; other mods can call
`MenuRegistry.TrySetProviderSetting` and arrive as `OtherMod`. Recheck the scope and
authorization in the provider.

Unlike `IConfigProvider` values, settings are never published to a service, written
to the Loader state or logged, so a page can show player IDs or roles. Result
details appear in the menu's status line, which is published, so keep private values
out of them. A dedicated server has no menu; edit server-side state from a client or
listen host through your own command path, or through the console.

## Menu page owners and refresh

A menu provider can implement `IMenuProviderPresentation` to say where its page
belongs. `OwnerModId` names the mod that owns the page, using the ID the mod
inventory lists for it, and `Group` is a short label that keeps related pages from
the same owner together. Both default to null. `MenuProviderDescriptor` accepts them
as init-only properties, and `IConfigProvider` has its own `OwnerModId`, which its
`openacai.config.<id>` page carries. The registry trims both values and treats blank
ones as unset. `MenuProviderSnapshot` reports them, together with `HasInputs` beside
`HasContent`, `HasCommands` and `HasSettings`. The menus nest a page under its
owning mod in the MODS group of the nav and show `Group` beside its name, with
pages of the same group kept together. A page without an owner, or whose mod the
current filter hides, is listed under MOD PAGES instead.

`MenuRegistry.NotifyContentChanged(providerId)` tells the menus that a provider's
content, commands, forms or settings changed. A menu showing that page refreshes it
on its next frame instead of at the next periodic refresh, although the native menu
still waits while the pointer is scrolling, dragging or held down. The call returns
false for an unknown ID, can be made from any thread and never calls back into the
provider; the menu reads fresh snapshots on the game thread as usual. Periodic
refresh continues for every provider, so calling it is optional.

## Console commands and origins

Every provider command is also listed in the console as `<providerId>/<commandId>`.
A console provider can declare a short `ConsolePrefix`, such as `admin` for
`admin/status`. It is used only while no other provider claims the same prefix or
has it as its id. The console views list the short name; the server console and the
pipe also accept the full name.
A provider that implements `IConsoleCommandProvider` receives
`TryInvokeCommand(commandId, invocation, out result)` instead of the argument-free
call. `invocation.Arguments` is the untrusted, trimmed text typed after the command
name (one line, at most 1024 characters), and `invocation.Confirmed` reports an
explicit confirmation for a command that requires one. The registry checks the
provider's current `CommandSnapshot` before every call and refuses an unconfirmed
command that sets `RequiresConfirmation`, so a provider never receives one. The public
`MenuRegistry.TryInvokeProviderCommand` carries no confirmation and can't run those
commands. Arguments sent to a provider without the interface are refused. The Loader's own views invoke on the game
thread, at most four console commands per frame. Results may span several lines;
keep them short.

`invocation.Origin` names the surface the command came from:

| Origin | Who |
|---|---|
| `OtherMod` | another mod, through the public `MenuRegistry.TryInvokeProviderCommand`; also the default value |
| `ModsMenu` | a RUN button in the mods menu, used by this process's local player |
| `InGameConsole` | the in-game console overlay, used by this process's local player |
| `DesktopConsole` | the desktop console or Manager pipe, which admits only the Windows user running the process |
| `ServerConsole` | a dedicated server's own console input |

The local player is the host on a listen server and a client otherwise, so a
provider that runs operator actions should relay client-side requests to the host
and authorize them there. The origin is a label for that policy, not a boundary
against other code in the process, which can do anything a provider can.

On a dedicated server the game's console window and output stay as they are. The
Loader also reads lines from the server's standard input, typed in its window or
written by a hosting panel, and runs them as `ServerConsole` commands. Results print
through the server log: in the server's console window and `LogOutput.log`, but not
on a redirected standard output, so a panel that only captures stdout sees them in
the log file. `help` lists the commands there, and `confirm <command>`
runs one that asks for confirmation. Each game or server process also publishes its
console pipe in `Modstoke/UserData/console-endpoint.json` (`Schema`, `Pipe`,
`ProcessId`, `StartedUtc`) so the Manager or a desktop console can attach; check
that the process is still running before trusting a leftover file. The pipe uses
the same length-prefixed JSON exchange as `ConsoleWire`.

## Player attachment handles

`ServerWorld.TryCapturePlayerBinding(session, out binding, out status)`
captures the current player attachment for an exact remote session.
`TryCaptureLocalPlayerBinding(out binding, out status)` captures the actual
listen host's local player. Both require the SDK's game thread and a running
server. Dedicated servers have no local-player binding.

Retain the `PlayerHandle` and call
`TryReadPlayerBinding(binding, out snapshot, out status)` for subsequent reads.
The handle retains the native attachment and checks it against the current
player, session and network ID. Normal detach/reattach creates a new attachment,
including when the game reuses a component or network ID. A read retires the
handle and returns `BindingChanged` only when the attachment has provably ended:
the player component was destroyed, Bolt detached or replaced its entity, the
player's session closed, the session or host now resolves to a different attached
player, or Bolt stopped. Later reads cannot rebind a retired handle, and
`binding.IsRetired` stays true; after Bolt stops, reads report `NotServer`. Other
failures, such as a missing player lookup, unavailable session tracking or a
native read exception, return their own status and leave the handle valid; keep it
and read again rather than capturing a new one. Release unused handles so their
retained native objects can be collected.

These APIs add no hooks or gameplay policy. They do not establish permissions,
trusted positions, health or life state. Death, revival or respawn within the
same attachment needs separate handling by the consuming mod. Existing world
snapshot APIs and their behavior are unchanged.

`TryReadPlayerLife(binding, out life, out status)` reads the native `Dying`
field for that same local or remote attachment. It validates the retained
attachment before and after the read and checks that the native player state
has not been replaced. `life.Binding` is the exact input handle;
`RawDyingState` preserves the native value and `LifeState` is `Alive`, `Dying`,
`Dead`, or null for an unknown value. `ObservedAt` is the UTC read completion
time. This additive API requires SDK build 0106.

The observation does not establish health authority or a life-generation token.
A player can die and revive between observations. Travel integrations should
retain the same attachment, check current permissions and life state before
dispatch, and cancel tracking when their world, session or attachment changes.
Later position observations do not acknowledge a native movement request or
prove destination safety. `QueueSpawnAssignment` retains its existing queued
and uncertain-send semantics; this API adds no teleport or retry behavior.

## Current Locator Records

`Modstoke.Sons.LocatorRecordReads.TryBeginObserving` returns an
`LocatorRecordObserver` lease. Acquire it on the loader's game thread
and retain it while observations are needed. Disposing one lease does not stop
other observers. Disposal is allowed from any thread; activation and reads are
game-thread operations.

The shared index advances by at most 256 group/record steps per update while a
lease is active. It visits grouped, world-ID ungrouped and ordinary ungrouped
records, deduplicates repeated group objects, and preserves matching records at
different indexes. It retains managed keys and numeric addresses, not native
wrappers. Scene/world changes, unavailable managers/caches, and observed root or
list changes discard the index. These are observable invalidations, not an
exhaustive native mutation or lifetime certificate.

`observer.TryRead(locatorId, out page, out status, maxRecords: 1)` reads a bounded
page without scanning the world. `maxRecords` accepts 1 through 16 and defaults
to 8. Follow `page.NextCursor` with `TryReadNext` to inspect additional indexed
matches, preferably across updates when many matches are present. `Changed`
means the cursor's generation or roots changed; restart with `TryRead`. Empty
building pages are normal. A completed pass remains available during the next
incremental pass, but every returned record is reacquired and checked again.

Keys preserve the native 12 bytes, represented as a GUID with its final four
bytes zeroed. Each record reports its own observation time and status. Reads
check current membership, key bytes, list bounds, stored pose and data-slot
references. Missing or misaligned lists remain unavailable; detected mutations
return changed records without stale pose or candidate values. Stored root pose
is not an active object's or falling tree's world pose.

`CurrentCacheSlotOnly` provenance means exactly that: the data reference was in
the current cache slot. It does not establish what was originally serialized or
used when the record was initialized. `IsTree` follows the native `Trees`
classification; `SmallTree` is separate. Each LOD's prefab observation follows
its current root swapper, cut prefab, descendant managers and fractured-object
Bolt entity. Traversal inspects at most 128 transform nodes per LOD, using the
first manager component on each object. Candidate IDs are raw prefab IDs, before
sender-side wire transformations. Missing paths, limits, failures and changed
references have explicit issue flags. No prefabs are instantiated or modified.

All snapshots are managed copies. `CoverageIsAuthoritative` is always false.
`PassCompleted`, `IndexedMatchCount`, apparent uniqueness, an empty candidate set
or a mismatch cannot establish absence, historical identity, legitimate cutting
or a reason to reject a player. Referenced objects can change without updating
list versions; these reads are not atomic. The service adds no enforcement and
requires no client extension. Runtime behavior and frame cost remain unverified.

## Map Layers

`Modstoke.Sons.MapLayers` lets a Modstoke mod publish what it puts in the world
for the hosting panel's 3D island map. The mod is named by its manifest ID and
must have started; the layer carries that manifest's version.

- `TryRegisterModel(modId, modelId, root, out status)` exports the meshes under a
  `GameObject` in its local space, merged into one primitive per material. A
  `Mesh` with its materials, or a list of `MapSurface` built by the mod, also
  works. Unity objects are read on the game thread; surfaces on any thread.
- `TryRegisterSavedModel(modId, modelId, out status)` reuses a model this mod
  version exported in an earlier session, so the mod can skip building it.
- `TrySetPlacement(modId, placement, out status)` and `RemovePlacement` place a
  registered model in Unity world space.
- `TrySetTerrainPatch(modId, patchId, surfaces, out status)` and
  `RemoveTerrainPatch` replace edited ground with a world-space mesh.
- `RemoveLayer(modId)` withdraws everything at shutdown.

Expected rejections return false with a `MapLayerStatus`. IDs use lowercase
letters, digits, `_`, `-` and `.`, as the panel serves them. Only meshes and
textures created at runtime are exported: Unity gives objects loaded from game
or bundle files positive instance IDs, and those are skipped, so the game's own
assets never leave the server. Base-colour textures are kept when readable and
at most 2048 px.

Files go under the loader's `UserData/map/` (`Modstoke/UserData`, or
`OpenACAI/UserData` until Bootstrap has moved an older install). Nothing is
written per frame. A world load schedules a write after 20 s, a world save one
straight away and a change one after 10 s; writes run on a worker thread, stay
30 s apart and replace every file atomically, layers.json last. Multiplayer
clients never write. With nothing registered, layers.json still appears with an
empty `layers` array.

```json
{
  "version": 1,
  "written": "2026-11-08T14:20:31.512Z",
  "layers": [
    {
      "mod": "modstoke.client",
      "version": "0.2.31",
      "models": [
        { "id": "drone", "file": "models/modstoke.client/drone.glb",
          "bounds": [-0.31, -0.225, -0.29, 0.31, 0.06, 0.27] }
      ],
      "placements": "placements/modstoke.client.json",
      "terrain": []
    }
  ]
}
```

Folders use the lowercased manifest ID. Models are binary glTF 2.0 in metres:
positions, normals, optional UVs, vertex colours and base-colour textures, and
indices, 16-bit where possible, with exact duplicate vertices merged. Each
material has a linear `baseColorFactor`, metallic and roughness. Unity's
left-handed space is converted to glTF's right-handed one by negating Z and
reversing the winding. A model is written once per mod version. `bounds` are
`[minX, minY, minZ, maxX, maxY, maxZ]` in the GLB's own glTF space.

Placements stay in Unity world space; the panel applies the conversion, so a
position `(x, y, z)` becomes `(x, y, -z)` and a rotation `(x, y, z, w)` becomes
`(-x, -y, z, w)`:

```json
[
  { "id": "drone-3f2a9c0d4b1e4f6a8c7d2e1f0a9b8c7d", "model": "drone",
    "position": [512.4, 38.2, -1204.7], "rotation": [0, 0.3827, 0, 0.9239],
    "scale": [1, 1, 1], "label": "Survey drone", "kind": "drone" }
]
```

A placement that moved less than 10 cm and turned less than about two degrees
is not rewritten. Terrain patches are world-space GLBs plus a footprint PNG:
8-bit greyscale, 1 m per pixel over the patch's X and Z bounds, column 0 at the
west edge and row 0 at the north edge (lowest glTF Z). White pixels are those
whose centre the patch covers seen from above; the panel hides the base terrain
there. Patches may be at most 1024 m on a side. Placements and patches belong to
the current world and are cleared when another world loads; models stay
registered.

## Structure Activation And Debug Commands

`ServerInteractions` exposes three synchronous server callbacks:
`ActivatingItemPlatingStructure`, `ActivatingArtifactStructure` and
`ExecutingDebugCommand`. They precede the corresponding `CoopServerCallbacks`
receivers. `State.ItemPlatingStatus`, `ArtifactStatus` and `DebugCommandStatus`
report hook registration independently, with details, under
`openacai.sons.serverInteractions`. No client extension is required.

Requests inherit `InteractionRequest`: immediate source connection
ID, exact source-session handle when available, event-time registered Steam ID,
receipt time and callback-only `Reject()`. These observations do not establish
permission or successful execution. No native event, connection or entity is
retained by the request; the session handle retains its existing live semantics.

Both activation requests provide the raw `RequestedTargetNetworkId`, including
zero, and a non-null `TargetDescription`. The description independently records
the original getter, type-name and unmodified text outcomes. A readable managed
null differs from a failed getter; type/text failures remain unknown. Descriptions
are not entity identity or ownership. Artifact `RequestedActivate` is nullable:
the game reads it only after finding a live component, so an observation failure
does not become a new prerequisite for native handling.

Debug requests copy `Input` and `Input2` without normalization, each with a
separate read-success flag. Null can be a successful read. The raw lowercase
`target` network ID is optional and does not resolve a live entity. Native code
reads no payload when its console is absent; failed optional reads therefore
remain observations rather than an SDK command rule. Native method dispatch and
component behavior are unchanged when the request is allowed.

No subscribers means no payload reads or policy decisions. Client receivers and
already skipped originals remain untouched. A subscribed wrong-thread call,
required context failure or handler exception skips this receiver. `Reject()`
is irreversible and returns false after dispatch or on another thread. It does
not undo prior actions, refund resources, suppress unrelated listeners or prove
that a requested effect occurred. The SDK supplies no command allowlist, role,
range, resource or ownership policy. Live handling remains unverified.

## Zipline And Glider Policy

`ServerTransports.AttachingZiplineItem` and `HandlingGlider` run
synchronously before the server's `CoopServerCallbacks.OnEvent` receivers for
`AttachItemToZiplineEvent` and `PickupGlider`. `State.ZiplineStatus` and
`State.GliderStatus` independently report registration under
`openacai.sons.serverTransports`; they do not prove handling or enforcement.
No client extension is required.

Both requests inherit `TransportRequest`: immediate connection ID,
source-session handle when available, event-time game-registered Steam ID,
receipt time and callback-only `Reject()`. No native event or entity is retained.
These copies do not establish permission, ownership or a completed world change.

`ZiplineItemRequest` contains the raw `RequestedZiplineNetworkId`,
including zero, without resolving an entity or component. `RequestedItemId`,
`RequestedPosition` and `RequestedDirection` are nullable observations: null
means the individual read failed, not zero. Native handling reads those fields
only after finding a zipline component, so failed observations do not add an SDK
rejection rule. Values are not validated or normalized.

`GliderRequest.IsDrop` selects the native branch. Drop requires
`RequestedPrefabId`, `RequestedDropPosition` and `RequestedDropRotation`; pickup
requires `RequestedTargetNetworkId`. Those properties are nullable because reads
on the other branch are optional. `RequestedTargetVelocity` is always optional:
native drop reads it after instantiation and passes it to `AddForce`, then uses
its magnitude to select temporary drag. It is not measured velocity or a direct
velocity assignment. Pickup's native live/attached/local-owner checks remain
downstream; local ownership is not proof that the sender owns the entity.

`PrefabDescription` and `TargetEntityDescription` are non-null managed snapshots.
Each has `ValueReadSucceeded`, `IsNull`, `TypeName`, `TypeNameReadSucceeded`,
`Text` and `TextReadSucceeded`. A successful getter returning managed null is
distinct from an unreadable getter; `IsNull` is meaningful only when
`ValueReadSucceeded`. Type and unmodified `ToString` results are captured
independently. Text may successfully be null. Description failures remain
unknown and do not by themselves reject a request; neither text nor a raw
prefab ID establishes an item identity.

Without subscribers, native handling is unchanged. Client receivers and an
already skipped original are left alone. A subscribed wrong-thread callback,
required capture failure or handler exception skips only this receiver.
`Reject()` is irreversible and returns false after dispatch or on another
thread. It does not roll back prior actions, refund items, free event storage or
skip downstream validation when a request is allowed. The SDK adds no distance,
prefab, item or force limits. Live callback and gameplay acceptance remain open.

## World-Placement Policy

`ServerWorldPlacements.PlacingDryingFood` and `DroppingActor` run
synchronously before the server's `CoopServerCallbacks.OnEvent(PlaceDryingFood)`
and `OnEvent(DropVailActorEvent)` bodies. They provide typed
`DryingFoodPlacementRequest` and `ActorDropRequest`
copies. `State.DryingFoodStatus` and `State.ActorDropStatus` independently report
registration, not successful handling or enforcement. The state is also published
under `openacai.sons.serverWorldPlacements`. No client extension is required.

Both requests inherit `WorldPlacementRequest`: immediate transport
ID, exact source-session handle when available, event-time game-registered Steam
ID, requested position and rotation, receipt time and a synchronous `Reject()`
decision. Coordinates and rotations are not validated or normalized. These
observations do not establish ownership, permission or a completed world change.

Drying-food requests include the raw `RequestedPrefabId` and independently read
`RequestedPrefabIdTypeName` and `RequestedPrefabIdText`, each with its own
`ReadSucceeded` flag. These preserve the boxed prefab value's managed type name
and native `ToString` result for diagnostics; they are not item identity evidence.
Optional `RequestedParentNetworkId` is copied from the serialized ID without
resolving a live entity, and `RequestedDecayState` is the raw integer. Null means
that optional read failed; a zero parent ID remains zero. The inspected native
receiver uses prefab and pose only, ignoring parent and decay. Description or
optional-field failures remain unknown and do not by themselves reject a request.

Actor-drop requests include raw integer `RequestedTypeId`, `RequestedVariationId`,
`RequestedState` and `RequestedAreaMask`. The SDK adds no accepted-value lists or
distance limits. When allowed, the original receiver still performs its native
three-metre source-position check, type check and actor-spawn logic. Subscriber
approval is not a way to bypass those downstream checks.

Without subscribers, native handling is unchanged. Client handling and callbacks
already skipped by another prefix are left alone. A subscribed server callback
on the wrong thread, a required capture failure or a handler exception skips
only this receiver. `Reject()` is irreversible and returns false after dispatch
or on another thread. It does not undo earlier world or inventory changes, refund
items, free native event storage or suppress other listeners. No event or entity
wrapper is retained in these request copies.

## Actor-Item Policy

`ServerActorItems.Giving` and `Requesting` run synchronously before
the server's `CoopServerCallbacks.OnEvent(PlayerGiveActorItem)` and
`OnEvent(PlayerRequestActorItem)` bodies. Both expose an
`ActorItemRequest`; `Operation` distinguishes `Give` from `Request`.
`State.GiveStatus` and `State.RequestStatus` independently report hook
registration, not successful enforcement. Without subscribers, native handling
is unchanged. Client receivers are not subject to these policies.

The request copies the immediate transport source, source session,
game-registered Steam ID, raw `RequestedActorNetworkId`, `RequestedItemId` and
`RequestedAmount`. Zero and unresolved actor IDs are retained. These fields do
not establish ownership, distance, original authorship across relays or a
completed inventory transfer. `RequestedAmount` is the unmodified payload, not
a verified returned quantity. The mapped request-back receiver ignores that
amount and replies with one item. Its reply retains the request's raw item ID
but carries the returned item's token, so outfit replies can have different
raw and token item IDs. Neither field alone proves a successful transfer.

Give requests also include `HasItemInstanceToken`, nullable
`RequestedInstanceItemId`, and `RequestedItemInstanceTypeName` /
`RequestedItemInstanceText`, each description with its own `ReadSucceeded`
flag. Descriptions refer to the protocol wrapper, not its nested item instance.
Their managed-null and independent type/text semantics match item-grant
diagnostics below. The token getter and contained item-ID reads are required;
optional description failures remain unknown and do not reject a request.
Request-back events have no token field, so these properties remain false or
null. No native event, entity or token wrapper is retained by the request.

`Reject()` skips only this receiver, including its actor changes and replies.
It does not refund an item, undo earlier client inventory changes or suppress
other listeners. Native dispatch retains event-storage ownership. Decisions
are sticky and valid only during the synchronous callback on its dispatch
thread. With a subscriber, required context-read failures and handler
exceptions reject the callback. The SDK imposes no quantity, distance or
permission rule, and it requires no client extension. Live multiplayer
behavior remains unverified.

## Pickup And Loot Policy

`ServerPickups.Destroying` and `Disabling` run synchronously before
the server's `CoopPlayerCallbacks.OnEvent(DestroyPickUp)` and
`OnEvent(DisablePickup)` bodies. Their separate `State.DestroyStatus` and
`State.DisableStatus` values report hook registration, not successful enforcement.
Without subscribers, native handling is unchanged. Client receivers are not
subject to these policies.

Both typed requests copy the immediate transport source, source session,
game-registered Steam ID and raw `RequestedPickupNetworkId`, including zero or
unresolved IDs. `PickupDestroyRequest` also exposes
`ClaimedPlayerNetworkId`, `RequestedItemId`, `RequestedSiblingId` and
`RequestedFakeDrop`. `RequestedSiblingId` copies the game's `SibblingId` field.
`PickupDisableRequest.RequestedNum` is a sibling index, not quantity;
that event has no player field. These are unvalidated payload values, not proof
of ownership, distance or original authorship across forwarding.

Destroy requests copy pickup and claimed-player type names and text through
`RequestedPickupEntityTypeName` / `RequestedPickupEntityText` and
`ClaimedPlayerEntityTypeName` / `ClaimedPlayerEntityText`, each with its own
`ReadSucceeded` flag. Their managed-null and independent type/text semantics
match the item-drop diagnostics below. Optional description failures remain
unknown and do not reject a request.

Pickup `Reject()` skips only that receiver's body, including its cleanup or
forwarding. It does not undo earlier local inventory grants or stop other
listeners. Native dispatch retains event-storage ownership. Forwarded destroy
events can omit the claimed player and other original fields; missing values
alone do not establish spoofing.

`ServerLootSpawns.Receiving` instead runs before Bolt dispatch can
forward an exact native `SpawnLootItemEvent`. Its managed snapshot includes
source context, raw routing mode and destination IDs, plus `RequestedItemId`,
`RequestedPosition`, `RequestedRotation` and `RequestedDynamic`. There is no
quantity or spawned-object identity field. Server-local sends are included with
a null source and are allowed by default; no host-only rule is imposed by the
SDK. Rejection stops this dispatch and releases its unforwarded storage once.
It cannot undo a host-local spawn that preceded replication.

For both services, `Reject()` is sticky and only valid during the synchronous
callback on its dispatch thread. With an active subscriber, required context
read failures or handler exceptions reject the operation. Requests retain no
native payload wrappers; session metadata may change later. Live multiplayer
behavior remains unverified.

## Item-Grant Diagnostics

For `Operation.Add`, `ServerInventory.Receiving` requests include
`RequestedItemInstanceTypeName` / `RequestedItemInstanceText` and
`RequestedPlayerTypeName` / `RequestedPlayerText`, each with a corresponding
`ReadSucceeded` flag. The item description refers to the `PlayerAddItem`
protocol token, not its nested item instance. The player description refers to
the payload's resolved player wrapper, not the transport source.

Descriptions follow the managed-null and independent type/text semantics below.
Optional description failures do not reject dispatch. The existing required
`ItemInstance` getter, token identity and item-ID reads remain required; token
descriptions reuse that wrapper. The additional `Player` getter is optional.
For `Operation.Remove`, all four strings are null and their flags are false
because those fields do not apply.

These copied strings are unmodified and untrusted. They establish neither
ownership nor authenticated identity, and no native wrapper is retained.
Existing routing, rejection and storage-release behavior is unchanged.

## Item-Drop Diagnostics

`ServerItemDrops.Receiving` exposes copied request data before the
native server callback runs. `Handled` exposes a separate entry snapshot and
correlated factory results. Neither hook proves inventory ownership, successful
replication or permission to remove an entity.

Both `ItemDropRequest` and `ItemDropSource` include:

- `RequestedItemInstanceTypeName` and `RequestedItemInstanceText`
- `RequestedPreSpawnedTypeName` and `RequestedPreSpawnedText`

Each string has a corresponding `ReadSucceeded` property. A successfully read
managed-null object produces null strings with true flags. A failed ItemInstance
getter produces null strings with false flags. For nonnull objects, managed
`GetType().FullName ?? Name` and `ToString()` are captured independently;
a failed read does not discard the other value. A successful `ToString()`
returning null still has a true flag. Failed or unavailable diagnostics should
remain unknown when matching saved descriptions.

These are unmodified diagnostic strings, not item IDs or ownership evidence.
The SDK does not combine, normalize or truncate them, retain their native
objects, or reject a request because a diagnostic read failed. PreSpawned
diagnostics reuse the wrapper already read for the existing network-ID field;
required payload and network-ID read failures retain their existing behavior.
Observation `HasReadFailures` covers required context and result reads, not
these optional diagnostics. Policy decisions still close after synchronous
dispatch, and rejection does not refund inventory or delete an existing entity.

## Free-Form Request Context

`ServerFreeForm.Receiving` keeps its existing server-side dispatch
through the shared base proxy. `FreeFormRequest.IsServerReceiver`
reports whether the actual callback instance was a live native
`FreeFormSystemNetworkProxyServer` when captured. Check `ReceiverReadSucceeded`
first: false means unavailable classification, not a known client receiver.
A live nonserver proxy produces false with a successful read.

`RequestedPickupEntityTypeName` / `RequestedPickupEntityText` and
`ClaimedPlayerEntityTypeName` / `ClaimedPlayerEntityText` copy the request's
`PickupEntity` and `SendingPlayerEntity` descriptions. Each has a corresponding
`ReadSucceeded` flag, with the same managed-null and independent type/text
semantics as item-drop diagnostics. No native entity is retained for these
descriptions. They are unmodified, untrusted text, not item ownership or
authenticated player identity.

Optional receiver or description failures do not abort dispatch. Existing
required payload reads, geometry resolution, decision lifetime and explicit
native-feedback behavior are unchanged. The mapped game's `FromSelf` check is
equivalent to a null `RaisedBy`; the existing null source-connection context
continues to represent that case.

## Source Files

Use the [Core source guide](../src/Modstoke.Loader/README.md) to
find an implementation. The main entry points include:

| Area | Source |
| --- | --- |
| Loader state | [Modding.cs](../src/Modstoke.Loader/Hosting/Modding.cs) |
| Game lifecycle | [Sons.cs](../src/Modstoke.Loader/Sons/Runtime/Sons.cs) |
| Assets | [Assets.cs](../src/Modstoke.Loader/Sons/Runtime/Assets.cs) |
| Building | [Building.cs](../src/Modstoke.Loader/Sons/Building/Building.cs) |
| Crafting | [Crafting.cs](../src/Modstoke.Loader/Sons/Crafting/Crafting.cs) |
| Inventory | [Inventory.cs](../src/Modstoke.Loader/Sons/Inventory/Inventory.cs) |
| Item registration | [ItemRegistration.cs](../src/Modstoke.Loader/Sons/Inventory/ItemRegistration.cs) |
| Items | [Items.cs](../src/Modstoke.Loader/Sons/Inventory/Items.cs) |
| Networking | [Networking.cs](../src/Modstoke.Loader/Sons/Networking/Networking.cs) |
| Save data | [SaveData.cs](../src/Modstoke.Loader/Sons/Saves/SaveData.cs) |
| Save lifecycle | [SaveLifecycle.cs](../src/Modstoke.Loader/Sons/Saves/SaveLifecycle.cs) |
| Save registration | [SaveRegistry.cs](../src/Modstoke.Loader/Sons/Saves/SaveRegistry.cs) |
| Map layers | [MapLayers.cs](../src/Modstoke.Loader/Sons/World/MapLayers.cs) |

`Modstoke.Loader.Modding` exposes loader state:

- loader version and API version
- startup readiness and failure status
- BepInEx, RedLoader, and SonsSdk compatibility shim health
- Modstoke-native versus legacy compatibility source-family data in mod
  inventory snapshots
- OpenACAI-owned keybind registration and context gating through `InputBindings`,
  with pressed/held/released polling, live `SetKey`, and `TryUnregisterKeybind`
- file-backed config parsing and saving helpers through `LoaderConfig`
- structured OpenACAI config providers through `IConfigProvider`
- renderer-side editing for structured config providers in the native and
  fallback Mods menu paths
- mods menu/config UI surface state through `UiState`
- public menu provider registry state through `MenuRegistry`
- safe command providers through `ICommandProvider`
- hosted RedLoader mod count
- safe runtime paths
- mod inventory snapshots
- loader lifecycle, scene, inventory, and startup warning events
- a small service registry for OpenACAI-provided runtime services

`Modstoke.Sons.GameRuntime` exposes Sons-specific state:

- current scene name, build index, and scene kind
- title/loading/game scene classification
- in-world state from `TheForest.Utils.LocalPlayer.IsInWorld`
- scene initialization on a later Unity frame, cancelled if that scene unloads;
  the initial-scene probe is skipped when a load event already arrived. The
  scene snapshot tracks the newest load, so delayed initialization or unload
  notifications for other scenes do not replace it. World presence is observed
  independently rather than inferred from title-scene events.
- pause menu known/open state
- lifecycle, scene, world, and pause menu event counters plus the last
  lifecycle event name, with a DIAGNOSTICS status provider and optional
  `sons-runtime-state` policy row
- OpenACAI-owned environment paths for game root, Unity data, persistent data,
  save data, native Modstoke folders, and legacy compatibility folders, with a
  DIAGNOSTICS status provider and optional `sons-environment` policy row
- per-mod save-data paths under `Modstoke/UserData/Saves/Mods`
  through `SaveData`, with a DIAGNOSTICS status provider and
  optional `sons-save-data` policy row
- OpenACAI-owned save/load lifecycle events from `SaveGameManager.Load` and
  `SaveGameManager.Save` through `SaveLifecycle`, with a
  DIAGNOSTICS status provider and optional `sons-save-lifecycle` policy row
- per-mod custom sidecar JSON save registration through
  `SaveRegistry`, with a DIAGNOSTICS status provider and
  optional `sons-save-registry` policy row
- serializer storage intent through
  `SaveStorageMode.SidecarJson`, `EmbeddedPreferred`, and
  `EmbeddedRequired`, plus `RegisterEmbeddedJson<T>` and the optional
  `sons-save-serializer-routing` policy row
- per-mod asset and audio paths under `Modstoke/UserData` through
  `Assets`, including supported-file discovery plus safe
  AssetBundle load/unload helpers and non-blocking AudioClip load/unload
  helpers, with per-request failure isolation and retained registrations when
  unloading fails (runtime operations belong on the Unity lifecycle thread)
- owned synchronous FMOD bank metadata loading, optional strings-bank ownership,
  immutable event GUID/path enumeration, balanced sample preloading with explicit
  readiness queries, and unloading through
  `AudioBanks`; runtime acceptance remains pending
- owned one-shot and retained event playback, explicit stop/release, volume,
  pitch, parameter and spatial-attribute controls; successful command submission
  is not in-game audio acceptance
- OpenACAI-owned blueprint roots, custom blueprint registration, and building
  placement planning through `Building`
- read-only item metadata snapshots through `Items`; native capture
  and uncached ID lookup require the initialized Unity lifecycle thread, while
  cached lookup/search/filter operations use managed snapshots only
- custom item definition planning and registration intent through
  `ItemRegistration`
- LocalPlayer/inventory readiness and inventory action planning through
  `Inventory`
- live item quantity, ownership and equipped queries through
  `TryGetItemCount`, `TryHasItem` and `TryIsItemEquipped`, on the initialized
  Unity lifecycle thread with an in-world LocalPlayer inventory
- crafting plan validation and execution gating through
  `Crafting`
- declared game-recipe metadata through
  `Crafting.TryFindRecipesForResult`, on the initialized Unity
  lifecycle thread with an in-world local player; runtime acceptance pending
- multiplayer/session readiness and message-channel planning through
  `Networking`
- Sons lifecycle, scene, world, and pause menu events

The live Sons state, environment, item catalog, item-registration gate,
inventory readiness, crafting, building, networking, save-data, save lifecycle,
and save registry services are also registered as:

```text
openacai.sons.state
openacai.sons.environment
openacai.sons.assets
openacai.sons.building
openacai.sons.items
openacai.sons.itemRegistration
openacai.sons.inventory
openacai.sons.crafting
openacai.sons.networking
openacai.sons.saveData
openacai.sons.saveLifecycle
openacai.sons.saveRegistry
```

## Remote companion requests

`ServerCompanions.Receiving` runs before server dispatch of remote
`RobbyOrderEvent` requests. Its copied fields describe Kelvin interaction attempts,
including opening or closing the order pad, giving orders and helping him up.
Server-created notifications and direct local calls retain native handling.

During the synchronous callback, `Reject()` skips this dispatch. Calling
`RequireValidNativeRequest()` also requires an exact current source/player binding,
a live server-owned Kelvin target, server-only routing, a supported event kind
and valid order selection. Invalid requests are rejected. Required checks repeat
after subscribers return, retaining the same player and target attachments.
`RejectInvalidNativeRequest()` runs the same checks but rejects only an invalid
verdict (`CompanionRequest.IsInvalidVerdict`): a player mismatch, a target the
server does not own, or invalid routing, event, order or subtype. Unavailable,
changed or failed reads leave native handling, which suits servers where some
reads are not qualified. Routing, event, order-type and drop-location ranges are
checked from the copied request before any read, so an unavailable read never
hides them; the player, target and order-index verdicts need those reads.
`CheckNativeRequest()` returns the status without deciding anything.
No subscribers, or subscribers that request neither check nor rejection, leave
native handling unchanged. Keep handlers bounded; reentrant remote dispatch and
handler exceptions are denied. Decisions close when the callback ends.

Order checks use the target's live order lists before native cleanup. Get also
checks the drop-location enum; MaintainBase checks entry zero, which the native
handler selects regardless of subtype. Empty or unavailable lists cannot pass.
The SDK does not change ignored subtype fields or call native refusal methods.

These checks establish request structure and connection binding, not Steam
authentication, proximity, interaction ownership, resource delivery or job
completion. Injuries and native action arbitration still belong to the game.
This request API has no job queue or cancellation API, and makes no claim of
protection against direct component calls. Hook registration and compilation do not
establish listen-host or dedicated multiplayer acceptance.

## Companion activity observations

`ServerCompanions.Observed` reports copied state before and after native
Kelvin order selection, order execution setup, Follow, completion and refusal
methods return. `ObservationState` reports this hook group's registration separately
from remote request protection. Observers cannot reject a transition. Exceptions
are contained, and transitions caused inside an observer are not published again.

Each observation retains its original server-owned entity attachment. If that
context disappears during the method, `After` is null and `AfterStatus` explains
the failed read. This differs from a successful snapshot whose `ActiveOrder` is
null. Native method exceptions and calls skipped by another prefix produce no
return observation. Missing observations do not establish continued execution.

Order references can be compared with `RefersToSameOrder` without exposing native
objects or pointer values. They identify a captured lifetime, not a persistent job
ID or current authority. Snapshots include the stored order, completion-callback
flag, raw injury state and observed override references. They do not invoke native
admission queries, change actions or poll every frame.

Native completion can follow missing holders, unavailable work or expiry. Rest
preserves an order, Follow can retain an older order, and a completion callback
can precede the terminal transition. Consumers must not promote these observations
to successful delivery, cancellation or safe automatic admission of another job.
They cover the listed method boundaries, not every death, interruption, direct
field write or world transition. Native behavior remains subject to multiplayer
qualification.

## Companion job queues

`CompanionJobs` is a separate transient server API for queuing enabled
native Kelvin Get orders. It binds each queue to a current player, world and
companion lifetime, waits for a native idle boundary and preserves native action
arbitration. Existing request and observation APIs remain unchanged.

Queues allow eight pending jobs, four open queues per server and 32 retained
receipts per queue. Waiting jobs expire after one hour. Rest/restart can retain a
submitted job indefinitely; a successor waits. `NativeEnded` means only that the
tracked native order ended, with delivery and success unknown. Cancellation only
removes waiting entries; closing or retiring a queue does not cancel running work.
There is no persistence, automatic objective progress or economy reward.

See [Companion job queues](COMPANION_JOBS.md) for the API, authorization callbacks,
exact lifetime boundaries, receipt meanings and pending runtime qualification.
The SDK supports current remote player bindings as well as host
bindings; Admin's command is currently listen-host only.

## Server printer requests

`ServerPrinters.Receiving` offers synchronous rejection at three
native boundaries: recipe/start event dispatch, local laptop `StartPrint(true)`,
and the local recipe-cycle callback. Event dispatch is gated before forwarding
and local handling. Local laptop calls are gated before their effects. A handler
exception also denies the invocation. `Reject()` is sticky and works only on the
callback thread before the decision window closes. Unsubscribe at shutdown.

The copied request exposes its operation, origin, target network ID, optional
recipe ID, optional routing targets and observation time. These are request
claims, not validated printer state or an operation receipt. `SourceSession`
binds a remote event to its exact current connection. Server-local dispatches
and laptop invocations have no authenticated player identity; neither a null
source nor `updateNetwork=true` establishes that the host initiated an operation.
Use current transport identity and server policy when granting account access.

Local recipe cycles expose no proposed recipe ID: native code computes it after
the hook. Local starts can also produce a later server-local dispatch; these are
separate decisions, not two completed jobs. Reentrant printer operations from a
policy handler are denied. Keep handlers bounded and avoid native printer calls.

Vanilla clients select and start locally before sending these events. Rejecting
the server route cannot undo that prediction, refund resources or cancel a job
already started. The hooks do not cover `StartPrint(false)` invoked directly,
the printer's float overload, general recipe setters, resin transfers, plate
pickups or completion. Native save/network restoration uses those separate paths
and remains unchanged. Do not infer a completed production transaction from an
accepted request.

`State.Status == Installed` means every hook registered. Partial installation
stays inactive. Native behavior is unchanged without subscribers, on clients,
and during offline play. Dedicated scene objects and callback registration require
qualification; compilation does not establish multiplayer enforcement.

## Named world-state requests

`ServerNamedStates.Changing` observes `ChangeIdObjectState` at server
dispatch before forwarding or native application. It exposes the raw index,
requested value and routing targets, the registered name resolved at that index,
and the exact current source session when remote. A synchronous `Reject()` skips
that event. Local dispatch has no authenticated player identity.

`TryReadRegistry` returns a bounded, copied list of current native indices and
names on the server game thread. Persist names when defining policies; indices
are temporary mappings. Registered names include doors and other world state.
Neither a name nor an accepted request proves a live door, valid state
transition, keycard ownership or successful native completion.

See [Named world-state access](NAMED_STATE_ACCESS.md) for callback lifetime,
registry limits, native coverage and the limits of vanilla-client enforcement.

## Server voice routing

`ServerVoice.Routing` exposes a synchronous decision for each native
voice recipient, including the listen host's local playback. A handler can call
`Reject()` to withhold that delivery; a handler exception also denies it. The
request contains player attachment handles, positional/radio mode, payload size
and receive time. It contains no audio bytes and is not a delivery receipt.

Subscribing activates the server routing path. Incoming voice must use the
registered Voice channel, have a canonical envelope and name the actual player
attached to the exact source connection. Current producer frames are limited to
1180 compressed bytes. This check runs before the native voice allocation, but
cannot prevent allocations already made by the underlying UDP stream reader.

The SDK reuses the game's proximity/area enumerator and radio player collection,
captures at most 256 candidates, and checks each exact recipient immediately
before native sending. An oversized candidate set is dropped before voice sends.
This replaces the native forwarding loop's unconditional local-player lookup,
allowing the code path to operate without a dedicated server's local player.
Native talking-monitor callbacks remain in place. Listen-host playback has a
separate policy boundary before decoding; denying a network send alone would
not suppress that local playback. Already queued audio cannot be recalled.

```csharp
// Register once during initialization and remove the same handler at shutdown.
ServerVoice.Routing += BlockRadio;

static void BlockRadio(VoiceDeliveryRequest request)
{
    if (!request.IsPositional)
        request.Reject();
}
```

The participant's `Session` identifies an exact remote connection lifetime;
`LocalPlayer` represents the actual listen host. Neither grants permissions or
proves client integrity. Use supported transport identity and current server
policy for account-based permissions. Do not infer the host from a null remote
source, a claimed account or a network identifier alone.

`State.Status == Installed` reports registration, not observed multiplayer
success. A partially installed group stays inactive. Without subscribers the
SDK leaves native behavior unchanged. The normal client capture, push-to-talk,
native decoding and local debug recording paths remain native. This controls
the inspected server relay and its playback, not other applications or arbitrary
networking installed by peers. Private channels, proximity behavior and dedicated
operation still require multiplayer qualification before relying on them.

## Incoming Chat Policy

`ServerChat.Receiving` handles game chat and negotiated client
commands. Check `State.RoutingStatus` before relying on game-chat rejection
before forwarding. `Installed` means the dispatcher hook registered, not that
muting has been verified with connected clients. `State.Status` continues to
describe the callback hook; callback-only policy cannot cover every native
forwarding route. `RoutingDetail` explains registration failures.

When routing is installed, remote `ChatEvent` policy runs before the dispatcher
routes the event. Rejection or a consumed SDK command stops that dispatch and
releases its storage. Approved events retain native routing and validation;
their server callback does not execute policy or commands again. Server-local
chat retains callback handling. A native rebroadcast is not attributed to a
remote player using its claimed Sender field.

Negotiated protocol frames stay with the notice dispatcher. A negotiated command
still enters policy as `ClientCommand`, with its existing session identity and
private reply path. Rejection is synchronous and irreversible; it does not remove
messages already displayed or forwarded before this server received the event.

## Server Tree Requests

`ServerTreeSpawns.Receiving` runs synchronously before
`CoopServerCallbacks.OnEvent(SpawnCutTree)`. A policy handler can call `Reject()`
to skip that callback. Rejection is irreversible and only accepted on the
dispatch thread before dispatch finishes. A handler exception also rejects
the request. With no subscribers, the hook leaves the callback unchanged.

`TreeSpawnRequest` contains copied position, rotation, force, force
position, cut-prefab ID and uniform scale, plus the source connection/session,
event-time registered Steam ID and receipt time. These are observations, not
proof of authentication, tree existence, permitted placement or legitimate damage.
The request retains no native event object. Its `SourceSession` remains a live
connection-lifetime handle, not an event-time identity snapshot.

`RequestedRawGuid` is the native GUID string when readable.
`RequestedGuidReadSucceeded` distinguishes native read failure from a returned
value; `RequestedGuidParsed` separately reports managed parsing.
`RequestedGuid` is canonical `D` format when parsed, otherwise null.
An all-zero GUID is a valid parsed value, not a read failure.

`State.Status` (`TreeSpawnHookStatus`) and the
`openacai.sons.serverTreeSpawns` service report hook installation. `Installed`
does not establish that a request has been observed or blocked in a live session.
Rejection does not undo prior locator updates, restore a tree, refund a client
or reverse another callback's effects. A destroyed locator is not sufficient
evidence of an invalid spawn: normal felling can update locator state before
sending this event. Admin policy and sanctions belong to the consuming mod.

## Example

```csharp
using Modstoke.Loader;
using Modstoke.Sons;

Modding.Lifecycle += evt =>
{
    // LoaderReady, LoaderFailed, etc.
};

GameRuntime.WorldChanged += evt =>
{
    if (evt.IsInWorld)
    {
        // Safe place for world-aware logic.
    }
};

InputBindings.TryRegisterKeybind(
    "example.mod.quickAction",
    "Example Quick Action",
    "Example Mod",
    UnityEngine.KeyCode.F8,
    InputContexts.InWorld,
    suppressWhenModstokeUiVisible: true,
    "Run an Example Mod action only while the player is in world.");

if (InputBindings.IsPressed("example.mod.quickAction"))
{
    // In-world and UI-suppression gates have already been checked.
}

if (Modding.TryGetService("openacai.sons.state", out State state)
    && state.IsInWorld)
{
    // The player is currently in the world.
}

var env = GameRuntime.Environment;
var gameSaveRoot = env.SaveDataDirectory;
var itemCatalog = ItemCatalog.Catalog;
if (ItemCatalog.TryFindItem(618, out var printedArrow))
{
    // Read-only metadata: id/name/type/UI title/spawn flags/tags.
}

var arrows = ItemCatalog.SearchItems("arrow", maxResults: 16);
var hotkeyableWeapons = ItemCatalog.FindItems(new ItemQuery
{
    Type = "Weapon",
    CanBeHotKeyed = true,
    MaxResults = 24
});

var customItemPlan = ItemRegistration.PlanCustomItemRegistration(
    "ExampleMod",
    itemId: 900001,
    itemKey: "example-token",
    displayName: "Example Token");
if (!customItemPlan.CanRegisterNow)
{
    // Custom item definitions are validated and tracked by OpenACAI now; item
    // database mutation remains gated until the runtime registration path is
    // verified.
    var reason = customItemPlan.ReasonCode;
}

ItemRegistration.TryRegisterItemDefinition(
    "ExampleMod",
    itemId: 900001,
    itemKey: "example-token",
    displayName: "Example Token",
    out var exampleItemDefinition);

// Call from a Unity lifecycle callback. A failed read is distinct from count=0.
if (Inventory.TryGetItemCount(507, out var arrowCount, out var inventoryStatus))
    Console.WriteLine($"Arrows: {arrowCount}");
else
    Console.WriteLine(inventoryStatus);

var addArrowsPlan = Inventory.PlanAddItem(507, quantity: 5);
if (!addArrowsPlan.CanExecuteNow)
{
    // Current builds validate the item/action intent and report why execution
    // is deferred instead of mutating inventory before the path is verified.
    var reason = addArrowsPlan.ReasonCode;
}

// Reads declared results, not current craftability or generated output.
if (Crafting.TryFindRecipesForResult(507, out var recipes, out var recipeStatus))
{
    foreach (var recipe in recipes)
        Console.WriteLine($"{recipe.Name}: {recipe.Ingredients.Count} ingredient entries");
}
else
    Console.WriteLine(recipeStatus);

var craftPlan = Crafting.PlanRecipe(
    "example.arrow.bundle",
    resultItemId: 507,
    quantity: 5,
    new CraftingIngredient(618, quantity: 5));
if (!craftPlan.CanExecuteNow)
{
    // Crafting plans validate result/ingredient ids now; execution remains
    // gated until the in-world crafting path is verified.
    var reason = craftPlan.ReasonCode;
}

if (Networking.TryRegisterMessageChannel(
    "ExampleMod",
    "sync",
    "Example Mod state sync channel.",
    out var syncChannel))
{
    var networkPlan = Networking.PlanMessage(
        "ExampleMod",
        "sync",
        NetworkTarget.Everyone,
        payloadBytes: 128);
    if (!networkPlan.CanSendNow)
    {
        // Message intent is validated now; transport remains disabled until
        // the OpenACAI Bolt packet path is verified.
        var reason = networkPlan.ReasonCode;
    }
}

var exampleBlueprintRoot = Building.EnsureModBlueprintDirectory("ExampleMod");
var vanillaBlueprintPlan = Building.PlanBlueprintItem(638);
if (!vanillaBlueprintPlan.CanPlaceNow)
{
    // Blueprint item and custom blueprint intent can be validated now; runtime
    // placement/book mutation remains gated until the construction path is
    // verified.
    var reason = vanillaBlueprintPlan.ReasonCode;
}

if (Building.TryRegisterBlueprint(
    "ExampleMod",
    "demo-cabin",
    recipeId: 900001,
    displayName: "Demo Cabin",
    sourceRelativePath: "demo-cabin.glb",
    out var demoBlueprint))
{
    var buildingPlan = Building.PlanCustomBlueprint(
        "ExampleMod",
        demoBlueprint.BlueprintId);
}

var exampleDataRoot = SaveData.EnsureModDirectory("ExampleMod");
var exampleSaveFile = SaveData.GetModFilePath("ExampleMod", "state.json");
var exampleAssetRoot = Assets.EnsureModAssetDirectory("ExampleMod");
var exampleBundle = Assets.GetModAssetFilePath("ExampleMod", "bundles/example.bundle");
var exampleAudioRoot = Assets.EnsureModAudioDirectory("ExampleMod");
var exampleClip = Assets.GetModAudioFilePath("ExampleMod", "clips/example.ogg");
if (Assets.TryLoadAssetBundle(
    "ExampleMod",
    "bundles/example.bundle",
    out UnityEngine.AssetBundle loadedBundle,
    out AssetLoadResult loadResult))
{
    // Use loadedBundle, then unload through OpenACAI when the mod is done.
    Assets.TryUnloadAssetBundle(
        "ExampleMod",
        "bundles/example.bundle",
        unloadAllLoadedObjects: false,
        out _);
}

if (Assets.TryBeginLoadAudioClip(
    "ExampleMod",
    "clips/example.ogg",
    out var audioLoad))
{
    // The request is pumped by the OpenACAI runtime driver. Poll later, then
    // play through your own AudioSource or other Unity audio flow.
    if (Assets.TryGetLoadedAudioClip(
        "ExampleMod",
        "clips/example.ogg",
        out UnityEngine.AudioClip loadedClip,
        out _))
    {
        Assets.TryUnloadAudioClip("ExampleMod", "clips/example.ogg", out _);
    }
}

SaveLifecycle.Lifecycle += evt =>
{
    if (evt.Operation == "Save" && evt.Phase == "After")
    {
        // Save-entrypoint postfix notification, not proof of durable game-save IO.
    }
};

var registration = SaveRegistry.RegisterJson(
    "ExampleMod",
    "settings",
    () => new ExampleSaveData { Enabled = true },
    data =>
    {
        // Restore sidecar data only after the native load returned true.
    });

var embeddedPreferredRegistration = SaveRegistry.RegisterEmbeddedJson(
    "ExampleMod",
    "world-state",
    () => new ExampleSaveData { Enabled = true },
    data =>
    {
        // Embedded intent is declared, but current builds safely fall back to
        // OpenACAI sidecar JSON until the Sons save-file hook is verified.
    });

MenuRegistry.RegisterOrUpdateProvider(new MenuProviderDescriptor(
    "example.mod.settings",
    "Example Settings",
    MenuProviderKinds.Settings,
    500,
    "Example Mod",
    "Expose Example Mod settings through the Modstoke Mods menu.",
    false,
    true));
```

`SaveLifecycleEvent.Success` is populated only after a native load returns.
`true` means the native load accepted its input; it does not establish that every
subscriber restored successfully. The registry skips restoration when the native
load returns `false`. Save notifications have no success verdict because the
native save entry point returns no result.

Sidecar writes flush a temporary file before replacing the previous file. This
protects an individual sidecar from a partial overwrite, but does not make the
native save and Modstoke records one transaction. The existing registry also does
not establish durable world identity; new ownership systems need an explicit
world binding before using it for permissions or resources.

`WorldRecords` provides a separate native world-save path. On the
server's game thread, acquire a context with `TryGetCurrent`, then use `ReadJson`,
`WriteJson` or `Remove` with a stable owner/name pair. A successful write changes
memory only. The native `ModstokeWorld` serializer captures records in the same
world archive as the game's serializers; `LastSaveStatus.Serialized` does not
prove that the archive reached disk successfully. Player-only saves exclude it.

The embedded versioned envelope carries a persistent world GUID. Loading a save
creates a new live context, even if its GUID matches a previous load; old handles
are rejected. Saving a copy of a world retains its GUID and records. New worlds
receive a fresh GUID. Scene changes, world exit and save-manager replacement
invalidate the live context. The public API requires server ownership, but the
native serializer also preserves records when that world is used in single player.

Records are UTF-8 JSON, at most 512 KiB each, with 128 records and a 4 MiB encoded
envelope limit. Owner and name accept lowercase ASCII letters, digits, `.`, `_`
and `-`, up to 96 characters each. Mods own payload schema validation and player
permissions; this API is not a security boundary between in-process mods.

A successful native load alone does not prove that a custom entry was read.
When the callback is absent, the SDK also inspects the ZIP and loose-file
fallbacks before treating a world as having no records. Unreadable or ambiguous
input stays unavailable. Inspection of the shared main envelope is bounded at
64 MiB. Unknown inner record formats are retained unchanged; a world save is
refused if existing records cannot be retained safely. `LastProblem` explains
the unavailable state without exposing record contents.

This does not migrate or replace the older sidecar registry or the legacy
`Mod` serializer. Back up and restore the complete native world save, including
its Modstoke entry. The native registration and serialization path has source
evidence and compile coverage; listen-host/dedicated save, reload and recovery
behavior still requires gameplay acceptance.

Structured config providers can publish typed settings without depending on
RedLoader's config system. Registered providers are surfaced in
`openacai.loader.config` and mirrored into the Mods menu nav through the
OpenACAI menu provider model:

```csharp
public sealed class ExampleConfigProvider : IConfigProvider
{
    public string Id => "example.config";
    public string DisplayName => "Example Config";
    public string Source => "Example Mod";
    public int Priority => 500;
    public bool Enabled => true;

    public IReadOnlyList<ConfigValueSnapshot> GetSettings()
    {
        return new[]
        {
            new ConfigValueSnapshot(
                "General",
                "enabled",
                "Enabled",
                ConfigValueKinds.Boolean,
                "true",
                "true",
                "Enable this feature.",
                true,
                false)
        };
    }

    public bool TrySetValue(string key, string value, out string status)
    {
        status = "Applied.";
        return true;
    }
}

LoaderConfig.RegisterOrUpdateProvider(new ExampleConfigProvider());
```

The native and fallback Mods menu renderers list config-backed providers in the
nav, under the owning mod, under LOADER for the loader's own, or else under MOD
PAGES, and render editable controls for booleans, choices, text, and numeric
values. User edits call the provider's `TrySetValue` method through
`LoaderConfig.TrySetRegisteredValue`; renderers do not own provider
persistence. The built-in `Runtime Preferences` provider is the live loader
smoke-test surface for this path and stores OpenACAI-owned preferences under
`Modstoke/UserData`.

Providers that need an in-game detail page can implement
`IMenuContentProvider`. The content model is deliberately Unity-free so
mods can publish status/settings rows without binding to the current renderer:

```csharp
using Modstoke.Loader;

public sealed class ExampleStatusProvider : IMenuContentProvider
{
    public string Id => "example.mod.status";
    public string DisplayName => "Example Status";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 520;
    public string Source => "Example Mod";
    public string Description => "Shows Example Mod runtime status.";
    public bool RequiresAuthorization => false;
    public bool Enabled => true;

    public MenuContentSnapshot CreateContent()
    {
        return new MenuContentSnapshot(
            DisplayName,
            Description,
            new[]
            {
                new MenuSectionSnapshot(
                    "Runtime",
                    new[]
                    {
                        new MenuRowSnapshot("Loaded", "Yes", "READY", "Plugin startup completed."),
                        new MenuRowSnapshot("Mode", "Client", "INFO", "Current Example Mod mode.")
                    })
            });
    }
}
```

Safe menu actions can be exposed without binding to Unity UI by implementing
`ICommandProvider`. The native and fallback Mods menu renderers show
command rows and invoke enabled commands that do not require confirmation:

```csharp
public sealed class ExampleCommandProvider : ICommandProvider
{
    public string Id => "example.mod.commands";
    public string DisplayName => "Example Commands";
    public string Kind => MenuProviderKinds.CommandGroup;
    public int Priority => 530;
    public string Source => "Example Mod";
    public string Description => "Runs safe Example Mod actions.";
    public bool RequiresAuthorization => false;
    public bool Enabled => true;

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        return new[]
        {
            new CommandSnapshot(
                "refresh",
                "Refresh",
                "Refresh Example Mod runtime state.",
                "READY",
                true,
                false)
        };
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        result = CommandResultSnapshot.Success("Example Mod refreshed.");
        return true;
    }
}
```

## Build Note

The current preview branch should be built with:

```powershell
dotnet build src\Modstoke.Loader\Modstoke.Loader.csproj --configuration Release --framework net11.0
```

The compatibility bridge targets `net11.0` on `openacai-net11-preview`. Use
`openacai-net10-port` for the current LTS runtime line, and archived legacy
packages/branches if a .NET 6 comparison build is needed for diagnostics.

## Target Framework Policy

Sons Of The Forest reports Unity `2022.2.16f1`. Microsoft's .NET Standard table
maps `netstandard2.0` to Unity `2018.1` and `netstandard2.1` to Unity `2021.2`,
so `netstandard2.1` is the better compatibility target for Sons-specific
Modstoke SDK abstractions.

- Use `net11.0` on the preview branch and `net10.0` on the LTS branch for
  OpenACAI-owned runtime implementation assemblies.
- Use `netstandard2.1` for public Sons SDK abstraction packages that should feel
  native to modern Unity/Sons mod development.
- Keep `netstandard2.0` only for deliberate backward compatibility shims or
  upstream BepInEx compatibility surfaces.
- Do not add new OpenACAI-owned code to `net35` or `net481`. `net481` is still
  .NET Framework, while the Modstoke Loader implementation runs on the
  modern `.NET 10/11` CoreCLR runtime started through hostfxr.

## Rewrite Roadmap

Good next modules to reimplement from the SonsSdk feature map:

1. Done: input and keybind registration with explicit scene/world/UI gating.
2. Done: sidecar JSON save registry and lifecycle status providers.
3. Done: environment, runtime-state, and save-data status providers.
4. In progress: `WorldRecords` adds a world-bound native serializer;
   observed save, reload and recovery acceptance remains pending. Existing
   sidecar registrations retain their original storage behavior.
5. In progress: item, inventory, and crafting helpers. Item catalog/query and
   inventory readiness are available, along with live count/ownership/equipped
   reads (runtime acceptance pending). Catalog capture now uses typed bindings
   and bounded IL2CPP enumeration, including native tag enumerator disposal;
   off-thread refreshes return a failed result without replacing the catalog.
   Custom item definition planning now
   validates registration intent and catalog collisions, inventory action
   planning validates common item operations, and crafting plan validation
   checks result/ingredient item ids. Crafting recipe reads return all matches
   for declared result item IDs, preserving ingredient counts/reusable flags
   and result entries without predicting actual output or craftability.
   Runtime mutation/execution remains disabled until the in-world paths are
   verified; the new recipe reads also await runtime acceptance.
6. In progress: sound and asset loading helpers with deterministic paths and
   cleanup. AssetBundle load/unload and non-blocking AudioClip load/unload
   helpers are available through OpenACAI. Owned synchronous bank loading,
   strings-bank lifetime and event metadata enumeration are implemented; actual
   FMOD bank behavior remains unverified. One-shot/retained event playback and
   instance controls are implemented but require in-game acceptance. Explicit
   sample preload/release and readiness queries are implemented; accepted requests
   and last-observed loading state are tracked separately, not treated as proof
   of audibility or current readiness.
7. In progress: multiplayer-safe networking helpers that do not require
   RedLoader globals. Networking readiness, channel registration, and message
   intent planning are available; packet/chat transport remains disabled until
   the Bolt path is verified.
8. In progress: building/blueprint helpers. OpenACAI-owned blueprint roots,
   mod blueprint directories, custom blueprint registration, blueprint file
   discovery, and placement plan validation are available; runtime placement
   and blueprint book mutation remain disabled until the in-world construction
   path is verified.

Avoid per-frame heavy scans, broad Harmony patches at loader startup, and static
global state that cannot be reset after returning to the main menu.

The first public UI registry slice exists as `Modstoke.Loader.MenuRegistry` for
menu tabs, settings, safe actions, and protected-server status. Built-in
Mods/config/credits pages and the private admin panel should both use that
provider model. The native and fallback menus take filter labels and tooltips
from provider metadata, and their nav lists built-in settings and status
providers under LOADER and DIAGNOSTICS beside external providers. Each provider
opens a structured page in both renderers. Config-backed providers render
editable settings controls and apply through
`LoaderConfig.TrySetRegisteredValue`. Command-backed providers render safe
action rows and invoke enabled, no-confirmation commands through
`MenuRegistry.TryInvokeProviderCommand`.


## Native NPC search policy

`ServerNpcSearch.Considering` offers a synchronous server policy
window for the stock `CombatLostSightOfEnemy` / `RunToAndSearch` thought. It runs
only after native `CanRun` accepts a lost-sight memory and its conditions, within
`Group.TryRunThought`. A policy may defer that candidate; the native loop then
continues selection. It cannot submit an action or make a rejected native
candidate pass. Sequence continuations and muted-condition calls are excluded.

A request copies the actor type, remembered destination and memory age. Call
`TryCountActiveSearches(radius, out count, out status)` to count other active
stock searches around that destination, then `TryDefer()` if the count warrants
it. The radius is three-dimensional and accepts 2–40 metres. Counts read native
cached search destinations, exclude the candidate, and retain no actor handles.
Each read considers at most 256 active actors, with eight reads per Unity frame.
Incomplete or unavailable reads produce no count and cannot authorize deferral.
Requests close when the callback returns and cannot be used from another thread.

No subscriber leaves selection unchanged. Observation or handler failures also
leave the native result unchanged. Keep handlers bounded and avoid invoking game
actions from them. This is an advisory crowding control, not a guaranteed global
NPC limit: unsupported thoughts and reads beyond the budget continue normally.
It adds no shared enemy knowledge, health modifiers, movement overrides or new
stimuli. Death, stun, scripting and ordinary interruption continue through the
native scheduler. Hook installation reports registration only; listen-host and
dedicated multiplayer behavior still require in-game qualification.

## Native NPC party pacing

`ServerNpcParties` offers a separate scheduling policy for whole
queued native search-party events. `Considering` permits synchronous deferral
before target selection or actor creation; `Attempted` reports the native
scheduling result without claiming a confirmed spawn. Existing parties and
retreat behavior remain under native control.

Queue expiry and bounded reads make this a soft pacing mechanism. See
[Native NPC party pacing](NPC_PARTIES.md) for scope, lifecycle and outcome
semantics. It uses native client representation and does not require Modstoke
Client; listen-host and dedicated multiplayer behavior remain unqualified.

## Managed combat contract

`Modstoke.Sons.Combat` exposes versioned pistol intents, server snapshots and a
bounded client synchronization adapter through the existing Core assembly.
Server policy remains outside the SDK. See [Managed combat protocol](COMBAT_CONTRACT.md)
for message layouts, exact-peer ownership and startup composition. The contract
does not enable strict combat or mutate native inventory and health.

## Server collision reads

`ServerWorld.TryTraceSegment` performs bounded point raycasts with
explicit layer and trigger policy. `TryMatchHitPlayer` matches a same-frame hit
to a retained native player attachment. See [Server collision reads](SERVER_COLLISION_READS.md)
for query budgets, world lifetime, synchronization and authority limits.

## Server ballistic data

`ServerWorld.TryReadBallisticWeapon` copies existing game settings and
a loaded ranged-weapon ammunition profile without loading or creating assets.
`TryReadWeaponTiming` copies the same template's fire delay, magazine and reload
flags. See [Server ballistic data](BALLISTIC_READS.md) for snapshot lifetime,
unavailable data and the distinction between initial damage and final health loss.

## PNG textures off the game thread

`PngTextures.DecodeAsync` (or `Decode` on a worker you already own) turns PNG bytes
into RGBA32 rows with a full mip chain, flipped for Unity, without touching the game
thread. `PngTextures.Create` then hands the result to the GPU on the game thread and
returns a non-readable texture. Unity's `ImageConversion.LoadImage` does all of this
on the game thread and stalls a frame for a few hundred milliseconds per 4K image.

Only 8-bit, non-interlaced grey, grey-alpha, RGB and RGBA images are decoded;
palette, 16-bit and interlaced files throw `NotSupportedException`, so keep
`LoadImage` as the fallback for files you don't control. A decoded 4K image holds
about 90 MB until it is uploaded, so create the texture soon after the task
finishes and drop the task if the texture is no longer wanted. Upload one large
texture per frame when several finish together.
