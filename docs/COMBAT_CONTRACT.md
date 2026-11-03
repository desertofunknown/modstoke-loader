# Managed combat protocol

The Modstoke SDK, by OpenACAI, exposes a shared combat contract in
`Modstoke.Sons.Combat`, in the existing `Modstoke.Loader` assembly.
It lets a client exchange pistol intents and server observations without depending
on Modstoke Admin. It does not enable strict combat. Native projectile processing,
damage ownership, input integration and bypass suppression still need integration.

## Messages

`StrictCombatProtocol` reads and writes exact-length records into caller-provided
spans. Invalid fields, unknown versions and nonzero reserved bytes reject the
record. Version 4 requires both the original combat capability (`1UL << 2`)
and life-bound requests (`1UL << 3`). Use `StrictCombatProtocol.RequiredCapabilities`
when selecting enrollment; both endpoints must require and offer the complete mask.

| Record | Bytes | Purpose |
|---|---:|---|
| `StrictCombatBinding` | 56 | Server epoch, increasing binding generation, actor network ID and reload duration. |
| `StrictCombatIntent` | 56 | Epoch, consecutive request sequence, originating life, action and optional fire direction. |
| `StrictCombatReceipt` | 96 | Request result, echoed request life and a server snapshot of ammunition, timers and life state. |

`StrictCombatBinding.SameBinding` compares epoch, generation, actor network ID
and reload duration, which is how the SDK decides that two bindings are the same.

Actions are equip pistol, unequip, reload, fire and read state. A fire direction
must be finite and nonzero; decoding does not normalize it or authorize the aim.
Other actions require positive-zero direction components. No intent supplies
damage, health, ammunition, a target or a projectile origin.

Every action that changes gameplay names the life in which it was queued.
`ReadState` uses life zero and can observe a new life. The server rejects an
unseen action for a different life with `StaleLife`, records the denial and
consumes its sequence without applying the action. Repeating that request returns
the recorded outcome. Reusing its sequence with a different life is a conflict.
This prevents a delayed fire, reload or equipment request from becoming an action
in a later life. It does not establish movement authority or authorize respawn.

Version 3 packets are not accepted as version 4. The existing capability constant,
assembly identity and constructor signatures remain available. Code constructing
intents directly must use the new overload with an explicit life generation;
the older overload supplies zero and is valid only for `ReadState`. The older
receipt constructor infers the request life from its snapshot for gameplay actions;
use the explicit overload to report a stale-life result. Rebuild protocol consumers
against the current SDK and negotiate the complete capability mask together.
There is no downgrade that strips life information. Legacy mod-loader adapters,
vanilla clients, saved settings and existing update endpoints are unaffected.

The pistol contract has a 12-round magazine and a 200 ms fire interval. Reload
duration comes from the binding. These values describe this protocol's weapon
profile; they are not a claim that arbitrary native weapons use it.

`StrictCombatSnapshot` carries a revision, weapon ownership and equipment,
magazine and reserve counts, reload escrow, remaining timers, life generation,
life stage, health and maximum health. Constructors do not validate values;
use the codec's validation and serialization methods at the transport boundary.
An actor network ID identifies a live attachment, not a durable player account.

## Readiness

`StrictCombatReadinessProtocol` adds an optional capability (`1UL << 13`) without
changing the version 4 records. Negotiate it before using `NotReady` receipts.
Its version 1 request is 104 bytes; its response is 112 bytes. Both carry the exact
binding, a request ID, life and observed revision. The response adds a decision
and server revision; it never updates health or resources.

The mode owner calls `TryBeginReadiness` only after acquiring native controls and
projecting accepted health, armour, resources and ammunition. Call
`UpdateReadiness` during synchronization and pass responses to
`TryReceiveReadiness`. The adapter retries the same request at most eight times,
500 ms apart, within eight seconds. Duplicate responses must match; uncertainty,
rejection and expiry cannot restart a claim. A committed new life needs a new
claim after its local handoff. A previous life's acknowledgement does not apply.

A server can keep state reads and initial pistol equipment available while
refusing gameplay with a recorded `NotReady` result. It must keep synchronization
traffic available: the shared admission gate also carries these datagrams and
cannot depend on readiness. A report establishes claimed synchronization, not
client integrity or isolation from every native gameplay route. The composed
combat mode remains unavailable until its native integration is qualified.

## Client ownership

`StrictCombatClient.TryCreate` accepts an exact client-to-server control peer
whose current enrollment both requires and offers both combat capabilities. All
operations belong on the game thread. The owner must:

1. Retain one adapter for that exact peer and call `Update` each tick.
2. Pass that peer's datagrams to `TryReceive`.
3. Queue actions with `TryQueue` and read accepted observations with `TryRead`.
4. Retire the adapter on shutdown, lost enrollment or peer closure. Keep a
   terminal adapter retired until the peer actually closes; do not recreate it
   against the same connection after a timeout or contradictory receipt.

The adapter permits one pending request. It retries the identical request at
most eight times, at least 500 ms apart, with an eight-second deadline that also
includes transport backpressure. A missing initial binding expires after eight
seconds. When idle, it requests state at one-second intervals. Queue acceptance
does not acknowledge delivery.

Older bindings are ignored. An equal-generation binding must match exactly;
a new generation requires a new epoch. Only receipts matching the pending
epoch, sequence, action and originating life can complete it. Older snapshots cannot replace a
newer revision. At equal revision, state must agree and remaining timers may
only decrease. Contradictory state retires the adapter.

Within one binding, a higher snapshot revision cannot reduce the life generation,
return an initialized life to Uninitialized, or change Dead to another stage in
the same life. Downed-to-Alive revival remains valid. A later life generation may
already be downed or dead when observed; polling need not see every transition.

`TryReceive` returns true for a handled binding or receipt. Inspect
`requestCompleted` before treating a receipt as a completed request, and use
`TryRead` for the latest accepted snapshot. `ShotPending` keeps the request open.
An accepted fire receipt acknowledges server launch handling, not a hit or
damage. `EffectFailed` does not imply an ammunition refund.
`StaleLife` completes a denied request and supplies the current server snapshot;
the client does not rewrite or retry the action against the new life. Mutating
actions cannot be queued before the first initialized life snapshot is accepted.

## Local player observations

`TryReadLocalPlayer` pairs the latest accepted server state with copied native
health and life fields. The local actor's packed network ID must match the server
binding. The adapter retains the exact local-player instance, entity, native
attachment, stats, vitals and health/sickness objects, then rechecks those objects
before and after reading. It also checks the player's loaded Unity scene handle
and the SDK's observed world-entry/exit count. It does not use server world-save
records as a client world identity.

Initial native capture may wait for player readiness. Once captured, a lost or
replaced object retires that capture; reusing the same network ID cannot revive
it. A newer server binding with a new epoch permits a fresh capture on the same
control peer. Native capture loss does not by itself retire the transport adapter
or require reconnecting. The owner still needs to request and establish the new
server binding; this API does not silently rebind it.

The result includes native current/minimum/maximum health, target health,
sickness, the raw dying state and the separate downed, died and respawning flags.
Unknown dying values have no interpreted life state. Nonfinite or inconsistent
health values return `InvalidState`. The native values can disagree with the
server's combat state; they are observations, not trusted health reports or
evidence of cheating.

`IsCurrentLocalPlayerSnapshot` checks that an observation belongs to this adapter,
the same retained capture, current frame, accepted binding, revision and life
generation. It does not freeze native values within a frame. Use `TryReadLocalPlayer`
again when current values are needed. Old data must not be used to apply a later
life transition. Existing `TryRead` remains a transport-state read and does not
claim a local native-player match.

