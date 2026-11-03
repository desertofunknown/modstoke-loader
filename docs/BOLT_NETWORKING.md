# Bolt networking

The SDK separates traffic admission from observations of native processing.
These APIs work with traffic from vanilla and modded clients without requiring
the client to enroll. Enrollment and strict-combat policies remain separate.
All native hooks require the recognized game binary and safe Harmony support;
an unqualified dedicated-server binary is not enabled automatically.

## API selection

| API | Boundary | Can reject? |
| --- | --- | --- |
| `BoltPackets.Receiving` | Whole game packet before Bolt channel decoding, after enrollment and SDK datagrams | Yes, the entire packet |
| `ServerEvents.Receiving` | Native event dispatch before routing and listeners | Yes, this dispatch |
| `BoltStreams.Receiving` / `ClientReceiving` | Reassembled stream on the server / client before policy | No; storage ownership is unchanged |
| `BoltStreams.Rejection` / `ClientRejection` | Reassembled stream on the server / client before internal consumers and native listeners | Yes, suppresses this stream's consumers and listeners |
| `BoltStates.Received` | Successful full replicated-state read | No; decoded/applied-or-queued observation |
| `BoltStateAdmission.Receiving` | Entity update header before native side effects | Yes, remaining-packet rejection followed by disconnect |
| `BoltProperties.Receiving` | Successfully decoded Boolean, Int32 or Single state property | Yes, restores the previous destination value or selects a typed server-ledger replacement |
| `BoltEntities.Changed` | Entity control and detach operations | No; completion, skip or fault observation |
| `BoltCommands.Received` | Successful full command input/result read | No; receipt is not admission |
| `BoltCommands.Executed` | Successful native command execution | No; includes prediction and replay |
| `BoltCommands.ChannelReceiving` | Command channel before result/input decoding | Yes, remaining-packet rejection followed by disconnect; framework-only |

Subscribe during mod initialization and unsubscribe on unload. Each API exposes
`State` with registration status and a diagnostic description. Installed means
registered, not runtime or multiplayer qualification. Observation records contain
managed copies; they never grant ownership of native objects or storage.

## Whole-packet decisions

`BoltPacketRequest` contains the exact source connection ID, packet
bit size and initial bit position. `TryCopyBytes(offset, destination)` copies
only the requested bytes into a caller-provided span without advancing the
cursor. `ReadableByteCount` excludes a final partial byte and unused pool space.
Access and `Reject()` expire at callback completion and are thread-bound.

`TryGetSourceSession` resolves player session identity only when requested. A
missing session means unknown, not cheating. Connection/socket lifetime and
packet metadata are checked before and after policy; a resolved session is also
revalidated. Numeric connection IDs alone are not durable identities.

Rejecting drops the entire game packet, including unrelated state, scene,
command and event channels in that packet. It does not rewind client prediction,
undo transport acknowledgement, or disconnect the sender. Handler exceptions
also reject. Recursive delivery of the same packet cannot acquire or dispose it
twice; new packets fed recursively from a policy callback are rejected.

This boundary is after UdpKit transport processing. It is not a transport parser,
handshake firewall, or selective state validator. Bolt channels share a bit
cursor: skipping a channel reader would misalign the remaining payload. Prefer
a typed event policy when it provides the decision you need.

## Streams

`BoltStreamRequest.Context` contains the exact source connection ID,
channel ID/name and payload size. Each subscriber receives a callback-scoped
request. `TryCopyBytes` reads a header, slice or complete payload into a
caller-provided span, bounded by that payload's length. `TryGetSourceSession`
is optional and lazy. Source, socket, channel and buffer lifetime are rechecked;
stale or wrong-thread access fails rather than exposing native storage.
Copied `Context` metadata and caller-owned payload copies remain readable after
the request expires; retaining a request does not extend native access.

`Receiving` and `Rejection` are server-only. On clients, `ClientReceiving` and
the separate opt-in `ClientRejection` apply only to streams from the exact current
server connection, rechecked throughout dispatch. Server policies never run on
clients, and client policies never run on servers. No client policy is enabled
by subscribing to the server policy.

Both observation events remain read-only and run before their side's policy.
Subscribe to `Rejection` or `ClientRejection` to call `Reject()` on a live request.
The first rejection stops policy dispatch and suppresses internal accepted-stream
consumers, including migration transport, and all native listeners for that stream,
without disconnecting the peer. Policy exceptions, stale lifetimes, wrong-thread
dispatch and recursive policy delivery fail closed; observation exceptions alone
do not reject. Side-specific observer and policy snapshots apply to the current
dispatch even if subscriptions change during callbacks. Without policy for that
side, invalid context skips observations but preserves native dispatch. With no
subscribers, the hook passes through without additional native reads or allocations.

