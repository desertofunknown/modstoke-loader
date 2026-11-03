# Cooperative ownership migration

`BoltMigrationSession` provides an explicit migration lifecycle for
an enrolled server and client. A transfer retires the old owner's entity and
creates a new entity under the destination owner. The new entity has a new
network ID and GameObject. This is not controller reassignment or an in-place
change to Bolt's owner/source fields. Ordinary entities use native destruction.
The exact local player rig uses native detachment instead, retaining its
GameObject without retaining the old network attachment. This does not rebind
the rig to the replacement or establish working local controls.

No entity category is enabled by default. A mode must supply a complete
`IBoltMigrationAdapter` for every supported type and version.
The built-in player adapter composition is incomplete and refuses transfer
admission; capabilities 15 and 16 remain disabled in the public Client.
The opt-in GolfCart adapter has a narrower qualified contract; it does not
cover player-occupied carts or other vehicles.
The common mechanism alone does not provide universal entity migration.

## Requirements

- Use the recognized game build, safe Harmony support and original game thread.
- Require and offer `BoltMigrationProtocol.Capability` (bit 15) in
  enrollment on every connected peer. Vanilla clients cannot participate.
- Authorize the exact source, destination and transaction in the mode. A receipt
  digest correlates a snapshot; it is not authentication or proof of honest code.
- Every current client must have the old replica and prepare its local references.
  The server barrier rejects missing, unenrolled, newly joined or reconnected peers.
- Use a recreatable native prefab and serializer. Scene entities and prefabs that
  prohibit creation on the destination client are rejected.
- Select `BoltMigrationTransport` before Bolt starts on every peer,
  or supply equivalent reliable delivery. Snapshots contain up to 1 MiB of
  payload; the SDK's 256-byte datagrams cannot carry them. Do not repurpose the
  limited enrollment/control negotiation channel. Receipts are 112 bytes.

The supported transaction shape is server-to-client or client-to-server, with
other clients participating as observers. Direct client-to-client transfer,
server host migration and atomic migration of a linked entity graph are not
implemented by this API.

## Sequence

1. The current owner calls `TryPrepareSource`. Its adapter stops writers and
   simulation, captures deterministic state and prepares local reference repair.
   Keep the returned session even when preparation returns false: an
   `Unconfirmed` result can mean quiescence was only partially established.
2. The mode distributes the authorized snapshot. The destination calls
   `TryPrepareDestination`; other clients call `TryPrepareObserver` with their
   server peer. Each reports its session's `ReadyReceipt`.
3. The server creates `BoltMigrationBarrier` for that snapshot,
   records its own readiness with `RecordLocal` and each exact peer's readiness
   with `TryRecord`. `TryGetReadyReceipt` succeeds only for a current, complete
   cohort. A server source passes its barrier and null sender to `TryRetire`;
   a client source accepts the aggregate receipt only from its server peer.
4. `TryRetire` re-captures the source state and pose before native retirement.
   Changed state blocks retirement. Send its `Retired` receipt to the destination.
5. The destination calls `TryRecreate`. A server destination also supplies its
   readiness barrier. A client destination supplies null. This call can report
   waiting until ordinary native removal has cleared the old incoming proxy.
   Once ready, it creates a fresh owner, restores state, and repairs local
   references while leaving the replacement quiescent.
   An adapter with `IBoltMigrationReplacementAdmission` can instead
   report `InitializingReplacement`. Poll `TryFinishRecreation` with the same
   barrier until native startup is ready. Do not instantiate another entity or
   treat a pending result without a receipt as `Recreated`.
6. Forward the `Recreated` receipt to the old owner and observers, through the
   server for observer clients. They call `TryRepairReferences` after the new
   replica arrives. Record all their `Repaired` receipts, plus the destination's
   `LocalRepairReceipt`, in the original server barrier.
7. `TryGetRepairReceipt(newId)` produces aggregate repair authorization. The
   destination calls `TryComplete` to resume. A server destination passes its
   barrier and null sender; a client destination passes its server peer and null
   barrier. Ordinary source and observer `Completed` statuses mean only local
   repair, not that the new owner has resumed. A retained player source and
   source/observer adapters implementing `IBoltMigrationReplicaLifecycle`
   stay `ReferencesRepaired` until `TryResumeParticipant` receives aggregate
   repair authorization for the same replacement. `TryResumeRetainedSource`
   delegates to that method but accepts only the original retained player source.

