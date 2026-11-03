# Native item, crafting and building actions

These APIs execute explicit actions on the initialized Unity lifecycle thread.
The existing `Plan*` methods remain advisory and never execute their plans.
Availability is not an authorization decision: a mod must authorize its own
commands before calling a mutating API.

## Items and inventory

`ItemRegistration.ExecuteItemRegistration(definition, nativeItem)`
publishes caller-authored `ItemData` into the native item list and ID cache.
The definition, UI and pickup IDs must match, and IDs must be unused. Keep the
authored assets and their bundles alive. `Registered` confirms local database
publication, not inventory layouts, multiplayer registration or save acceptance.

The overload without `nativeItem` can clone an explicitly selected simple base
item. It does not clone complex equipment, special item modules or Bolt prefab
identities. Clones receive independent UI data and an inactive pickup template;
display strings are registered for the current locale. Other locales, inventory
layouts and complex item prefabs remain the mod author's responsibility.
Native registrations cannot be hot-unregistered while game objects may refer to
them. Declaration-only registration and removal remain separate operations.

`Inventory` exposes:

| API | Native operation |
| --- | --- |
| `TryAddItem` | Local inventory addition with native capacity and item behavior |
| `TryRemoveItem` | Local removal without amount overflow, retaining equipment cleanup |
| `TryEquipItem` | Normal native equipment request for a locally owned item |
| `TrySpawnItem` | One native pickup factory call at an explicit pose, offline or host only |
| `TryReplaceInventoryModel` | Reversible visual replacement of loaded local inventory models |

Add/remove/equip results separate invocation, native acceptance, before/after
counts and observed equipment. A native return is not an atomic transaction,
finished animation or remote acknowledgement. False after invocation may include
partial effects: do not retry automatically. These APIs do not mutate remote
players and refuse inventories held by the specialized authority system.

Pickup spawning does not debit inventory or validate a remote player's right to
spawn. It requires a loaded world, ready pickup service, a valid native item and
a finite normalized pose. The game retains pickup/network lifetime management.

Inventory model replacement accepts visual-only prefabs. Original item modules,
interaction colliders and `ItemRenderable` remain intact. Retain the returned
handle and restore/dispose it on the Unity thread before unloading assets.
Native outline, gray-out and sheen effects are not rebound. Explicitly reapply
after native model recreation; held, crafting and world models are separate.

## Crafting

`TryStageIngredient` moves one selected native inventory layout onto the mat.
`TryReturnIngredient` returns the last populated native ingredient slot in its
group, matching the game's LIFO behavior. `TryCollectCraftedResult` uses the
result's native stash/reset lifecycle, including crafted notifications.
Pass actual layouts from the current local inventory, not constructed instances.
These actions confirm both storage and mat changes; a void native return or a
disappearing visual alone is not treated as success.

Transfer results distinguish selection, attempted transfer and confirmed custody.
Unique item identity and module references are checked. Module-free nonunique
stacks can exceed the mat's visible slots: count-only transfers verify native
storage deltas and leave the representative layouts intact. They do not claim
an exact destination instance. Item 483's visible-row return requires the
native live-player condition, checked again after selection.

`ExecuteModuleItemRegistration` publishes caller-authored unique ItemData with
an explicit set of the ten built-in native module families supported by the
game's save and Bolt readers. Normal native initialization must supply those
modules. Registration neither changes vanilla uniqueness nor creates modules.
Keep matching assets and stable IDs available before save loading on each peer.

`TryApplyAuthoredResultModules` applies one matching native SaveData record per
declared module to an existing unique result on the idle local mat. It calls
the original module loaders and refreshes presentation, checking exact instance,
module and storage custody across callbacks. It does not create or collect an
item. Completion means those calls returned with custody intact, not that all
supplied values round-tripped or were saved/replicated. An unconfirmed result may
have partially applied state and must not trigger an automatic retry or grant.

`ItemModuleSnapshot.TryCapture` copies supported native module records
and supplemental volume/limb state into immutable data. Capture neither removes
an item nor proves ownership. `TryApplyStoredResultModules` restores that data to
an already-created, registered unique result using the same custody guards. It
also restores saved decay timers and checks complete snapshot readback after
refresh. It does not create an item or consume any mod-owned storage count.

`ModuleStackStore.TryCreateHost` creates an opt-in, mod-owned data
store in a settled host/offline game scene. `TryPush` and `TryPop` require the
current revision and preserve last-in-first-out order, merging only consecutive
identical module snapshots. Each store is bound to its scene, load and Bolt
session; create a fresh store after a world change. Readbacks are immutable.
Default hard limits are 1,000,000 items, 4,096 runs and 4 MiB of serialized data;
callers can lower them. Mutations copy and serialize the bounded candidate, so
this is not a per-frame inventory scan or a zero-allocation API.

`TryExportJson` checks the current host world. `TryParseSnapshot` validates the
entire version-1 document without native calls, and `TryImportInitial` restores
only into a pristine host store. Invalid or stale mutations leave stored data
unchanged. Snapshot fingerprints check consistency, not sender authenticity.
Historical `ReadSnapshot().ExportJson()` remains available after world teardown
and must not be mistaken for current-world save data.