These APIs do not write health, alter timers or start death/revival. Native death
and healing paths contain delayed calls and coroutines; complete managed life
ownership must also prevent stale native continuations from applying their effects.

## Native armour observations

`TryReadLocalArmour` copies the current local player's armour under the accepted
binding, revision and life. It retains the armour system, game object, configured
order and data lists, slot records, piece definitions and spawned instances.
The supported slot domain is 0–11; this does not establish the live capacity or
configured consumption order. `TryGetSlot` addresses native data slots, while
`TryGetOrderSlot` reads the separate configured order. An unlisted data slot has
`OrderIndex == -1`.

`IsCurrentLocalArmourSnapshot` rechecks those references, copied values and the
same frame/player/life. `IsSameLocalArmourPiece` accepts an older original only
for identity comparison with a fresh current snapshot in the same life. It
requires the same live spawned instance, slot record, definition, system and
layout. Matching item IDs or empty slots is insufficient. Identity continuity
does not refresh old values or freeze the item's configuration.

Reads and explicit checks share 64 operations per adapter per frame. Collections
are bounded by the known slot domain. Nonfinite values and invalid layouts reject;
finite negative points and empty-slot remnants remain observations. Unity instance
IDs are transient. Golden multipliers do not establish golden eligibility.

Native unequip can leave a destroyed instance reference and old points in an empty
slot. That slot reports `HasInstance == false` and `InstanceId == 0`; the retained
reference is checked for freshness but cannot identify a live resource. An empty
slot with a still-live instance remains distinguishable during deferred destruction.

These APIs do not grant resources, change durability, break equipment or acquire
native ownership. Accepted server resources use the separate delivery contract below.

## Owned armour delivery

`StrictCombatArmourProtocol` version 1 adds capability `1UL << 5` to the existing
combat capabilities. The enrollment must require and offer it before creating a
combat peer. That selection stays fixed for the peer's lifetime. Existing version 4
receipts remain unchanged for peers without this requirement; an armour participant
does not fall back to accepting an ordinary receipt by itself.

Each armour page contains the exact version 4 receipt, armour revision and rules,
plus up to six ordered resources. Twelve resources require two pages of at most
240 bytes each, within the existing 256-byte transport limit. Both pages must have
identical receipt fields, including countdowns. Duplicate pages must agree, and
slots and resource IDs must be unique across the assembled state.

The client accepts the combat receipt and armour together only after every page
arrives for its exact pending request, peer, epoch, action and request life.
Partial delivery leaves the ordinary request retry and timeout active. Pending
launch and final receipt pages use separate bounded assemblies. Rebinding,
retirement and completed requests discard their fragments. Older receipts can
finish their original request without replacing a newer accepted life or revision.

`TryReadOwnedArmour` exposes only armour matching the current accepted binding,
life and combat revision. A separate respawn update makes earlier armour
unavailable until matching state arrives. Unprovisioned state is explicit and is
different from a provisioned empty account. Within a provisioned life, resource
identity and order remain fixed, durability cannot increase, and broken resources
remain broken. Repair and equipment replacement need a further resource contract.

The server freezes this companion with its retained receipt; retrying never pairs
old health with live armour. Provisioning advances the combat revision. Broken
resources stay in their original order with zero points, so skipped intermediate
updates do not erase their identities. Delivery alone authorizes neither native
effects nor repeated break calls. The binding, barrier and projection APIs below
provide the separate native ownership and completion checks.

## Local armour resource binding

`TryBindLocalArmour` associates each accepted resource with its existing native
piece for the current player life. Initial binding requires an Alive life, provisioned
state and unbroken resources. Slots, item IDs and configured order must agree; each
resource needs its own live spawned instance, and no occupied slot or live orphan
instance may be left unaccounted for. A provisioned empty account can bind an empty
native layout. Native points never provision or increase server resources.

`IsCurrentLocalArmourBinding` rechecks the current accepted revision and retained
player, system, layout, slot records, definitions and spawned instances. Accepted
points and broken flags may advance while the native pieces await projection. The
same-life identity check remains available after downing or death; it grants no
authority to apply effects. Resource identities, order, golden rules and observed
native multipliers must remain unchanged.

A missing or replaced piece retires the binding for that life, except for a break
completed through the owned projection below. Repeating the bind request cannot
adopt a replacement. A new life requires a new binding, and
retiring the player retires retained bindings. Incomplete synchronization and stale
observations can be read again without replacing the original identities. Binding
and validation share the existing 64-read budget and game-thread checks.

For an acquired barrier, a correlated respawn retains the old binding until the
explicit handoff below. Bind and validation requests return `LifeChanged` during
that interval; they do not adopt the next life or discard the handoff's identities.

These calls retain identity only. They do not block native writes, change durability,
break pieces or enable strict combat. The separate native barrier below supplies
known-path suppression; projection has its own once-only completion accounting.

## Native armour barrier

`PrepareNativeArmourBarrierForNextStart` installs the known armour mutation hooks
from the menu before world or network startup. Initial loading and unowned armour
continue normally. Acquisition cannot occur inside an active armour mutation;
finalizers release that guard after normal completion or managed exceptions.
An incomplete mutation history prevents acquisition.

`TryAcquireNativeArmourBarrier` requires a current resource binding established
while Alive and the exact acquired native life barrier. Acquisition can finish
after that same life becomes Downed or Dead, including when accepted damage has
broken a resource whose original native piece is still awaiting projection. The
original pieces must remain current; a missing or replaced piece cannot be
adopted as an already-completed break. All hooks must be
installed before a receiver can be owned. Repeated acquisition returns the same
ownership only for the same binding and life barrier. The registry retains at most
32 receivers for the process lifetime.

The barrier suppresses both equip overloads, equip-next, unequip, damage calculation,
wear-list updates, deserialization, clothing-change callbacks and validation on
the retained armour system. Blocked boolean operations return false; blocked damage
calculation returns zero. The life barrier is still required to contain native
health paths. A replacement armour system assigned to the same owned local Stats
is also blocked. Unrelated remote armour remains unowned.

Retirement preserves suppression for old receivers. There is no fallback to native
combat and no implicit ownership transfer to a later life. The explicit handoff
below retains suppression while changing the life-specific authority.

This API does not write durability, invoke break effects or enable strict combat.
Generated field setters and direct memory writes bypass method hooks. These hooks
cover the qualified native paths, not arbitrary client tampering or an exhaustive
set of future game changes. Source and compilation checks do not establish runtime
hook behavior or multiplayer acceptance.

## Local armour projection

`ProjectLocalArmour` applies the current accepted resources to the exact pieces
retained by an acquired armour barrier and native life barrier. It runs on the
game thread, checks the accepted revision and life throughout, and reserves its
bounded reads before any native effects. A full twelve-piece break uses at most
51 entries from the shared 64-read frame budget. An exhausted budget returns
`LimitReached` before mutation.

New breaks follow the native configured order in reverse. Each resource receives
one attempt, authorizing only the exact owned receiver, slot and destruction call.
Nested callbacks cannot use that authorization for further armour mutations.
Equipment, weapon presentation, health and life changes also reject reentrant
requests while projection is active. Retirement remains available and invalidates
the operation.

A successful break must leave the original slot record, a cleared definition and
the same retained instance reference. That reference may still be live during
deferred destruction or already Unity-destroyed. A replaced or cleared instance
does not count as completion. The cursor is updated after each successful break;
remaining points are copied from accepted state, including zero for broken pieces.
Final readback must confirm the full state before returning `Applied` or
`AlreadyCurrent`. Completion records prevent repeat effects on later calls.