The matched reliable and unreliable receive paths allocate GC-managed buffers;
the dispatcher and voice consumer have no incoming payload-pool return obligation.
The SDK therefore performs no manual payload free. `State.CanReject` indicates
registered capability, not runtime acceptance. Rejection deliberately skips native
listener work, which can include voice delivery. It is after allocation/reassembly,
does not undo transport acknowledgements, and leaves the earlier reserved SDK
control channel unchanged. It is not a stream-allocation or bandwidth defense.

## Replicated state

`BoltStateReceived` reports actual reader source, entity network and
prefab IDs, state type, wire frame, cursor delta and native frame-list counts.
Entity source and controller are separate fields; neither is substituted for
the reader's source connection.

Native decoding can update an initial/predicted frame or enqueue another frame.
It can also mutate hierarchy while decoding. A successful read is not completed
simulation. Frame counts and the current `Storage` object do not identify the
latest incoming values; this API does not expose them as a validated snapshot.
Value-level rollback is not provided by that observation hook.

### State admission

`BoltStateAdmission.Receiving` runs earlier, at `EntityChannel.ReadUpdate`
before native control tokens, entity creation/detach and state properties are read.
It peeks the continue marker and packed network ID without advancing the cursor.
The request exposes `ClaimedNetworkId`, the actual source connection ID, and an
optional `ExistingIncomingEntity` snapshot from that connection's incoming proxy.
An unknown entity stays unknown; no entity is created to inspect the request.

`RejectAndDisconnect()` stops this update and the remaining packet. The original
receiver retains packet disposal, then the SDK disconnects the same live source.
Skipping an undecoded delta while keeping its connection could desynchronize the
acknowledged replication baseline, so this header API does not offer silent
selective rejection. Scalar policy below instead lets the native value read finish
before changing its destination.
Earlier events, scene processing and entity updates in the packet are not rolled
back. This is header/source admission, not validation of property values that
have not yet been decoded. Use whole-packet policy for decisions that must
precede every channel.

Admission is opt-in and active only inside a matched native receiver with guarded
channel types. Calls outside that scope and unknown channel overrides do not run
the admission policy. Malformed/truncated headers or policy exceptions reject and
disconnect only while this policy is subscribed. No default kick rule is enabled.
Context and target identity are rechecked after callbacks. Session lookup is lazy;
the path performs no unconditional roster scan or payload allocation.

### Scalar rollback and server values

`BoltProperties.Receiving` is opt-in server policy for qualified
`Boolean`, `Int32` and `Single` state readers. The native scalar read is fully
consumed before the synchronous callback; rollback does not skip wire bytes or
rewind the packet cursor. It affects only the supported received server replica,
not the whole state update. Transform, reference and composite properties are
not covered.

`Receiving` sees every supported property of every state. To police one property,
resolve it with `BoltProperties.TryResolveRootProperty<TMeta>(name, kind, out selector, out detail)`,
for example `TryResolveRootProperty<PlayerState_Meta>("Dying", BoltScalarKind.Int32, ...)`,
and register `TrySubscribe(selector, handler, out subscription)`. Resolution reads the
generated state's shared metadata and refuses nested, array, duplicate and differently
typed properties; indexes are build-specific, so resolve them at runtime and never
persist them. While no `Receiving` handler exists, states and properties that no
subscription selects are read natively: they are not captured and cannot abandon the
packet. Any subscription still enables the receive guard for every packet. Dispose
the subscription on the game thread when unloading.

Bolt reads a new proxy's first state before attaching its entity, so both forms police
creation reads too; a client cannot bypass a policy through its create packet.

`BoltPropertyRequest` exposes copied `Previous`, `Proposed` and
`Selected` scalars and a `Decision`. `Previous` is the actual destination value
before this read, not necessarily the displayed value or an authoritative ledger.
`Decision` records the policy request, not proof of application if its native
context becomes invalid or the enclosing read fails.
For an interpolated float, this is the reader's actual interpolation destination
(`Float1` in the next storage slot), not the displayed/current `Float0` value.

* With no decision, the proposal is accepted. `Reject()` irreversibly selects
  `Rollback` for this proposal and restores `Previous`; it does not recover a
  server-authoritative value automatically. Handler exceptions also select rollback.
* `TryUseServerValue(BoltScalar value)` selects a replacement supplied
  from the mode's committed server ledger. It must match `Proposed.Kind`; `Single`
  replacements must be finite, although incoming proposals can contain non-finite
  bits. Construct values with `FromBoolean`, `FromInt32` or `FromSingle` and read
  them with the corresponding `TryGet...` methods. A type mismatch or invalid
  replacement returns false without selecting it.