The low-level store owns data only. For native item transfers, persistence and
multiplayer coordination, use `ModuleStorage` instead of manually
combining `TryPush`/`TryPop` with inventory grants. Stored decay timers are
snapshots, not running timers. Vanilla count-only storage and item-uniqueness
flags are unchanged.

### Integrated Module Inventory

This is a per-player compact inventory extension, not a shared world chest.
Register matching stable mod/container IDs with `RegisterContainer` on the host
and every client. `EnableForNextStart` must succeed on each participant before
starting multiplayer. It adds protocol capability 62 to the existing gameplay
gate without replacing package-admission requirements or enabling ownership
migration. Ordinary offline storage does not need network selection.

`TryOpen` obtains a current local container. `TryDeposit` moves one exact stored
native instance into compact storage; `TryWithdraw` restores the top snapshot
into native inventory. Native reentry requires a supported unique item with
the same module shape and state. The SDK does not silently convert vanilla
counted items into unique items. Preparation reserves storage capacity across
pending containers and the next revision before native execution. A refused preflight changes no native
item; an attempted but unconfirmed native action blocks further storage work
and saving until the world is recovered or reloaded. Never retry it or grant a
replacement automatically.

Inspect the returned operation's `State`, `Detail` and `Item`. Multiplayer
operations can remain `Pending` while the host approves the transition or
acknowledges the committed revision. The owning player's loader performs the
native action; the host validates the ordered store transition and retains a
read-only mirror. `TryReadPeer` exposes that current mirror on the host. Initial
inventory reports and receipts are cooperative client claims, not independent
anti-cheat proof. Private inventory snapshots are not broadcast to other players.

After a completed withdrawal, `TryStageWithdrawnIngredient` selects its actual
native inventory row and uses the normal crafting-mat transfer. Call
`Crafting.TryBeginCurrentCraft` for the mat's native recipe. An
ingredient can be returned through `TryReturnIngredient` before deposit;
`TryCollectAndDeposit` collects an actual crafted result and then submits it
for storage. Collection and deposit are separate operations: if deposit is
refused, the collected item remains in native inventory. No substitute recipe,
fabricated output or direct mat insertion is used.

Persistence participates in the native save pipeline through the unique
`ModstokeModuleInventory` serializer, including player-only and client saves.
Immutable data is captured before native saving; incomplete native transfers
prevent that save. Loading retires the prior inventory lifetime and stages the
validated payload before a local container is opened. Existing data remains
preserved even when the feature has not been selected for multiplayer. Missing,
unreadable or skipped payloads are distinguished so stale data cannot silently
replace a different save. Native serializer participation is not proof of
disk durability or crash atomicity across all files.

Network transfer uses bounded fragments, receipts, retransmissions and exact
connection lifetimes. Ordinary mutations send one item's intent and revision,
not a complete inventory. Full snapshots initialize synchronization; no world
or inventory scans run each frame. Integration bounds are 16 containers,
256 KiB per container and 2 MiB of native-save JSON per player. The lower-level
store retains its separate limits. Network failures never replay native item
actions. A locally committed transfer may still be saved while its host receipt
is pending; a new connection synchronizes that saved state rather than granting
the item again.

Module-bearing nonunique returns/results and fallback outputs without a real
item instance remain unsupported. Native collection can drop an output if
capacity changes during a callback; an unconfirmed result must not trigger a
retry or replacement grant.

`TryRegisterAuthoredIngredientLayout` and `TryRegisterAuthoredResultLayout`
publish an empty caller-authored group for an SDK-published item into the current
local crafting mat. Supply matching item identities and unique-item mode, with
inactive, distinct rows under the correct native root. The inventory must be
open and idle. This does not clone layouts, replay their initialization or
authorize remote transactions. Keep assets alive and inspect the returned
publication/rollback outcome; registration is tied to the current mat lifetime.
Nonunique module-bearing ingredients cannot be staged through the count-based
native path, even when a visible slot is available.

`Crafting.TryBeginCurrentCraft` executes the native combination path
for the ingredients already on the player's crafting mat. It does not convert
an arbitrary recipe plan into grants or subtract an invented ingredient list.
The native system selects the recipe and owns exact ingredient instances,
modules, reusable ingredients, animations, consumption and output creation.

Retain the execution handle and call `ReadState` on the Unity thread. Acceptance,
pending work, observed completion and uncertainty are distinct. The native
completion event occurs before consumption/output handling, so that event alone
is not reported as completion. Disposing an observer does not cancel or refund
the native craft. Outputs may remain on the mat after completion.

Crafting requires the native inventory to be open and the locally owned player
to be idle. Remote and migrated player transactions use their own authority
paths. These APIs do not provide headless crafting or arbitrary automatic
selection of ingredients from storage.

## Building

`Building.RegisterRuntimeBlueprint` registers a caller-prepared
ingredient prefab through the established recipe and network-prefab path.
Supply stable recipe IDs and reserve both the recipe ID and its ghost ID
(`recipeId - 1`). Server and peers must ship matching content. Registration does
not distribute assets or migrate saved IDs.