Native initialization and `Attached` callbacks run before adapter restoration.
An adapter must reject types where those callbacks cannot safely run in this
sequence. The API never reattaches the old GameObject: a late removal packet can
otherwise destroy an already-reattached object. Retaining the source's local
player rig is not reusing a remote proxy or adopting a new network identity.

`IBoltMigrationReplacementIdentity` receives the exact new ID only
after its correlated `Recreated` receipt is authorized. Early delivery records
the ID while ordinary old-object removal finishes. A later call may establish
the exact replacement's hold; repeated calls must not replay native effects.
The coordinator handles this ordering before read-only replacement admission.
Position or prefab equality alone never identifies a replacement.

## Reliable delivery

`SelectForNextStart(true, out detail)` selects the dedicated reliable stream
`Modstoke.OwnershipMigration.v1`. It does not enable or reset mod admission.
Both sides must separately require and offer capability 15 and register the
same channel order: negotiation uses ID 1 and migration requires ID 2.

`SendSnapshot` and `SendReceipt` validate the exact enrolled peer and native
stream configuration. `Queued` means only that the native send returned;
`Unconfirmed` means a send was attempted and must not be blindly retried.
Incoming managed snapshots and receipts are delivered on the game thread,
after the SDK's server rejection policies. Each consumer must still authorize
the peer, transaction and phase. Client delivery accepts only the current server.
There is no automatic transfer, retry, bandwidth change or admission policy.

Native reliable-block reassembly carries the snapshot. The transport checks
native block/header sizes against the send buffer before queuing. Its receive
limit applies after native reassembly, not before native allocation.

## Player identity

`PlayerBinding.PlayerSession` identifies the client whose character
was captured. `SourceSession` continues to report the actual Bolt source; it
is null for a server-owned character. Do not use either property as proof of
trusted movement or input. Capture a new binding after an attachment changes;
an old handle never follows a new entity automatically.

For a client-to-server player migration, `TryPrepareDestination` automatically
reserves the player identity before producing readiness. Calling
`TryPreparePlayerIdentityMigration` for that same session returns the existing
reservation. This records the exact session and
attachment, not a reusable connection number or Steam ID. After every peer has
repaired references and the original client has been assigned control,
The built-in player coordinator publishes that reservation while preparing
the inventory endpoints after aggregate repair; `TryComplete` checks the same
identity before resume. Other adapters publish it during `TryComplete`.
It updates the native connection's player reference and the qualified player
lookup hook. Failed or uncertain publication is not replayed. Cancellation is
possible only while the original attachment is intact; `TryAbort` releases the
reservation and disconnect/shutdown expires it. A later native respawn releases
the migrated mapping only when the old attachment is gone and native UserData,
source and both entity registries identify the same new player. Old bindings and
bridges are retired, not retargeted to that new life. This is not automatic
server-owned respawn or another migration of the server-owned character.

This identity participant does not implement the player's adapter. The stock
player attaches its network component to an existing local rig, whereas the
network prefab's attachment callbacks choose local/remote setup from ownership.
Server recreation now routes the carrier's local attachment branch through
remote presentation setup and prevents its `UpdateState` from reading the
host's local-player globals. This routing applies only inside a qualified
player recreation; it does not change native ownership or ordinary players.
A complete implementation still needs mid-transfer committed-event routing,
complete inventory actions, native mode transport and entry/exit, survival/death
handling and player-occupied vehicle authority.
Assigning Bolt control or setting `LocalPlayer.Entity` alone is insufficient.

Every player adapter must implement `IBoltPlayerMigrationAdapter`.
Its read-only lifecycle preflight runs before quiescence. On the source,
reference repair calls `TryRebindRetainedPlayer` and leaves input paused; only
aggregate-authorized resumption calls `TryResumeRetainedPlayer`. An uncertain
rebind or resume is not retried. The built-in role path supports a retained
client player transferring to the server, not server-to-client player migration.

## Player input and state