* Conflicting server-value overrides from subscribers select rollback, not a last
  writer; matching overrides compare exact scalar bits. Once rolled back, later
  server-value selection fails. Existing pending dirty notifications, including an
  earlier accepted duplicate property, are preserved. A rejected/no-op value only
  clears its new notification if the destination still matches `Previous`.

Read-only identity fields include `EntityNetworkId`, `SourceConnectionId`,
`StateTypeId`, `PropertyIndex` and `WireFrame`. `PropertyIndex` is the root-state
index including nested/array offsets; a leaf name from `TryGetPropertyName` alone
is not an identity. `IsLocallyOwned` and `IsSourceEntityOwner` distinguish local
ownership from the actual sender being the nonlocal entity owner, not merely its
controller.

`TryGetSourceSession` and `IsFromSession` use already-observed live membership via
an O(1) lookup, without scanning the roster or inferring membership from an ID.
Unknown stays unknown. Decisions and lookups are callback-scoped and thread-bound;
copied scalar observations remain readable afterward. Policies must not re-enter
Bolt, mutate native state or detach entities. Context/storage failures can abort
the enclosing receive path rather than safely roll back a scalar.

Neither rollback nor replacement transfers Bolt ownership, corrects the owner
client, repairs previously queued frames or prevents future local writes. Strict
combat remains mode-owned: use its existing receipts and projections for owner
correction. The optional scalar correction adapters below support explicitly
authorized properties outside those existing projections. This API adds no
default enrollment, penalties or private Admin policy.
Native acknowledgement and per-property delivery bookkeeping are unchanged;
rejection does not ask the owner to retransmit an unchanged value.
`State` reports registration only, not runtime delivery or multiplayer proof.

## Client scalar correction

`BoltCorrectionServer` and `BoltCorrectionClient` provide
an opt-in, revisioned correction path for an enrolled remote owner. They support
Boolean, Int32 and finite Single properties on the recognized native build.
They do not enable themselves or subscribe to datagrams automatically.

1. Select enrollment and the gameplay gate on both peers before Bolt starts,
   including `BoltCorrectionProtocol.Capability` (bit 14) in the
   negotiated required/offered capabilities. Select `Datagrams` too.
2. The server calls `TryCreate` for the exact peer, entity, life generation,
   state type, root property index and scalar kind. Share the returned `Binding`
   through the mode's authenticated setup. `TryWriteBinding`/`TryReadBinding`
   provide serialization only, not authorization or a binding handshake.
3. The client matches that binding against its authorized current life and
   property before calling its own `TryCreate`. Never create an adapter merely
   because an arbitrary binding arrived. A new life, attachment or mode requires
   a fresh binding, and the previous adapter must be disposed.
4. Forward `Datagrams.Received` to the appropriate `TryReceive` on
   both sides. On the game thread, queue only committed server-ledger values with
   increasing revisions and drive the server adapter's `Update`. A successful
   `TryQueue` means retained locally, not delivered or applied.

Each adapter holds only its latest correction. Superseded pending revisions are
coalesced. Sends are spaced by at least 500 ms, with at most eight queued sends
and an eight-second timeout per revision. A full native send window is retried
within that budget; a failed native send is reported as uncertain. Idle updates
do not resolve entities or scan players. Messages are fixed 64-byte records;
encoding and decoding use spans, with no reflection or boxed scalar payloads.

The client rejects mismatched bindings, ignores older revisions, and answers
duplicates with the cached receipt without applying them again. Reusing one
revision for different values retires the client adapter. `Rejected` and
`Unconfirmed` applications stop further writes on that binding; repeated copies
of the final correction can still retrieve its receipt. Do not recover an
uncertain application by blindly creating another binding and replaying it.

Application follows the local scalar setter's storage/comparison/dirty-bit
semantics, including rejecting animator-driven properties. Interpolated floats
write the owner's current `Float0`, not the receive-side `Float1` history slot.
Existing dirty bits are preserved. Equal values do not force retransmission;
callbacks run through Bolt's normal simulation instead of being drained here.
No historical frames, gameplay components, physics state or server ledger are
rewritten. A later gameplay writer can overwrite the corrected property.

`PeerApplied`, `PeerUnchanged` and `PeerRejected` are remote reports, not proof
of honest software, sustained correction or gameplay authority. `Uncertain`
and `TimedOut` do not imply that nothing happened. Vanilla clients do not support
this protocol; there is no silent fallback. Strict-combat life/readiness barriers
and typed health, inventory and movement authority remain the mode's concern.

## Ownership and lifecycle