Native unequip can return false after changing state. A failed call, exception or
lost context after mutation returns `MutationUncertain` and retires projection
authority while retaining native suppression. It cannot be retried or rolled back
as though nothing happened. The API does not grant server resources, replace
equipment, prove an honest client or activate the product's strict combat mode.
Product integration remains separate work; runtime effects and multiplayer
behavior still require gameplay acceptance.

## Armour ownership after respawn

`TryHandoffNativeArmourBarrier` transfers an acquired receiver only after the
correlated respawn has committed and the same native life barrier has advanced
exactly one life. It also requires an Alive accepted state and a provisioned
armour receipt for that life and current combat revision. An unprovisioned receipt
is not an empty account and cannot authorize a transfer.

The old binding must still be healthy. Its system, configured order, slot records,
definitions and surviving instances must match. Completed breaks must remain
empty with their retained destroyed references. Unrecognized pieces, changed
layouts, unexplained cursor changes and replacements are rejected. The new server grant must independently
cover every still-equipped piece with unbroken resources. The call waits if a
deferred destroyed instance still prevents an empty slot from binding.

This operation neither equips items nor copies native points into server ownership.
The server must explicitly choose new-life resource IDs, quantities and rules.
The current server coordinator still needs that provisioning policy; its respawn
outcome alone grants no armour. Inventory removal, new loadouts and equipment
replacement remain separate operations.

The game-thread handoff reserves four entries from the shared read budget and
rechecks the accepted state and native capture before publication. It replaces one
registry entry, retires the old binding and keeps the same receiver continuously
suppressed. Repeated successful calls return the existing new-life binding without
resetting its break attempts. A failed post-publication check retires the new owner
and leaves suppression in place. Retired or uncertain owners cannot be recovered
through this API.

The product controller must order this handoff before projecting new-life armour.
It does not enable strict combat, prove native respawn retention or establish
multiplayer acceptance.

## Native life barrier

`PrepareNativeLifeBarrierForNextStart` registers the known native health and life
hooks from the menu, before a world or network run starts. It does not select enrollment or
claim a player. `TryAcquireNativeLifeBarrier` then requires a current local
observation, an accepted Alive server life and a healthy native player without
pending death calls, healing, respawn or an active respawn timer.

`TryAcquireInitializedNativeLifeBarrier` additionally requires an observed normal
return from `Vitals.InitializeStats` for the exact vitals and health objects.
It returns `NativeInitializationPending` until that evidence exists, or `Busy`
while an intercepted initialization or save-load call remains on the stack.
The internal Client driver uses this entry with its existing bounded initial
ownership wait. The original acquisition entry retains its previous contract.

Initialization and save-load observations remain bounded and retain their native
identities. Exceptions, unbalanced calls, unreadable receivers and health objects
without matching initialization cannot establish readiness. A skipped original method does not establish
initialization. Nested cooking effects and life transitions still pass through
their existing checks; initialization never clears transition or cutscene history.
A later initialization or save load remains subject to the owned health writer
replacements. This evidence does not establish completion of future load callbacks,
actor attachment, other native callbacks or full startup containment.

The internal Client driver first binds the live armour pieces while Alive, then
acquires initialized life ownership, projects accepted health and acquires the
already-held idle pistol. Server armour and selected resource grants must be
provisioned before this sequence starts. If accepted downing arrives while armour
acquisition is waiting, the driver can complete ownership using that earlier
binding, project accepted armour damage and then present the downed life. It
cannot establish initial life or pistol ownership from a Downed or Dead state.

Armour binding alone does not prevent native changes. Replacement or removal of
an original piece before barrier acquisition ends the attempt; the driver does
not rebind it. Initial loading and actor attachment still need separate startup
qualification. This sequence does not provide admission quarantine or contain
arbitrary held items. Once life, pistol and armour are owned, optional breathing
setup does not hold up downing. Resource synchronization resumes when alive; the
downed interval does not consume its wait deadline.

`DiscardLocalWeaponInput` drops captured fire/reload input that has not been
queued. The internal driver calls it during initial ownership waits and when
setup completes, so buffered input from that wait cannot become a delayed shot.
It requires the owning game thread and rejects concurrent SDK mutations. It does
not cancel queued protocol requests, change ammunition or release native
suppression.

This is an incomplete integration mechanism for a managed life controller.
Acquisition suppresses the captured player's known native health, death, revival and
respawn paths; it does not apply the server's replacement behavior. The shipping
Client does not prepare or acquire it. Do not use it as a standalone combat mode.

The barrier covers the inspected PlayerStats entry points, four native coroutine
continuations, the associated respawn timer and local death-cutscene selectors.
Those life routines also access the current global LocalPlayer, so they are
suppressed while that player is owned even if an older Stats object receives
the call. This does not intercept remote player-state replication.
An unowned native life transition observed before acquisition disqualifies that
Stats object. Healthy death checks and initial Alive setters remain usable.
An observed unowned cutscene start or incomplete observation history disqualifies
later acquisition for the rest of the process. This avoids treating a healthy
snapshot as proof that earlier asynchronous work has finished.

Retiring the client or losing its capture leaves suppression in place for that
exact native Stats, Vitals and health-stat identities. A new binding cannot reclaim
the same Stats object.
There is deliberately no in-world return to native life processing. Retained
owners and pre-acquisition observations are each bounded at 32 objects; reaching
either bound rejects further acquisition instead of evicting live ownership.
These bounds concern local lifecycle history, not the server player limit.

The hooks do not cancel an already-running cutscene, write health, prevent every
direct field write or prove that the client is honest. Native death-marker replies
lack a request/life nonce; merely registering a new callback cannot distinguish an
old reply. Managed death and respawn still need their own correlated operation,
native presentation ownership and completion handling. Hook registration and
compilation do not establish successful interception in the running game.

## Native health writers

An owned Vitals update retains the inspected order of ordinary stat ticking,
hydration/fullness/rest buff accounting, cooking expiry, and non-health portions
of the native energy and god-mode branches. The replacement omits the inlined
health regeneration and sickness-to-health clamp. The health stat's inherited
tick and mutation methods are intercepted separately; other stat receivers pass
through. Native `SetSickness` keeps its sickness write without clamping target
health.

The single-item effect dispatcher suppresses its Health branch for the owned
local player on both application and expiry. Other effects, including sickness,
remain native. These hooks do not authorize consumable use, reserve inventory,
refund a consumed item or provide server-owned survival rules. The managed mode
must supply those operations before exposing them as supported gameplay.

Several simple stat setters share native code with unrelated classes and structs.
Their hooks compare only the raw receiver address with the retained health object
and forward every other receiver, value and method-info pointer unchanged. They
never cast unrelated receivers to Stat or convert reference arguments to a health
type. Their inspected Windows x64 code must match before installation; changed
or already-patched bodies make preparation unavailable. The shared empty base
`Stat.OnValidate` body is deliberately not hooked.

Native update helpers can invoke other code. Ownership, connection, capture,
life and frame are checked around those calls. Failure retires the writer and
halts the captured Vitals update without restoring its native health loop.
Recursive updates are suppressed. Hook installation must finish before any
player can be acquired; a partial installation cannot claim ownership.

The captured player's stat initialization, save-state loading and strength gains
also preserve their non-health work without replacing health. Initialization
retains stat validation, temperature sources and awake overrides. Loading retains
typed survival values and cooking-effect reapplication in native order; it ignores
saved maximum, current and target health. Strength progression retains its curve,
display and coroutine timing without granting health. Sickness maximum follows
the owned health maximum in these operations.