`PlayerBridge` uses the existing unreliable datagram transport,
not the lifetime-budgeted negotiation stream. Both endpoints must explicitly
select datagrams and require/offer capabilities 15 and 16. Public bridge creation
requires a completed migration and its exact current attachment/controller.
The built-in coordinator can prepare an inventory-only endpoint after exact
local repair, with the server also requiring all-peer repair. This endpoint
cannot carry motion or publish equipment while `IsGameplayActive` is false.
Only completed migration activates gameplay; endpoint preparation does not
release native holds or report completion.
It carries movement/view/button intent toward the server and acknowledged
pose/velocity toward the controlling client. It does not read hardware input,
simulate movement, apply transforms, or grant authority to client-reported state.

The records are `PlayerMotionInput` and `PlayerMotionState`, with
`PlayerMotionFlags` on the state. They were `PlayerInput`, `PlayerState` and
`PlayerStateFlags` until the names clashed with the game's
`Sons.Player.PlayerState` for any mod importing both `Modstoke.Sons` and
`Sons.Player`. A same-named forwarder would keep that clash, so the old names
are gone; no Modstoke mod referenced them. Only the C# names changed; the
wire layout and protocol version are the same.

Version 5 ground input frames occupy 80 bytes and state frames 96 bytes. Both peers'
mode contracts must explicitly agree on this version before migration; capability
16 alone does not establish wire compatibility. Input includes a monotonic
accepted-jump ordinal so repeated edge jumps remain distinct while the native
jumping flag stays set. Zero retains button-edge input producers. A separate Run
press marker survives packet loss without treating held sprint or auto-run as a
fresh press after exhaustion. The codecs validate
version, identity, finite values, ranges, reserved fields and flags. The bridge
rejects stale/replayed sequences, wrong directions and mismatched lifetimes.
`TryTakeInput` consumes a new sample once; `TryReadCurrentInput` supplies the
latest movement intent between packets, expiring after 250 ms. Consumers must
stop movement when no current input is available. Simulation uses server timing, never a client's
claimed delta as authority. A mode must implement edge-triggered actions and
prediction/reconciliation; this intent transport is not a complete action API.

After successful authoritative simulation, call `TryCommitSimulatedInput` for
the exact input that was read. `SendState` requires that committed sequence and
publishes its acknowledgement once. Merely reading an input is not evidence
that it was simulated. Publish the pose at that input boundary, not a later
pose produced by several repetitions of the same latched input.

`PlayerMotionHistory` retains 128 post-input predictions in a fixed
buffer. It prepares translation and velocity offsets against an exact
acknowledgement and shifts later predictions only after the caller commits a
fully applied correction. This prevents applying the same historical error
twice. Missing history or an excessive correction requires explicit
resynchronization; it never silently teleports a player. Invalidate the history
after a teleport, respawn or uncertain native write. It does not rotate the
camera, replay collision simulation, or apply a correction to a body itself.

`PlayerClientMovement` can sample the retained native controller and
apply bounded body corrections. Capture its rig before detachment and explicitly
start it with a completed bridge and ground-mode predicate. It records resolved
post-physics positions rather than render-interpolated transforms. Uncoordinated
mode changes, missing history, obstructed corrections and uncertain writes stop the binding;
it does not teleport through obstacles, rotate the camera or disable the original
controller. This is not deterministic replay or parity with the vanilla motor.
The bridge also checks retained root/controller/body lifetimes while idle.

`TryBeginHangGlider` and `TryBeginGolfCartSeat` provide explicit native-mode
sampling and reconciliation after validated entry. Their input and state use
the same exact-peer bridge, with 96-byte input and 112-byte state records.
Both peers must first settle the same `NativeModeKey` through the ordered
proposal, server decision, client application and receipt exchange. The 88-byte
transition messages retry independently of continuous input. Direct key changes
without that pending transaction are refused. Mode, transition, anchor and seat
identities are carried together; boost and brake remain separate continuous levels.
`TryResumeGround` requires committed exit. These paths retain original
controls, guide/seat writers and cameras. Native server simulation and ordered
enter/exit gameplay actions are still required; continuous packets cannot
authorize those transitions. Ground packets from an earlier mode are rejected.
Cart seat correction does not simulate or reconcile the cart itself.
Rope, wall and crane modes have distinct keys with an exact anchor and no seat.
Their state carries world position and velocity; cart-local seat constraints
do not apply to climbing. Native action admission and animation ownership are
still required before those modes can run.