The supported authoring contract requires unit root scale, one or more enabled
non-trigger root box colliders defining the placement footprint, and valid
ingredient components with authored visuals and ghost swappers. Placement
retains separate boxes instead of replacing them with one oversized bound.
Keep source assets immutable and alive.
Register before world loading when saved structures depend on the recipe.

`RegisterPreparedRuntimeBlueprint` accepts an `BuildingNativeProfile`
for a complete authored native single, multi-point or tree structure graph.
Supply its recipe, node/built prefabs, ingredients, colliders, serializers and
tree linkers with matching stable IDs. The graph is borrowed, not replaced
with the simple template. Persistent source behaviours require the native
prefab-enabler lifecycle. Partial publication remains reserved for explicit
preparation retry; the SDK cannot safely destroy borrowed assets to undo it.

Freeform and hybrid profiles additionally require an explicit
`BuildingFreeFormGraph` containing ordered native Beam, Pilar, Floor,
Wall, Ramp or Stairs structures, their complete ingredient/link graph and already registered
element profiles. Use untouched prefab assets, not initialized scene copies.
Pure freeform recipes have no built prefab; hybrid recipes retain their separate
screw-built graph. Electric-device built profiles require their exact native
serializer, device and unconnected wire. Native conversion and initialization
remain responsible for deployment; registration does not manufacture assets.
Ramps and stairs require contained Beam/Wall supports with reciprocal links and
real source/replacement elements. Solar-panel links and automatic foundations
are not supported by this authored profile path.

`PrepareRuntimeBlueprint` and `TryGetRuntimeBlueprintRecipe` distinguish source
publication from a currently prepared native recipe. `RegisterBookPage` adds
one or two mod-owned recipes to the real book data. Supply the page texture and
localized title key, close the book before edits, and use `RemoveBookPage` for
exact-handle removal. Recipe registrations cannot be hot-unloaded.

`BeginRecipePlacement`/`BeginBlueprintPlacement` start normal interactive ghost
placement. `SubmitCurrentPlacement` confirms the current valid native target;
`CancelCurrentPlacement` uses normal cancellation. Collision, snapping,
discovery, multi-point input and multiplayer authority remain native-owned.

`TryReadSpecializedPlacement` reads the current tree/multi-point operation.
`LockCurrentPlacementPoint` uses that exact multi-point operation's native
validation and custom handler. A submitted input does not prove a point was
accepted. Native operation objects are reusable, not unique session tokens.

The observed `SubmitCurrentPlacement` overload requires the expected operation
and returns a receipt of exact nodes encountered inside matching synchronous
native placement calls. Evidence distinguishes factory returns, active-node
registration and ghost display. Partial, empty or truncated receipts are not
server acknowledgments. Custom handlers can take other routes, and unrelated
patches running within the call scope can contribute effects. No asynchronous
client-to-server correlation is inferred from a position or prefab.

`SubmitConstructionIngredient` and `CancelCurrentConstruction` act only on the
node already selected by ordinary native interaction. They retain the game's
item handling, animations, refunds and network requests. `Submitted` does not
confirm ingredient consumption, world creation or server acceptance. No API
grants materials, forces instant completion or selects distant targets.

`BeginBlueprintFileImport` loads a registered definition's trusted GLTF/GLB
asset through the existing SDK importer. Supply SDK mesh ItemId extras and
explicit root-local footprint boxes. Retain its handle and call
`CompleteBlueprintFileImport` or `DiscardBlueprintFileImport` on the Unity
thread after `IsLoadCompleted`. Completion never waits for an unfinished load;
the importer is not cancelled mid-load. Published source assets remain alive,
including when a registration callback fails. Inspect `CleanupFailure` after
release. This does not import game saves or arbitrary JSON/blueprint files.

`ObserveConstruction` attaches to an exact existing native node.
`TryReadProgress` reads current native ingredient counts on demand. The native
completion callback and matching server completion evidence remain separate:
the callback runs before built-object creation and does not prove success.
Dispose the observation when finished; world exit removes its delegates.
`TryReadAuthoritativeCompletion` reads exact IDs from the matching server
completion observation, including current ownership, structure-list membership
and the native save-ID field. Exact retained factory objects can be read after
delayed network attachment. Supported freeform results report current database
membership and element-profile IDs from retained objects, without a world scan.
Retention ends on replacement observation, disposal or world teardown. Bounded
outputs expose `ResultsTruncated`. These observations do not prove complete
conversion, a disk save or replication.

Other freeform structure types, auto-foundations, arbitrary runtime-created
graphs, dynamic/external recipe overrides and standalone nestable serializers
are not implemented by the profile API. Native completion can return after
internally handled conversion errors, so membership alone is not success.
Assets still need explicit registration on each participating peer.

## Qualification

Source implementation and compilation are separate from in-game acceptance.
Custom assets, save/reload behavior and multiplayer replication need validation
against the exact game build and mod combination before distribution. Supported
runtime actions do not imply complete Unity or SOTF API coverage.