Capture requires the cached health and sickness objects to be uniquely registered
with the same native stat manager. Rebuilding its list with those same objects is
allowed; a detached cache, duplicate stat or changed manager retires the capture.
Recursive initialization or loading retires the writer rather than restoring the
native routine. Normal unowned players retain the original behavior.

This covers the inspected routes, not arbitrary field writes or every native
caller that might inline a mutation. Complete status-effect and life presentation
integration remains open. No shipping Client path acquires this ownership, and
runtime interception remains unqualified.

## Applying accepted health

After acquiring the native life barrier, `ProjectLocalHealth` applies the latest
accepted server health and maximum to its exact captured player. The caller
cannot supply a health value. The barrier retains its life generation; only the
correlated respawn handoff described below can advance it to the next life.
`LifeChanged` identifies that mismatch if the barrier has not already retired.
Downed or Dead health can be represented as zero within that same life, but this
operation does not change native dying flags, animations, controls or respawn.
It does not require weapon, armour or breathing ownership. The internal Client
driver applies it before local setup waits for the owned life, while preserving
the required correlated handoff before projecting any later life.

The operation checks the current peer, binding, accepted revision, life, frame and
native objects before and after each field write. It writes maximum/current
health through the captured health stat, sets the native target to the accepted
health and verifies all three values. This direct writer does not temporarily
permit native setters, so a nested native call cannot borrow its authority.
It preserves sickness and the native lower bound. An incompatible lower bound
rejects the operation. Local invalid current
or target health can be repaired; those values never become server health input.

`Applied` means those fields matched on readback. `AlreadyCurrent` avoids writing
fields that already match. `Busy` rejects reentrant projection. If a write throws,
ownership changes or readback disagrees after mutation starts, `MutationUncertain`
retires the barrier's writer without releasing native life suppression. There is
no rollback into a potentially different player or life, and no automatic retry.
The mode owner must handle unsuccessful outcomes before continuing managed play.

This is an explicit operation, not an automatic update loop. The separate
native-writer hooks cover the routes described above. A successful projection
does not establish that every later write is prevented or prove an honest client.
Ordinary Client startup does not invoke it. Strict combat remains unavailable until
health/life presentation, native bypass handling and server action authority are
integrated end to end.

## Presenting an accepted life state

`UpdateLocalLife` advances ordinary downing and same-life revival for the captured
player. The mode owner calls it each game frame after acquiring the native life
barrier. It reads accepted server state, projects that state's health and checks
the same player, connection, life and revision around every presentation change.
It does not request damage, revival or respawn, and no shipping Client path calls
it automatically.

Once the life presentation context is captured, each advance projects health
before checking the requested pose or preparing the equipment stash. An
unsupported pose can therefore leave health synchronized without applying the
downed controls; health projection alone never counts as a completed transition.

Downing stashes held items, disables movement and view controls, and updates the
native downed pose, stimuli, replicated dying state and HUD. Revival restores
those controls through the native get-up tags and layer weights. The controller
owns the waits and blend progression; it does not start native death or healing
coroutines. A second downing interrupts its pending get-up. Weapons remain
unequipped until a separate authorized equip operation restores them.

`Pending` means presentation is still advancing. `Presented` and `AlreadyCurrent`
describe local application, not a server acknowledgement or client-integrity
claim. `Dead` keeps the player incapacitated and returns `RespawnRequired`;
corpse creation and native respawn prompts are not implemented here. A separate
respawn transaction can authorize the next life. The captured respawn timer must
stay unchanged and inactive. Swimming,
knockdown, active landing and an unrelated full-body action at downing are
unsupported contexts, not permission to run native death as a fallback.

Every delayed step rechecks ownership. Losing context after a mutation returns
`MutationUncertain` and retires the writer without unlocking a different player.
The mode owner must stop managed play on an unsuccessful outcome. This slice
does not reproduce death audio, blood overlays or fades, broadcast legacy heal
events, or establish exclusive control over every native animation writer.
Visual behavior and multiplayer replication still require in-game acceptance.

## Correlated respawn

Respawn uses `StrictCombatRespawnProtocol` version 1 alongside combat version 4.
It adds capability bit 16: both required and offered capability masks must contain
28 for respawn operations. Ordinary combat still requires mask 12. The request,
offer, report and outcome records are 72, 120, 96 and 152 bytes respectively.
They carry the complete binding, old life, client request ID and, after server
authorization, server operation ID. There is no compatibility downgrade.

The mode owner calls `TryRequestRespawn` only after ordinary downed presentation
has finished for the accepted dead life. The server decides the destination and
health policy explicitly. `TryReceiveRespawn` accepts offers and outcomes from the
exact enrolled server. `ApplyRespawnPlacement` attempts an offered native move
once; `UpdateRespawn` drives request/report delivery without moving the player.
Each send phase has at most eight attempts separated by at least 500 ms.

The client has 15 seconds from its own request to start placement. Duplicate
offers cannot renew that window. Once placed, it can wait until 30 seconds from
the original request for a correlated outcome. The server's authorization and
commit window is 15 seconds from first request receipt. These are local monotonic
deadlines, not synchronized clocks. Expired or uncertain operations are not retried
as another request for the same life.

Placement retains inventory and uses the native spawn teleport only while the
captured player, controls, physics objects, life and frame remain current. It
does not start a native death sequence, create a bag or grant items. Partial
mutation or lost ownership is uncertain and retires the writer. The existing
native bag path can reuse a bag without capturing newly held items before deleting
them; inventory loss and retrieval therefore need a separate authoritative transfer.

The server treats `Placed` as a client claim. It also requires two observations
of the exact actor within one metre of the authorized point, separated by at
least 250 ms. Only then does it start the next life using retained server policy.
These observations are neither terrain-safety proof nor evidence of an honest
client. A terminal outcome is immutable and duplicate reports cannot start
another life. A missing outcome remains uncertain.

On `Committed`, the client advances the same native barrier by exactly one life.
Its registry entry continues suppressing old native callbacks. No retired barrier
can resume writing. If an ordinary state receipt arrives first, health remains
contained but projection waits for this correlated handoff. A later accepted
snapshot is preserved, including damage, downing or death after the commit;
the respawn outcome cannot roll it back. `UpdateLocalLife` then presents that
current state through its existing downed/get-up controller. An unrelated pending
combat intent still requires its own life-bound receipt.

These are explicit integration components. No shipping Client path requests or
applies respawn, and strict combat remains unavailable. Inventory transfer, safe
spawn selection, equipment restoration, complete native bypass coverage and
observed multiplayer behavior remain separate acceptance work.

## Capturing pistol input

`PrepareNativeWeaponBarrierForNextStart` prepares interception from the menu,
before world and network startup. `TryAcquireNativeWeaponBarrier` then captures
an already equipped, idle local pistol for an accepted alive life that owns and
equips the server pistol. It requires the existing item, inventory, registered
held object, inventory presentation, controller, weapon, ammo module and camera
to agree. It never equips an item, refreshes a
module, copies native ammunition into the ledger or enables strict mode.

The supported profile is pistol 355 with internal ammo 362 and capacity 12.
Native magazine and reserve counts do not authorize an action. The server owns
those counts, reload timing and cooldowns. Pending equipment changes, native
reloads, attacks and incompatible life or input states prevent capture or input.
An observed native pistol shot before acquisition prevents later acquisition for
that inventory: this adapter cannot prove that its projectile has finished.

For a captured pistol, primary-action and reload input are intercepted before
native firing, ammunition spending, reload and delayed reload callbacks. Mutation
callbacks never generate requests. Retired captures continue blocking their native
callbacks. Unknown local pistol receivers using an owned inventory are blocked
without granting them input authority. Preparation and capture are explicit; the
shipping Client does not select them.