The native `FirstPersonCharacter` controller cannot currently be used as a
remote server motor: its fixed update and target-velocity calculation read
the singleton local player's state. The controller's movement modes and
action publishers still need separation from those globals before migrated
player simulation can be enabled without affecting the listen-server host.

`PlayerGroundMovement.TryStart` supplies an explicit server-only
ground motor for a completed migration. It uses Unity's
[CharacterController.Move](https://docs.unity3d.com/2022.2/Documentation/ScriptReference/CharacterController.Move.html)
for collision resolution and the server's fixed timestep for walking,
sprinting, crouching, gravity and jump edges. It never reads `LocalPlayer` or
replaces a client controller or camera. The adapter supplies server-owned
settings, preserved velocity/crouch state and an authoritative ground-mode
predicate. An obstructed standing capsule cannot uncrouch. Input expires
after the bridge's deadline; client-provided elapsed time cannot accelerate
simulation. Acknowledgements contain the exact post-input boundary, including
when the transport is temporarily full.

This motor requires an unparented carrier in the default physics scene with
no competing solid collider, rigidbody or first-person controller. It owns
and removes only the native character controller it creates. The adapter
must exclusively own root motion and stop or replace this motor on a mode
transition. `TryEnableSwimming` adds a separate actor-local Rigidbody/capsule
motor with server water queries, exclusive ground/swim activation and post-physics
acknowledgements. A server-supplied water-jump profile also enables native surface
jump math, contact/edge qualification and cooldowns. The client sampler observes
accepted native launches without invoking them twice. Server-owned profiles and
action admission remain required; no host `LocalPlayer` globals are swapped.
This does not implement climbing, vehicle entry, general stamina authority or
client-side collision replay. It is not a complete
vanilla player adapter and does not enable a transfer action.

The bridge uses bounded burst allowances around its 50 Hz average so minor
scheduling jitter does not discard every other packet. Invalid and stale
packets do not consume valid receive allowance. `WindowFull` is a pre-send
rejection and does not consume an input or state sequence; uncertain native
send failures still must not be replayed.

No complete vanilla player transfer or automatic transfer is enabled.
The public Client does not advertise these unfinished player capabilities.

## Adapter coverage

The adapter is the preservation contract, not merely a Bolt serializer. It must
cover all applicable state: gameplay and inventory, save identity, ownership and
controller rules, transform and rigidbody bindings, animation, parenting,
subscriptions, external component caches, and references to or from the entity.
Use independent deterministic bytes, never raw native memory or object pointers.
Opaque protocol tokens, trigger history and transform objects require explicit
type handling; shallow storage copies do not preserve them correctly.

`PlayerMigrationAdapterFactory` creates transaction-scoped
`PlayerMigrationAdapter` instances composing retained source,
replica, snapshot and inventory participants. Construction does not enable
enrollment or establish readiness. `ReadReadiness` and participant admission
remain mandatory and currently do not permit full player transfer.
The built-in player contract is version 4; old contract participants cannot
silently join the revised movement/inventory transport.
`TryBindCompletedInventory(session, result, out detail)` binds only the exact
completed session's inventory custody, not movement or full player activation.
The coordinator prepares that custody before resume and waits for its baseline
handshake, avoiding a dependency on completion to create the required endpoints.
Source preparation starts after local repair; aggregate repair authorization
is retained separately until resumption. Native gameplay coverage is still
required before any writer can be released.
Inventory, physics and survival approval are separate server decisions; none overrides
missing native writer holds or action-fence coverage.
The survival observation is PSV3 (956 bytes), including the source actor's water
masks, tag filter, ocean mode, swimming thresholds and initialized stat rates.
Its water mapping requires
separate approval against the server's loaded scene and water providers; cave
handling is not interchangeable with a below-terrain test.

`PlayerPreservation` captures and restores an explicit subset of
typed player state, including array contents, nested item data and qualified
internal transform bindings. It rejects opaque tokens, delegates, unresolved
references and incompatible layouts instead of dropping them. Getter-only
animation scalars use qualified Animator setters and native parameter pull,
with both Animator and PlayerState readback. Curve-controlled mismatches and
invalid parameter bindings reject before writes. Pending triggers, full transform history,
inventory instances and lifecycle state are not covered;
`SupportsCompletePlayerState` remains false.

`PlayerRigBindings` captures the original local rig and repairs
known behaviour, stats, voice and `LocalPlayer` caches once after retirement.
The original camera, input controllers, inventory, body and their parents must
retain their identities. It never replays `LocalAttached`, rewrites native
ownership or restores detached subscriptions. Native animation detachment cancels
delayed sends. The migration kernel observes completion of the captured setup
and animation detach callbacks before declaring local retirement successful.
Animation state and publication still require preservation after rebinding.

`PlayerEquipment.TryCaptureSource` retains observers for the
original rig's held-item, slot-transfer, lighting, clothing, armour and race
callbacks after the preservation baseline has been established. It does not
remove the native delegates or write nonowned player state. After completed
migration, bind that capture to the client bridge and create the server
publisher with `TryCreateServer`. The loader pumps explicitly created
endpoints; bridge retirement tears their subscriptions down.

The publisher requires an inventory/appearance authorization policy, including
the server's exact held-item instance when applicable. Catalog membership is
not proof of possession. Ordered changes use a bounded queue, receipts and
duplicate suppression; an uncertain native mutation is never replayed. Queue
overflow, rejection or a lost receipt deadline stops the endpoint and requires
explicit recovery. This covers the named callbacks, not a server inventory
ledger, independent item-module updates, held-only ownership or respawn.

`PlayerInventoryAuthority.TryCreateServer` supplies a separate,
initially empty inventory scope and owns its equipment endpoint.
Trusted server code grants exact instances, reserves equipment transitions and
commits them only after native publication readback. Distinct account keys can
represent repeated item IDs, and accepted nonunique stacks retain their quantities.

`PlayerInventoryBaseline` captures an untrusted initial observation,
including native item-module save data, equipment, clothing and armour condition.
Baseline version 2 also fingerprints exact container volume, limb freshness
and blood colour that native item serialization does not fully preserve.
`TryAcceptUnverifiedExistingInventory` admits that baseline only into an empty,
current server account after explicit approval of its exact fingerprint and an
operator reason. Keep the captured inventory suspended through approval and
binding. Approval accepts the initial state; it does not verify earlier item
acquisition. Client counts, audit reports and automatically echoed fingerprints
are not approval, and queued delivery is not proof of client acceptance.

`TryPrepareMigration` creates a single-use `MigrationHandoff` that preserves the
accepted records and transaction floor for the same peer's next completed bridge.
Disconnect or load invalidates it. This does not enforce every vanilla inventory
mutation or reconstruct death bags and respawns. `CoversVanillaInventory` remains
false; preserved module data does not imply every module can be published as equipment.

Armed inventory enforcement also advances detached perishable simulations on
the server and projects exact module state to the retained client inventory.
Native restoration callbacks run before the exact decay timer is assigned,
including nonpositive timers. Timer-only publications are coalesced; state
changes remain eligible immediately. Qualified equip/stow requests use exact
instance custody transactions. `TryQueueStorageWithdrawal`, `TryQueueStorageDeposit`
and `TryQueueWorldDrop` support narrow plain-item transfers, with independent
server access approval, exclusive upstream routing and exact receipts.
`TryQueueWorldPickup` is obsolete and always returns false: `DestroyPickUp` is
downstream cleanup, so pickups are reserved only by typed world intents.
`TryReserveCraft` reserves qualified recipe custody; it does not
certify all crafting effects. Consumption, power and death helpers likewise do
not establish complete action routing or survival/death ownership. General
inventory coverage and save reconstruction remain incomplete. Inventory messages
use version 6 and 184 bytes; grant frames preserve supplemental module state.
Peers must use matching builds.

Typed world intents use 160-byte MPIWV1 frames and reserve custody before native
pickup, stored drop or storage transfer. They support one plain, module-free,
nonunique unit, with server reach and clearance approval. Held throws, compound
pickups and derived pickup effects remain unsupported. A downstream cleanup event
cannot authorize another inventory grant.

Crafting uses 256-byte MPCFV3 frames. The source confirms the exact reserved recipe
before server mutation. Qualified native mat writers retain ingredient custody;
result presentation reuses authoritative grants instead of creating items again.
Settlement waits for native completion and result collection. Only fixed-result
CraftNewItem recipes are supported; reusable containers, dismantling and item
upgrades need their own routes. Missing callbacks retain the reservation rather
than synthesizing completion.

Consumption uses a 348-byte version 7 frame with exact source-before/server-after stats and
reserved item debit. Two cached 240-byte fragments transport it within the
unchanged 256-byte datagram limit. Native edible entry queues intent before
removal or effects. The server reserves custody, waits for client debit, applies
supported numeric effects against the current server values once, then requires
client projection and native presentation completion before settlement. Prepare
reserves custody; its preview values do not freeze stamina or determine the later
commit. Retries retain the original debit and the immutable committed projection.
Stored unique and perishable instances and qualified
container portions retain their exact module state. Container portions require
the authored server prefab's edible-use count, not a client-provided value.
Missing native prefab definitions refuse preparation rather than using guessed portions.
The consumption owner must be explicitly bound to approved server stats with
exclusive stat writers. Stowed Inventory-view intake uses the original native
animation, prop cleanup and completion callbacks. Negative-health/life effects,
held/world intake and mixed volume/perishable modules remain unsupported.
Continuous authoritative stat/HUD projection is still a separate integration requirement.
This does not establish full inventory coverage.

`PlayerMigrationSnapshot` version 3 correlates the inventory and
animation capture with an immutable `PlayerCarrierProfile` and
canonical survival observation. The factory requires a separate survival-approval
policy. Approval restores that exact observation to the held carrier once; a
partial native restore cannot be retried as though no mutation occurred. Capture
the profile's resume values immediately before pausing the original rig, not
the temporary zero velocity or kinematic values used during the pause. Native
profile observations require their own server policy; approving an inventory
fingerprint does not approve client-supplied physics parameters.

`PlayerCarrier` prepares only the new server-owned presentation and
physics body. It observes ordinary equipment startup instead of replaying
`Start` or rebuilding subscriptions. `TryStartNative` adopts that exact body,
capsule and contact tracker for ground/air/swim movement and actor-local stamina.
The motor must be disposed before the carrier. Startup admission is separate
from lightweight fixed-step identity checks. `PlayerAuthorityDynamics`
supplies actor-local ground/swim conditions and stamina from server-owned stats
without running host-local player updates. Survival progression remains separate.
`PlayerSurvivalRuntime` advances actor-local numeric survival with the
consumption owner's exclusive cooking clock. It requires approved settings and
a server-owned environment reader. Retained frames preserve elapsed time while
inventory custody is pending. The aggregate must also serialize stamina writers
and own damage, life transitions and source presentation; this runtime does not
enable full gameplay by itself.
`PlayerSledAuthority` supplies actor-local entry, dry/wet riding and
exit physics to the retained ground motor. Durable source action and wire
integration remain incomplete. These implementations do not complete
native mode transitions, step-helper behavior or full player lifecycle coverage;
unsupported features must not be silently disabled to admit a transfer.

Player action-fence admission currently rejects before any pause writes. Bounded
routing of native committed events during retirement and repair is not
implemented. Do not bypass that refusal or replace it with an unbounded queue;
committed damage/actions cannot simply be discarded on overflow.

For completed bridges only, `PlayerEventRouting` canonicalizes old/new
identities during scoped native decode and dispatch of `HitPlayer`, `PlayerAddItem`,
`ItemRemoveFromPlayer` and the collector reference in `DestroyPickUp`. A pickup
cleanup event is not converted into a new item grant, and its world target is
unchanged. Routes belong to the exact completed session and run;
native queue ordering and event storage ownership are unchanged. This does not
buffer committed effects during transfer, authorize inventory mutations or make
the action fence ready.

`VehiclePreservation` provides typed GolfCart and linked LogSled
captures, plus a single-use restore point for known fields on the **same**
attachment. Restore requires frozen Bolt state, an inactive hierarchy and
caller-managed writer suspension. Occupied/reference-linked vehicles, changed
save records, changed sled contents and opaque item data reject. It does not
restore a newly created vehicle or transfer occupants,
physics history or network transform history. A successful known-value
restoration is not a successful ownership migration.

`TryPrepareCartSaveHandoff` separately preserves an empty, dry, quiet GolfCart's
local save record across destruction and replacement. Startup observation must
have seen its native initialization. The first eligible preparation arms the
qualified writer hooks and returns a refusal; retry on a later game frame.
Preparation suspends the supported cart, water and buoyancy writers and pauses
timers. Active audio, pending coroutines and unsupported references reject.

The handoff makes an independent save-record copy before unregistering the old
saver, because native destruction can mark its own record destroyed. Cancellation
restores the original registration before retirement. `TryBindReplacement` accepts
only a same-prefab owned replacement after source destruction and suspends its
supported writers. `TryRestoreReplacementKnownValues` restores cart/body fields,
hierarchy poses, five transform bindings/render buffers, paused timers and supported
dry local caches. Its readback must succeed before saving can resume. Unknown
records and conflicting save slots reject rather than overwrite existing data.

After confirmed cancellation, `TryResumeCancelledSourceWriters` restores the
original source's activation and writer policy. After replacement restoration and
save resumption, `TryResumeReplacementWriters` performs the corresponding local
resume. The caller must first satisfy its remaining cross-peer/reference barriers,
suspend external writers and stay outside save/load work. These operations do not
certify full vehicle quiescence, preserve occupants or native physics/replication
history, or add LogSled migration support.

`VehiclePreservation.GolfCartMigrationAdapter` combines qualified
cart preservation, retained Vail/IK writer holds, local save custody and deferred
replacement startup. It is transaction-scoped and opt-in, not registered by the
loader. Without an explicit player-runtime provider, player seats and driver
ghosts reject. Unknown inbound references and unqualified action/lifecycle states
also reject. Only the exact authenticated replacement can be
held; unrelated carts at the same position are not selected. Its source and
observer writers remain held until aggregate repair. This is not universal
occupied-vehicle support and has no multiplayer acceptance yet.
`CartPlayerMigrationRuntime` retains the existing source sampler, rig and camera,
yields the exact old driver authority, and prepares replacement driver, passenger
and proxy bindings. Its same-seat anchor rebind requires the cart session's active
authenticated all-peer repair authorization; it never fabricates a ground-mode
transition. The caller must still provide actual scheduler and lossless committed
event barriers. Moving-cart admission, passenger exit and repeated transfer of
active passenger/proxy policies remain unsupported.

`TryRestore` must translate self-references from the old network ID to the new
one and leave the replacement quiescent. `TryResume` is called only after the
all-peer repair authorization, or to cancel an intact source before retirement.
The mode must prevent concurrent changes to the reference set, or its reference
participant must detect newly added references and reject incomplete repair.

`BoltMigrationReferences.TryCapture` is a helper for an explicit list
of locally owned Bolt `Entity` and `NetworkId` properties. Each slot names the
holder, state type and root property index, including nested/array offsets. It
validates all current destinations before writing packed IDs, preserves dirty
notifications and refuses diverged slots. It does not discover references,
repair Unity component caches, reparent transforms, copy historical frames, or
implement an entity's complete adapter. Empty lists explicitly mean zero known
Bolt slots, not proof that no other references exist.

## Failure handling

`LastResult` records the last accepted phase; a rejected call returns its reason
without changing that phase. `MutationAttempted` describes local work only.
`Unconfirmed` may include partial destruction, creation, restore or repair.
It is terminal for automatic progression. Keep the session, snapshot and new ID
for diagnosis; do not create a new session or blindly destroy a replacement.

Waiting for ordinary replica removal or arrival does not execute mutation and
may be retried. Accepted native creation/destruction and reference repair are
not replayed. Duplicate receipts return cached phase evidence where applicable,
not a new claim about current native state.

Cancellation is only for an intact prepared entity. Revoke distributed readiness
and cancel the server barrier before calling `TryAbort`; then distribute its
`Aborted` receipt. Recording that receipt invalidates a barrier. Do not cancel a
participant while the source is authorized to retire. Disconnects, cohort changes
and failures after retirement have no generic rollback.

There are no automatic world scans or migrations. The ground motor's fixed
loop is empty until explicitly started for a completed player bridge. Transport
subscriptions exist only after explicit selection; the identity hook leaves
ordinary player lookup unchanged without an explicitly committed reservation.
Payload hashing and cohort/reference validation happen only at explicit phases.
Compilation does not establish multiplayer correctness. Coordinated migration,
disconnect handling and preservation for each concrete adapter need separate
in-game acceptance before enabling the feature in a mode.