`BoltEntities.Changed` observes `AssignControl`, `TakeControl`,
`ReleaseControl`, `RevokeControl`, `Detach`, and the internal take/release paths.
Records include before/after scalar snapshots, outcome, nesting depth and parent
operation. Nested operations report in completion order. A changed snapshot only
means the reported fields changed, not every native side effect.

Server role does not imply entity ownership. The connection receiving control
is not necessarily the requester. Detach and revocation remain uncancellable so
shutdown and ownership cleanup can complete. Failures are observed without
suppressing the native exception; snapshot failures are explicitly unavailable.

`ServerWorld.TryRevokeEntityControl` is a separate, explicit authority
operation. It requires the exact expected controller session, current server-local
ownership, matching attached entity/backing pointers, main thread, running server,
and the recognized native build. It uses native revocation so queue, proxy and
token cleanup stay together. The caller supplies the authorization rule.

`TryAssignEntityControl` adds an explicit controller handoff on an attached,
server-owned entity. Supply the expected current controller session (null means
require no remote controller) and the exact live recipient session. An already
matching recipient is a no-op. Native `AssignControl` does not clean up a previous
remote controller, so a handoff first calls native `RevokeControl`, revalidates
the attachment and both sessions, then calls `AssignControl`. This is not atomic:
if validation or assignment fails after revocation, the entity may have no
controller. The SDK reports `Unconfirmed` and does not attempt a blind rollback.
The same context and native-build guards apply, and the caller still owns
authorization and gameplay readiness.

Results distinguish `Rejected`, `NoChange`, `Revoked`, `Assigned` and `Unconfirmed`. `Revoked`
means a normal native return followed by observing no remote controller on the
same owned attachment. `Assigned` means the requested live controller was observed
after a normal native return. Neither confirms remote delivery. `Unconfirmed`
may include partial native changes and must not be retried blindly.

Control is not ownership. The inspected Bolt build establishes ownership from
the entity's source at creation and embeds its origin in the network identity;
it has no synchronized in-place owner-migration operation. Changing local owner,
source or network-ID fields would not transfer authority on the other peers.
The separate [ownership migration API](BOLT_OWNERSHIP_MIGRATION.md) implements
an opt-in cooperative destroy/recreate lifecycle with explicit state adapters,
reference repair and an all-peer barrier. It is not an in-place handoff or
automatic support for vanilla players, vehicles or other entity categories.
The control APIs above never perform that destructive lifecycle implicitly.

## Commands

The inspected game metadata contains no concrete `Bolt.Command` subclasses.
`AdminCommand` and `debugCommand` are Bolt events, handled by the event API.
The command hooks expose the protocol framework for compatible consumers; they
do not imply that current gameplay sends native command traffic.

Receipt identifies the actual connection but not the target entity. Execution
identifies the entity and its current source/controller, not a proven original
sender. Sequence numbers are not globally unique lifetime identifiers. Result
observations may describe temporary commands immediately freed by the caller.

`ChannelReceiving` adds source-level admission before either results or inputs
are decoded. `RejectAndDisconnect()` requests the same guarded remaining-packet
abort as state admission. The request deliberately has no target entity: one
channel can contain commands for multiple entities. It offers lazy session
lookup and checks exact packet/channel/source lifetime. Both section readers
honor a latched abort. Earlier channels are not rolled back.

This is a framework capability for a compatible native receive path, not a claim
that current gameplay routes this channel. Native queue flags, retirement, storage
ownership and replay remain unchanged. Individual decoded-command denial, silent
queue removal and synthetic success results are not provided: skipping execution
can stall the backlog, and unlinking alone does not correct client prediction.

## Performance and authority

With no subscribers, the new hooks perform no observation reads, payload copies
or per-call SDK allocations. Subscribed paths use copied subscription snapshots
or allocation-free delegate enumeration. Packet bytes are copied only on demand;
there is no packet logging loop or unconditional roster scan. Keep synchronous
callbacks short and queue copied observations for expensive work elsewhere.

Observation callback exceptions are isolated. Policy exceptions reject only the
policy's unit of traffic. No new default rate limits, punishments, gameplay
settings, wire messages or changes to native collision/simulation are introduced.

These are APIs for trusted local mods, not a sandbox for hostile server plugins.
They do not prove client integrity or transfer client-owned health, ammunition,
inventory or movement authority to the server. Event arrays/tokens and arbitrary
native object payloads are not reconstructed by the common event schema. Use
qualified typed APIs where available. Blob transfers and the reserved SDK control
channel retain their own existing transport policies.

Compilation and source inspection do not establish native hook delivery,
vanilla/modded interoperability, rejection cleanup or long-session performance.
Those require separately authorized multiplayer acceptance on the matched build.