`UpdateLocalWeaponInput` queues one copied action through the existing life-bound
protocol. Call it on the game thread before ordinary `Update`. Its buffer lasts
at most 250 ms while another request is pending. Further clicks do not replace
that input or renew its deadline. A changed player, life, held weapon or input
context discards it. `Queued` means locally queued, not accepted by the server.

Fire copies normalized camera-forward direction at input time. This is the
managed aiming rule, not a claim of native reticle parity or trustworthy client
aim. No native projectile, muzzle effect, reload animation or ammo write is
performed by this input adapter. Client orchestration of equipment changes, remote
shot and impact effects, complete bypass coverage and the integrated mode owner
remain separate work.
Capture and hook behavior still require in-game acceptance.

## Native ammunition ownership

The weapon barrier retains the captured item, ranged module, ammo object and
weapon. It blocks the mapped native ammo additions, removals, type changes,
refreshes and reference replacements for those objects, including retired
captures. Ranged-module refresh and save-data loading cannot restore cached ammo
into the captured weapon. A refresh receiving the captured item is also blocked
before it can bind another ranged module. Unowned modules retain native behavior.

Some native setters share machine code with unrelated types. Their guards compare
retained receiver addresses without interpreting unmatched objects, and forward
unowned calls unchanged. The supported Windows x64 bodies are checked before
installation. An unavailable target or changed body prevents barrier acquisition;
partially installed guards remain rooted.

Native packet decoding remains intact, including its cursor advancement and
cached attachment/ammo fields. Its calls to live ammo writers are intercepted.
Cached values, native saves and native magazine displays are not the authoritative
server ledger. Neither direct field assignments nor memory editing are prevented
by these method guards. Equipment transitions beyond the controlled stash below,
inventory serialization and the complete managed mode still need their own
ownership contracts and acceptance.

## Native rebreather ownership

Refill and wear requests retain their version 1 formats and exact resource-revision
preconditions. `StrictCombatRebreatherProgressProtocol` adds capability bit 11 for
version 2 requests that permit intervening timed air progress. It requires the
resource, refill and wear capabilities together. Both peers pin this selection for
the connection; version 2 never falls back to version 1.

The existing request constructors and codecs still select version 1. New constructor
overloads set `AllowsTimedProgress`; the explicit `TryWriteProgressRequest`,
`TryReadProgressRequest`, `TryWriteProgressResponse` and `TryReadProgressResponse`
methods select version 2. Packet sizes and field offsets are unchanged, and an echoed
request must use the same version as its response. This flag is part of request
identity and retry correlation.

The server must distinguish timed progress from decisions that invalidate an older
observation. Current policy, life and resources still govern the operation, and the
actual account mutation uses the current exact revision. Accepted version 2 results
may therefore have a revision greater than the observed revision plus one. Responses
remain historical decisions; clients obtain projection authority from the subsequent
combined state receipt. This protocol does not itself implement resource policy or
prove client integrity.

`PrepareNativeRebreatherBarrierForNextStart` installs the mapped rebreather guards
from the menu, before world or network startup. `TryAcquireNativeRebreatherBarrier`
then captures an existing equipped item 444 for the exact owned local life. It
checks equipment membership, inventory presentation, controller, existing volume
module, vitals, binding and accepted life revision. It does not assume a chest
slot, create a module, equip an item or copy native air into server resources.
Pending equipment work, a running refill or previously observed local refill,
consumption or `PlayerStats.UseRebreather` call prevents a new acquisition.

The barrier retains at most 32 inventories and their captured equipment identities
for the process lifetime. Retirement keeps suppression. The mapped worn
consumption route and manual refill start, update and disable routes are blocked
for owned equipment. Manual refill selects its own local inventory instance, so
the barrier also suppresses that inventory's manual refill route. Native
initialization, contents replacement, refill, empty, refresh and save loading
cannot write the retained volume module. Packet loading consumes the native
recipe and signed volume fields, preserving the 48-bit cursor advance without
initialization, recipe lookup or volume mutation. Malformed reads retain their
failure rather than being accepted as an update.

`PlayerStats.UseRebreather(bool)` is also blocked for retained player-stats or
breathing-data identities. Both Boolean values can transfer air between the lungs
and the tank mirror, so neither is a presentation-only operation. The guard runs
before the native active flag or air values change and remains in place after
retirement and across an accepted same-object life handoff. Other receivers retain
their native behavior. This does not yet provide authoritative lung oxygen or
mouthpiece presentation.

Equipment guards install with rebreather preparation, independently of pistol
preparation. They reject the mapped item-object setters, item initialization and
refresh, volume-module checks, inventory presentation initialization and held
controller initialization when they would reuse captured equipment. Volume
refresh checks its incoming item and object as well as the module itself. The
rebreather controller override also rejects initialization for the owned animator;
blocking only its base call would leave the override's later writes unguarded.
Within the owned inventory, presentation initialization rejects replacement item
444 while unrelated equipment keeps its ordinary path.

Two volume setters share native code with unrelated types. Byte-qualified Windows
x64 detours compare retained module addresses only; other receivers keep their
original calls. All required hooks must install before acquisition. Direct field
writes, memory editing, generic module-list changes and replacement through
unmapped entry points remain outside this guard's coverage. These hooks do not
provide an accepted equipment replacement or transfer operation.

These explicit APIs do not enable strict combat. Breathing authority, refill
intents, equipment/life transfer and persistent resource grants are not connected
yet. The shipping Client does not call the APIs, and native interception still
requires in-game acceptance.

### Owned tank delivery

`StrictCombatRebreatherProtocol` defines an optional capability at bit 8 and a
152-byte receipt containing the unchanged version 4 combat receipt. It carries
server-owned capacity, air, canisters, refill mode, funded remaining refill air
and the resource revision and refill sequence. The authority retains these values
with the original receipt; retrying an old request cannot attach newer resources
to its old result. An unprovisioned account is explicit.

The client accepts this extension only when required and offered by the enrolled
peer. If armour delivery is also required, the matching armour pages and tank
receipt must arrive before either resource set or the combat state is accepted.
Pending and final shot results have separate bounded assemblies. Conflicting
envelopes or resource contents retire the client. Rebreather revisions advance
independently of health: a newer resource revision can carry a funded refill
while the combat revision stays unchanged. Equal resource revisions must retain
the same contents, and capacity cannot change within a provisioned life.

`TryReadOwnedRebreather` reads the accepted resource state.
`ProjectLocalRebreather` requires the exact retained rebreather and local life. It
rounds accepted tank values down to native floats and writes only the module's
air/capacity and the matching breathing-data tank fields. Each write is bracketed
by binding, life, accepted-state, object and frame checks. Readback distinguishes
`Applied`, `AlreadyCurrent` and `MutationUncertain`; an uncertain mutation retires
the equipment and life barriers without rollback against possibly changed objects.

`ProjectLocalRebreatherActivation` additionally maps accepted `IsWorn` to whether
native breathing may use the accepted tank. It requires negotiated wear receipts
and accepts either wear value. This is an explicit activation operation; wearing
the inventory item, raising its mouthpiece and enabling tank breathing are distinct
native states. The operation keeps the same equipped item and does not animate the
mouthpiece or call `UseRebreather`.

Deactivation clears the breathing flag before changing air. Activation sets the
flag only after all four tank fields have been projected. Every write retains the
same owner, accepted snapshot and frame checks. Readback includes the activation
flag; a partial or uncertain mutation retires both barriers. These sequential
writes do not provide rollback. The existing air-only method keeps its behavior,
including its worn-state requirement when wear receipts are negotiated.

Neither operation grants canisters, invokes refill or volume-change callbacks,
changes lung air, equips an item or authorizes drowning damage. Native breathing
uses the activation flag and positive tank air to skip lung drain; its separate
lung depletion, replenishment and warning-audio paths remain native. This API does
not establish server-owned lung oxygen or complete breathing enforcement. The native
physical gauge reads the module directly, but gauge and HUD behavior remain unverified
in-game. The shipping Client does not advertise the new capability or invoke
projection. Server provisioning, refill requests and the breathing clock still
need integration before this is a usable survival mode.

## Projecting accepted ammunition

`ProjectLocalAmmunition` writes the latest accepted magazine count into the exact
captured, held pistol's ammo object. It requires the same server binding, life,
accepted revision and native weapon associations throughout the operation. The
supported pistol remains item 355 with internal ammo 362 and capacity 12. The
server may be reloading while the native weapon remains idle; only an accepted
snapshot changes the projected count.

`Applied` and `AlreadyCurrent` describe local count readback. Native reserve
items, module caches, ammo type, capacity and ownership references are unchanged.
No native reload, grant, refresh or setter bypass runs. Loss of context after a
write attempt returns `MutationUncertain` and retires the weapon and life barriers;
there is no rollback or automatic retry. Native counts are never uploaded as
server authority. Direct memory writes and a dishonest client remain outside
this projection guarantee.

The call is explicit and does not activate strict combat or restore equipment.
The shipping Client does not call it yet.

## Delayed equipment initialization

The weapon barrier also retains the item's held object and inventory presentation
owner. Native item-object assignment, whole-item refresh and renderer completion
cannot replace or refresh an owned item or held object, including after retirement.
This prevents an old render callback from refreshing a replacement object through
the item's mutable reference.

Held initialization is blocked before its first mutation when it targets those
identities. Pistol initialization through the owned inventory presentation is
also blocked, including callbacks that would initialize a new pistol object.
Other item types and unrelated player presentation retain their native path.
The callback's supplied renderer transform is not used as ownership evidence:
the native callback itself ignores it.

Direct held-controller and ranged-controller initialization are also blocked
when they target a retained controller or item. Ranged weapon rebinding, ammo
instance lookup and event registration cannot reinitialize those associations,
including after retirement. An unowned controller already referring to a retained
weapon cannot run these binding helpers. Unrelated readable controllers retain
their native behavior. The shared native `OnEnable` body is left untouched; its
unique event-registration callee is guarded instead.

These guards do not perform re-equipping. A controlled equip operation still
needs to own item removal, object creation, initialization, animation waits and
completion. Native module refresh cannot be used as a pure reference binder:
it switches attachments and restores cached ammo. Direct field writes, direct
calls to other module types and arbitrary mod code remain outside these guards.
The shipping Client does not activate this integration.

## Preparing a controlled equip

The internal equipment transfer retains one existing pistol instance, its storage
entry and list, inventory, callbacks and ownership effects. It requires empty
hands, no pending native equip, and the current accepted alive/equipped server
state. Taking the item removes that exact list entry and updates its retained
count; it never falls back to removing another pistol by item ID. It preserves
the native successful-removal notification order, including the zero-count manager
notification, without calling the type-based removal helper.

Each notification and ownership-effect call is bracketed by captured context and
storage checks. A notification can invoke multiple native subscribers before
returning; these checks cannot interrupt an arbitrary subscriber midway through
its work. An uncertain partial transfer retires the captured life and is not
retried or rolled back. A fully taken item may be returned before presentation
only while the same operation remains authorized. Return uses the exact instance,
suppresses auto-equip and requires direct storage readback. Neither result means
that a weapon has been equipped.

Prefab preparation creates an inactive staging object and an inactive clone of
the held prefab under it. It preserves the source prefab and applies the native
locator transform convention. Unity documents that clone Awake/OnEnable callbacks
run only for objects active in the hierarchy during instantiation. This is the
basis for preparing references before activation; it is not multiplayer acceptance
or a guarantee about arbitrary mod constructors. See the [Unity 2022.2 Instantiate
reference](https://docs.unity3d.com/2022.2/Documentation/ScriptReference/Object.Instantiate.html).

These internal phases are composed by the explicit SDK equipment operation
described below. A failed preparation retains its partial object references for
owned cleanup and reports uncertainty when mutation cannot be established. It
does not retry or activate those objects. The Client mod does not yet invoke the
operation, and the complete re-equip flow still needs in-game acceptance.

An internal preparation reservation extends the existing native guards to the
new controller, weapon, held object and original prefab ammunition before
activation. Reservation requires a qualified inactive clone with exactly one
compact-pistol controller and one ranged weapon, existing 362 ammunition with
internal capacity 12, and the captured item transfer. Missing components or
ammunition are rejected; the constructor does not supply them. This does not
establish that every stock prefab has the required layout before rendering.

Reservations remain protected after failure or retirement and share the bounded
weapon-owner capacity. They provide no input authority. Adoption requires the
ordinary completed-weapon checks plus the same player, life, binding, object,
module and retained ammunition. Native initialization and cleanup suppression
does not replace the still-needed owned initialization and cleanup operations.

The event helper retains the seven animation delegates and two input delegates
with their exact receiver/actions. Registration is attempted once; explicit
detach removes only those captured subscriptions. Dispatch requires the caller's
current completed-weapon/life qualification and the captured receiver association.
Partial registration or uncertain dispatch disables delivery and retires its
owner. Native UnregisterEvents cannot remove these managed forwarding delegates.
Neither helper is connected to the shipping Client or enables strict combat.

Internal `StrictCombatEquipmentBinding` captures the source inventory item,
module and ammunition, controller/player references and qualified prepared-clone
components. Explicit, one-attempt direct reference assignments preserve magazine,
reserve and cached ammunition state without calling native module `Refresh`.
Reference validation remains tied to the retained current life and ownership;
uncertain attempted writes retire the reservation and life.

Internal `StrictCombatEquipmentRenderer` observes the retained instantiate
operation pointer and version, status/result, cached loaded object and
controller/weapon callback fields. Newly boxed handle wrapper identity is not
treated as stable. The helper neither acquires nor releases Addressables handles.

The appearance helper applies existing visual variants and plating to captured
receivers under the loaded renderer. It retains their materials, meshes and
property-block targets, then checks the result. It does not initialize modules,
restore ammunition or dispatch arbitrary module consumers. Unsupported layouts
are rejected before mutation; uncertainty after an attempted write retires the
equipment and life.

The animation helper owns a new proxy on the captured player animator and retains
the native synchronization component, target, rules and scheduler subscription.
Native updates require the same equipment life and permitted operation phase;
native driver replacement is blocked only for owned components. Cleanup checks
the exact scheduler entry before disabling it and requests destruction of only
the owned proxy. The caller must wait for confirmed detachment because Unity
object destruction is deferred.

The controller helper initializes the captured local controller without native
global-player lookup helpers. It accepts a skipped first-look animation only when
the native flags suppress it or the exact saved history entry already records it.
Plated items use their separate history key. It registers the captured hit-react
receiver, updates the captured network appearance and loaded-ammo display, and
resets the captured camera position. It does not run a first-look coroutine.
An explicit one-attempt equip-audio request uses the captured item name and one
position read, with no network role. A returned request is not proof of audible
playback. Its explicit detach removes only its own attempted registration.

The inventory handoff helper retains the ordinary held definition, object map,
storage entry, equipment arrays, effects and notification delegates. Registration,
animation variables, the initialized notification and final inventory handoff are
separate one-attempt steps. The pose advances with the native damping parameters
once per caller frame. Commit requires an active initialized object, writes its
right-hand slot, applies equipped effects and sends the equipped notification.
Each step requires the same life and accepted revision; uncertain mutations are
not retried or reported as complete.

The activation helper disables the controller before activating the staging
hierarchy and owns its renderer's first enable and load callbacks. Both immediate
and asynchronous completion must retain the same operation, life and accepted
revision. Retired equipment cannot receive late rendering callbacks. Enabling
the controller requires a committed handoff for that same reservation; native
Update remains blocked until the completed weapon is adopted.

Retirement stops controller updates before the operation detaches its events and
animation proxy. Renderer cleanup then consumes its Addressables reference once.
A pending load can keep its own execution reference, so a still-valid handle is
not permission to release again. A returned release request does not establish
destruction: the caller must observe reference retirement and destruction of the
complete staging hierarchy. Native cleanup cannot perform a second release for
owned renderers. Unrelated renderers retain their native behavior.

`TryBeginLocalEquipment` starts one explicit restoration attempt for the exact
pistol retained by an owned stash, after local get-up has completed and the server
authorizes ownership and equipment. `UpdateLocalEquipment` advances loading,
presentation, inventory handoff, fresh weapon capture and accepted-ammo projection.
No new attempt starts automatically. The controller stays disabled during loading;
weapon input is enabled only after adoption. Concurrent SDK mutation calls are
excluded, and competing native inventory changes interrupt preparation. This does
not establish exclusive control over every native inventory entry point.

`CancelLocalEquipment` requests retirement. Cancellation detaches captured events,
animation and hit-react registration, releases the renderer once, reverses a
confirmed handoff, returns the same item and observes staging destruction. Pose
layouts with triggers or ambiguous parameters are rejected before pose changes.
Partial or uncertain effects and notifications are not replayed as compensation.
Same-life downed/death presentation waits for cancellation while the captured
inventory is still usable; a changed life, retired owner or replaced inventory
does not authorize a return. Respawn requests wait for a confirmed return.

The SDK reserves one of 32 cleanup retention slots before starting an equipment
operation. While its runtime ticks, it advances cancellation even after the
calling mod shuts down. It never evicts an unfinished operation or begins normal
preparation automatically. Callers still drive `UpdateLocalEquipment` for loading
and handoff, and may explicitly advance cancellation too. An uncertain result can
retain blocked cleanup and must not be treated as a returned item. Deferred
cleanup cannot be guaranteed at process exit. Installed prefab behavior,
lifecycle interruptions and multiplayer behavior remain unobserved. Strict combat
remains disabled and unadvertised.

## Presenting an accepted shot

`UpdateLocalShotPresentation` presents a fire request captured by
`UpdateLocalWeaponInput` only after the enrolled server accepts its launch.
The adapter retains the original binding, life, weapon and request sequence.
`ShotPending`, denied requests, arbitrary receipt values and direct `TryQueue`
calls do not authorize these effects. The server receipt acknowledges launch;
it does not establish an impact or damage outcome.

The mode owner calls the presentation method on the game thread each frame.
Only the latest unplayed accepted shot is retained. Its one-second lifetime
starts at the original input, including input buffering and transport delay;
duplicates cannot extend it. Changed contexts and expired shots are discarded.
The attempt is consumed before native calls, so a partial effect is never retried.

Presentation uses the existing pistol muzzle effect, ejected shell, fire-state
crossfade and explicit local sound. The prefab component graphs and critical
particle settings must match the supported cosmetic behavior. Unknown components
or unavailable settings reject presentation before it starts. This qualification
is for the stock effects and is not a sandbox for arbitrary mod code.

The crossfade uses the native fire-state presentation helper instead of the
local attack trigger, whose cleanup belongs to the intercepted input loop.
This is a controlled animation choice, not a claim of full native recoil parity.
Shot sound uses `NetworkRole.None`. The ordinary weapon sound callback also
emits NPC noise and network audio, so owned native audio and network-presentation
callbacks remain suppressed. Server-authorized NPC noise and other players'
shot presentation need their own integration.

`Presented` means the local effects were submitted. It does not mean they have
finished, been observed visually or reached other players. Muzzle and shell
lifetimes belong to their own effect objects. A failure or changed ownership
after mutation starts returns `MutationUncertain` and retires the weapon writer;
it does not undo an accepted shot or release native suppression. No shipping
Client path invokes this method, and strict combat remains unavailable.

## Stashing a held pistol when downed

`UpdateLocalLife` qualifies equipment before projecting a new downed or dead
state. It supports empty hands, or the already-owned idle compact pistol with
an empty left hand, an available backpack and room to store its exact unique
item instance. Other loadouts return `UnsupportedContext` before this operation
changes health or controls. This does not equip a pistol or acquire weapon
ownership on demand.

The transfer uses captured inventory, slots, item, held object and controller.
Each step checks the accepted life and revision, game frame and captured
references. The pistol is returned once, with readback confirming its exact
instance in storage. An inventory callback that changes context interrupts the
operation. A failure after mutation retires the life writer and reports
`MutationUncertain`; it never repeats the grant or attempts a blind rollback.

Controlled cleanup runs while the captured life is current. Deferred native
disable and destruction callbacks stay blocked for that controller, including
after retirement, because they otherwise reach the current global player.
Old wide-hand pose workers are also suppressed for the owned animator and
inventory presentation object. Stashing applies the final removal pose without
starting another worker. Ammunition prompts are hidden immediately, without
native reparenting or tutorial slide-out. The native camera-reset worker is
not started.

Revival does not automatically re-equip the weapon. Other equipment transfers,
pending renderable initialization, arbitrary mod callbacks and inventory
serialization remain outside this qualification. Native identity checks and
readback do not provide atomic rollback or prevent direct memory changes.
Compilation and installation do not establish animation quality, gameplay
parity or multiplayer acceptance. Strict combat remains unavailable.

## Equipped weapon observations

`ServerWorld.TryReadPlayerPistolTransform` copies one selected field
from an existing equipped pistol (355): projectile, visual projectile, cached
physical spawn point, aim, ballistic spawn or visual spawn transform. The last
two read the inherited `physicalSpawnPoint` and `visualSpawnPoint` fields. It
does not call native shooting or aiming methods,
load a prefab, equip an item or synthesize an offset from the player.

Local reads use the current inventory's right-hand instance. Remote reads require
one setup bound to the exact player attachment, one equipped pistol instance and
one matching ranged-weapon module and component. Missing equipment, ambiguous
matches and changed object identities fail the read without retiring an otherwise
valid player binding. Lookups accept at most 32 setups, 32 equipped instances,
32 modules and 8 ranged-weapon components; these limits bound the inspected
results, not Unity's native hierarchy traversal.

The snapshot retains native objects for identity checks and copies the world pose,
source, frame and presentation flags. Discard it after that frame.
`IsCurrentWeaponTransformSnapshot` checks the world, binding, equipment, selected
field, pose, flags, signed trace offset and both physical-spawn references again.
Reads and explicit checks share a 64-call budget per
server frame. Inactive presentation is reported rather than treated as proof of
an invalid player.

These are observations, not trusted muzzle or aim coordinates. A field may be
missing on a dedicated server or may reflect stale animation or client replication.
The selected field is not asserted to be the transform used by a native shot.
Private server policy must qualify and authorize its use before accepting a
launch; no fallback or firing policy is supplied by this API.

Native pistol launch uses `physicalSpawnPoint.position` plus the selected firing
direction multiplied by `_traceForwardOffset`. The snapshot exposes the finite,
signed offset without clamping and reports whether the inherited physical spawn
reference matches the cached projectile physical point. The cached point and
inherited reference remain separate sources; existing enum values are unchanged.
Native visual-spawn selection does not change this ballistic origin calculation.

Native firing may reposition the point immediately before launch, and its
transform getters can create or reconfigure missing points. This read API does
neither. A successful observation does not establish those shot-time mutations,
direction normalization, scatter or zeroing, and does not promise that a remote
snapshot reproduces the local shot. Native damage also changes shared impact
data and mutable node state before owner-side armour; raw ballistic damage is
not final player health loss.

## Remote-player muzzle clearance

`TryCheckPlayerMuzzleClearance` checks the observed body-to-muzzle path and the
muzzle-to-launch-origin offset in the server's default physics scene. The caller
supplies a positive clearance radius, a maximum distance, collision layers and
trigger handling. The radius is a server rule, not a conversion from ammunition
diameter. A completed call returns `Success` and a separate `clear` result;
failures and a full result buffer never establish clearance.

The anchor is the bounds center of the exact remote setup's `_bodyDamageCollider`.
The call requires a live capsule at runtime, one setup associated with the retained
player attachment, and finite geometry within the requested distance. It does not
guess a height above the player origin or fall back to a different collider.
Listen-host local players and other body shapes are unsupported. The native field
exists, but its availability and assignment on a running dedicated server still
need multiplayer acceptance.

Capsule overlaps include both endpoints; a zero-length path uses a sphere overlap.
Each query uses the shared 256-read frame budget and a fixed 32-collider buffer.
Only the exact body collider and colliders with a qualified direct damage-node
route to that same player are excluded. Other colliders remain obstructions,
including unqualified objects parented under the player. Binding, body identity,
anchor, player pose, world and frame are rechecked before returning a result.

This is a bounded observation of current server geometry. It does not prove
authoritative movement, honest aim, recent replication, or that every obstructing
object is represented on the server. The API does not fire, damage or move anything.

## Player damage-node observations

`TryReadHitPlayerDamageNode` copies the direct damage node reached by a current
collision's supported player receiver route. It requires the exact player
attachment, one receiver on the effective hit transform and the qualified
node-to-controller-to-player callbacks. A direct controller does not identify a
unique node and returns `ReceiverUnsupported`; the API does not choose a node
from the controller's list.

The snapshot includes projectile multiplier, absorption, break state, remaining
damage behavior, activity and admission-related flags. Parent, child, validation,
break, block, dismember and team-type references are reported as presence flags
and retained privately for identity checks. It also reports whether the receiver
has the base damage-node runtime type. Finite signed native values are preserved;
nonfinite values return `InvalidDamageNode`. These observations do not establish
that the node's behavior is supported by a particular combat policy.

`IsCurrentPlayerDamageNodeSnapshot` rechecks the original world/frame, collision,
player binding, receiver route, native object identities and all copied values.
Both capture and explicit checks consume one entry from the shared collision-read
budget. Copied values expire after their frame. `NodeInstanceId` is an ephemeral
Unity identifier, not a persistent ownership key.

`IsSamePlayerDamageNode` compares a retained original with a fresh current
snapshot. The original may come from an older frame; it is retained solely to
check the same live node, game object, player binding, world and callback route.
The current snapshot passes the full freshness check, consuming one collision-read
budget entry. This comparison does not refresh old values, freeze configuration
or transfer resource ownership. Policies must qualify current configuration
separately before using a previously created account.

These operations do not invoke impact or validation callbacks, change absorption,
break armour or update health. A copied absorption value is not an authoritative
resource grant. Private server policy must own resource state and separately
qualify node admission, parent processing, team rules, owner armour and final
health damage. Optional callback presence does not describe or approve callback
behavior. No new native damage path is enabled by reading a snapshot.

## Integration boundary

The adapter neither selects enrollment/datagram transport nor subscribes to
events. Ordinary transport and observation calls do not change native inventory,
health, movement or presentation. Native life suppression requires the separate
explicit preparation and acquisition described above, and health projection is
a separate explicit write operation. Local life presentation is also explicit
and must be driven by the mode owner.
The game-specific server owns grants, life transitions, replay accounting,
collision evidence, damage calculation and permission policy.

Admission and combat must share one startup selection owner. When managed mod
admission is selected, compose capabilities through
`ModAdmission.SelectForNextStart`; a separate enrollment selection
would clear that admission selection. Select datagrams only after gated
enrollment and before network startup. Shipping the Client package alone must
not advertise combat support or require vanilla clients to enroll.

Enrollment and package reports are compatibility information, not evidence of
an honest client. This contract makes no multiplayer enforcement claim. The
server must own the complete launch, impact, life and native bypass paths before
a strict mode can be offered.

The Client contains an internal session driver for this integration. It keeps one
exact server peer, queues at most 32 copied datagrams and advances protocol and
native operations through a nonrecursive update. Initial native ownership is a
single attempt against an accepted alive life and an existing held pistol.
Downed/get-up presentation precedes later equipment requests; only a correlated
accepted equip receipt can start one restoration attempt. Respawn requests remain
explicit, and a placed player waits for the matching server outcome before
advancing the native life. Retirement is terminal for the same live connection;
SDK cleanup outlives the Client's update callback where runtime ticks continue.

The driver is started and stopped with the Client, but ordinary admission never
calls its explicit pre-network preparation or offers the combat/respawn mask.
Consequently it does not subscribe to combat packets or acquire native ownership
in the current product flow. Completing server damage, effects, startup selection
and end-to-end mode ownership is still required before exposing it to players.

### Native lung oxygen

Prepare `StrictCombatClient.PrepareNativeBreathingBarrierForNextStart` before
world or network startup, alongside native life preparation. After owning the
local life and receiving provisioned lung state through the negotiated breathing
capability, call `TryAcquireNativeBreathingBarrier`. It uses the existing life
registry and does not require an equipped rebreather. Previously observed native
lung activity prevents late acquisition.

For initial synchronization after ordinary native breathing has already finished,
`TryTakeOverNativeBreathingBarrier` offers an explicit alternative. It still requires
pre-start preparation, an already-owned healthy native life and coherent accepted
server air. It rejects incomplete or exceptional activity history and returns
`Busy` while either intercepted native breathing method is on the call stack.
Completed air drain or recovery is not imported into the server account. Project
accepted air immediately after acquisition. The existing acquisition API continues
to reject prior native activity; neither API reconciles an earlier native death or
respawn transition. A same-object respawn handoff retains this choice.

The owned `PlayerStats` and `BreathingData` identities suppress
`ProcessAirDrain` and `UseRebreather`, including local drain, replenishment and
the mapped drowning path. The capacity getter returns that life's accepted
capacity without changing global survival settings. Unowned objects retain
native behavior. Retired identities remain suppressed; they never fall back to
native oxygen accounting. The suppressed drain call returns false, so native
low-air warning audio and recovery gasps are not supplied by this path.

`ProjectLocalBreathing` writes accepted capacity and air with conservative float
conversion and checks their exact readback. It does not copy native values into
server resources or change tank fields. An uncertain write retires ownership;
it cannot roll back into a changed player. `TryHandoffNativeBreathingBarrier`
requires the same native objects, a committed respawn and accepted new-life
resources. Replacement objects cannot be adopted through this handoff.

This is method-entry containment and explicit projection. Direct field writes,
constructor re-entry, replacement objects and other unmapped writers are not
continuous memory protection. Server recovery and drowning policy, managed
warning presentation and product startup integration are still required.
The internal Client driver uses explicit takeover for its selected breathing
profiles, but ordinary product startup neither prepares it nor advertises lung
ownership. This does not establish complete startup containment or strict readiness.
