# Modstoke Public Mod Platform Direction

Developer: Alex Cooper

Modstoke Loader should be a public Sons Of The Forest modding platform first.
The admin, server-management, and anti-cheat tools are important first-party
consumers of that platform, but they should not define the shape of the public
SDK or the in-game Mods menu by themselves.

## Design Rule

Build the loader UI and SDK as general modder infrastructure:

- Mods menu: public inventory, config, diagnostics, credits, and future mod
  actions.
- Admin panel: private or privileged provider that plugs into the same menu and
  SDK extension points.
- Security/protected-server state: disclosed runtime capability and server
  policy status, not hidden UI behavior.
- OpenACAI patches and compatibility fixes: built-in platform services that any
  future mod can observe where appropriate.

If a feature only helps Modstoke Admin, it should either become a
private admin provider or be generalized before it enters the public loader.

## Server-First Capabilities

Compatibility with earlier loaders is a baseline, not the endpoint. The SDK
should let mods build capabilities beyond those loaders, with server hosting
and administration as a primary use case. Modstoke Admin is a paired
server-hosting toolkit and a first-party consumer of those public capabilities.

The default server path must support players using the unmodified game. Only
the server installs the loader and toolkit. Features on that path must use
messages, replicated state and presentation that existing clients understand;
custom packets alone cannot teach an unmodified client a new behavior.

Implementation priorities are:

1. Real server messaging: announcements, targeted replies and group delivery
   using verified native message paths, without impersonating a player. Sending
   must report unavailable sessions and invalid recipients honestly; queueing a
   native event is not proof of delivery or display.
2. Server sessions and permissions: connection-bound player identity, lifecycle
   events and authorized commands. A client-supplied name, sender field or claimed
   role is not an authority source.
3. Gameplay validation: cancellable requests at verified server-controlled
   mutation points, with actor, target and relevant world context. Observation
   after a change has replicated must not be described as prevention.
4. Toolkit policy: moderation, rate limits, automation and auditable enforcement
   built on these hooks. Game-specific detection policy stays in the private
   admin toolkit; general access and enforcement primitives belong in the SDK.

Each feature must state whether it is server-only, uses a native client feature,
or requires an optional client component. Client-only visuals, new client code
and inspection of client-local state are not part of the no-download guarantee.
Server-side anti-cheat is bounded by what the server can observe and control;
the loader must not claim universal detection or prevention. Client attestation
is a separate disclosed, opt-in capability, not a prerequisite for this path.

Public APIs should use OpenACAI contracts without requiring consumers to depend
on RedLoader or SonsSdk. Status providers and planning APIs do not count as
implemented transport or enforcement. Acceptance requires the paired toolkit
to exercise the real operation and unmodified clients to receive the expected
result. These are development requirements, not claims of current support.

### Server Connection Lifetimes

`ServerSessions` tracks the server's remote connections, including
vanilla clients. It does not require the optional notice handshake. `Sessions`
is a read-only membership snapshot containing stable managed handles. A handle
has a unique `SessionId`, the native `ConnectionId` and `FirstObservedAt`; it is
never reused for another observed connection lifetime. The local host player
does not have a remote session handle.

Use the handle or `SessionId` for connection-scoped policy state instead of a
native connection ID that can be recycled. The GUID is a correlation key, not a
credential. `IsCurrent` reports last-observed liveness while tracking is enabled,
not a real-time network guarantee. Closed handles remain closed and retain their
managed metadata. `ClosedAt` records observation time, not the peer's disconnect
timestamp.

`RegisteredSteamId` is refreshed separately from handle identity. It may be
absent, appear later, change, or disappear; listen-server registration is not
guaranteed. This is the game's registration table, not an independent Steam
authentication or permission check. Membership and metadata are refreshed on the
game thread, normally once per second and when resolving a request's source.

`Opened`, `Closed` and `RegistrationChanged` are delivered on the game thread
after bookkeeping, outside the native connection callbacks. They describe
observed transitions: a short-lived handle may already be closed when its
`Opened` notification arrives, and metadata can have advanced by delivery time.
Subscriber exceptions are isolated. Unsubscribe when shutting down.

Chat, item-drop, storage, structure-hit, player-hit, Vail-hit and dismember-hit requests expose
`SourceSession` for their exact live source connection. This is null for server-local events,
departed sources or unavailable tracking. Their separate `RegisteredSteamId` remains an event-time
copy even when the referenced handle's metadata changes later. Tracking failure
does not disable these policy hooks; consumers decide how to handle a null session.

Disconnect invalidates the handle before game disconnect callbacks. A connection
still awaiting removal from the native list cannot be rediscovered as live.
Both native shutdown paths clear tracked sessions, and startup begins a new
observation period. All required hooks must register before tracking is enabled;
a registration failure keeps it off for the process.

A native read failure or off-thread lifecycle callback suspends tracking rather
than retaining apparently live sessions. While suspended, `HooksInstalled` is
false, `Sessions` is empty, handles are noncurrent and identity, role, endpoint
and disconnect calls return `Unavailable`. Policies that need a source session,
such as named-state and printer requests with subscribers, keep rejecting remote
requests. After one complete frame without a new failure, tracking rebuilds its
membership from the live client list: handles whose exact connection is still
live become current again, departed ones close with `Closed`, and new
connections open new handles. Queued notifications are delivered after
recovery. Tracking recovers at most three times per server start; after that it
stays suspended until the next start. While tracking is `Recovering`, server
control peers are held rather than closed: the negotiation transport checks
each one against its exact native connection, and a disconnect seen meanwhile
still closes it. A connection that opens during the pause gets its
control peer once tracking resumes, and the enrollment gate holds such
connections instead of rejecting them. Suspension closes server control peers
as before. Read `HooksInstalled` when deciding
rather than caching it. `State.Status` and service key
`openacai.sons.serverSessions` report `Installed`, `Recovering`, `Suspended`,
`Disabled` or `Failed` with a detail string. Only the first suspension and the
first exhausted retry budget are logged.

No native connection wrapper is retained in a public handle. Live reconnect,
shutdown and metadata transitions still require multiplayer acceptance.

`Disconnect(session)` requests the game's native disconnect on the game thread,
after checking the exact handle against current native client membership. It
works for vanilla clients and does not require a registered Steam ID. Results
distinguish unavailable tracking, wrong thread, non-server context, a stale or
missing session, a previous attempt and native failure. `Requested` means the
native call returned, not that the player has left; liveness changes only when
departure is observed.

Only one native disconnect attempt is made per session. An exception may occur
after work was queued, so another call for that still-current session returns
`AlreadyAttempted` rather than retrying blindly. Server mods must make their
own permission and policy decisions; no disconnect is an automatic moderation
rule or a saved ban.

`DisconnectWithNotice(session, messageKey = "HOST_KICKED_YOU")` adds the game's
native kick token with its Banned flag set to false. It targets the same exact session without requiring
a player entity, Steam registration or client extension. It does not change
roles, update a ban list, force a server save or retry without a token.
`Disconnect(session)` keeps the original tokenless behavior; both methods
share the single-attempt guard, so switching methods cannot send a second
disconnect for the same session.

Keys must be nonblank single-line strings of at most `MaxDisconnectMessageKeyBytes`
(256 UTF-8 bytes). Controls, line separators, angle brackets and malformed
UTF-16 are rejected without trimming or truncation. `InvalidMessageKey` returns
before native access and does not consume the attempt. Existing result values
are unchanged. Token preparation failures report `Failed`; after reservation,
any native failure retains the attempt because the request may already have
been queued. This API's failure logging records only the exception type,
not the key.

This is not an arbitrary-text message channel. The vanilla client treats
`messageKey` as a key in its `Multiplayer` localization table, stores the
localized result in `SteamClientConfig.KickMessage`, runs its normal quitting
callbacks and returns to the title scene with the built-in KickedFromGame
dialog. That dialog uses configured localized content; the inspected path does
not directly pass the token's resolved text to it. A different key therefore
does not guarantee different visible text. Key existence is not validated by
the server API, and missing keys may produce a localization diagnostic or an
empty result. No literal-text fallback is supplied.

The native client logs the disconnect token, including its message key; do not
put secrets or private data there. Normal quitting callbacks may have their own
side effects, so the API does not promise a save-free client shutdown.

`Requested` confirms only that the native call returned. It does not establish
that the client received the token, displayed a notice or finished leaving.
The client owns presentation. Native disconnect and display behavior still
need multiplayer acceptance.

### Server Connection Telemetry

Use `ServerSessions.TryReadConnection(session, out snapshot, out status)`
to read a current remote connection without a client extension. It requires the
game thread, a running server and working session tracking, but no Steam
registration or spawned player. A null or departed handle cannot select a new
connection that happens to reuse its native ID. The same connection and
transport must remain present when the read finishes.

The immutable `ConnectionSnapshot` contains:

- `Session`: the exact requested lifetime handle, which may later close.
- `NetworkRoundTripSeconds`: the transport's smoothed RTT after its native
  acknowledgment-delay deduction.
- `AliasedRoundTripSeconds`: the smoothed RTT before that deduction.
- `BitsPerSecondIn` and `BitsPerSecondOut`: Bolt's last published incoming and
  outgoing bit counts for its frame-based sampling interval.
- `ObservedAt`: local read completion time, not the native sample time.

RTT estimates initially start at 0.1 seconds and do not expose a first-measurement
flag. The two estimates come from the underlying transport; this game's Bolt
`PingAliased` getter returns the network estimate instead of the distinct
aliased estimate. These are round-trip estimates, not one-way latency, client
frame time or an independently trusted measurement.

Bolt publishes its traffic accumulators at frame boundaries determined by
`framesPerSecond`, without normalizing them against actual wall-clock elapsed
time. The API preserves its property names and raw values. A stalled simulation
can stretch that interval, and zero may mean no published traffic sample yet.
These are not cumulative totals, total wire bandwidth, packet-loss percentages
or proof that outgoing data reached the client. The read does not reset counters.

Status distinguishes unavailable tracking, the wrong thread, a non-server
context, an unavailable session or transport, and a failed read. A successful
read copies raw values without clamping and retains no native wrappers. Fields
may update independently, so the snapshot is not atomic. It sends no packets,
adds no polling loop and makes no moderation decisions. Hosting tools can use
these observations as context, but live multiplayer validation remains open.

### Remote Transport Endpoints

`ServerSessions.TryReadEndpoint(session, out snapshot, out status)`
reads the actual remote transport endpoint without requiring player registration,
a spawned entity or a client extension. It requires the game thread, a running
server and working session tracking. Existing connection telemetry and game
identity reads are unchanged.

`EndpointSnapshot` contains the exact `Session`, `Kind`, opaque
`RawEndpoint`, nullable `SteamId`, nullable `IPv4Address` and `Port`, and the local
read-completion time `ObservedAt`. Native Steam and IPv4 fields occupy the same
eight bytes. The SDK uses the exact concrete native socket class to interpret
them; it never guesses from a numeric range or the dedicated-server flag.

- `SteamP2P`: `SteamId` contains the raw Steam peer value. Address and port are null.
- `IPv4Udp`: `IPv4Address` is dotted decimal and `Port` is the remote port.
  `SteamId` is null, even though the overlapping native field contains bits.
- `Unknown`: the socket is not one of those two concrete implementations,
  including a custom derived type. Only `RawEndpoint` is exposed; all interpreted
  values are null. This is a successful opaque observation, not a failed read.

Zero values are preserved, not converted into a valid account or endpoint.
`RawEndpoint` is a transport value, not a durable identity key. IP addresses can
be shared or change, and a Steam endpoint observation does not independently
verify an authentication ticket, game registration, license or permission.
Use `TryReadIdentity` separately for the game's identity/role view; those reads
are not atomic with this one. No role, ban or trust decision is inferred from
agreement or disagreement between them.

Before publishing, the API rechecks the exact connection, transport, socket,
platform socket and unchanged endpoint bits. It uses
`ConnectionReadStatus`; missing or changed transport components
return `TransportUnavailable` with no snapshot. Exceptions return `Failed`.
These checks do not lock the transport worker or pin the endpoint after return.

This read copies only managed values, sends no packets, performs no DNS lookup
and creates no socket, poller or authentication request. It does not expose an
IP address behind a Steam relay or inspect the separate blob transport.
Endpoint values are not written to loader logs; consumers control their own
display and retention. Hosted Steam, dedicated UDP, reconnect and custom-socket
behavior remain unverified in a live game.

### Steam Authentication Observations

`ServerAuthentication.Observed` exposes the game's existing Steam
game-server validation callback after its normal handler returns. This requires
the server loader, not a client extension. It does not request a ticket, start
another authentication session or change the game's rejection behavior.
`HooksInstalled` reports successful hook registration, not authentication or
callback delivery.

Each immutable observation contains the reported `SteamId`, `OwnerSteamId`,
`RawResponse`, nullable known `Response`, and local `ObservedAt` timestamp. Zero
IDs and unknown response values are preserved. The owner can differ from the
ticket subject; it does not confer server permissions. No ticket bytes or ticket
handles are exposed. This observer's diagnostics omit account IDs and exception
messages; the game's existing logging is unchanged.

Callbacks run synchronously on the native publisher's thread, which is not
assumed to be the game thread. Subscribers can retain the copied values, but
must honor thread requirements before calling other SDK APIs. A throwing
subscriber does not prevent later subscribers from receiving the observation.
The event cannot cancel validation. A failure observation may arrive after the
game has requested a disconnect, without proving that disconnect completed.

A prefix in the same patch call copies the game's own SteamID-to-connection
lookup just before the native handler runs. `VanillaTargetConnectionId` is that
connection, or null when none is registered; `VanillaTargetChecked` is false
when the lookup could not be taken. `VanillaWillDisconnect` is true for a
failed response with a target, which is the branch where vanilla disconnects;
a failed response without one is only logged. The lookup is the game's table,
not an authenticated binding. `TryGetVanillaTarget(observation, out session,
out status)` resolves it to a `ServerSession`, only inside the callback and only
when the callback runs on the game thread; otherwise it reports `WrongThread`.

This is an event stream, not a current-authentication query. Subscribing late
misses earlier deliveries. `TryReadLatest(steamId, out latest)` keeps only the
latest observation for each of up to 1,024 accounts since load, and
`ObservationCount` counts them; an entry can predate the current session and
must not be carried over to a reconnect. Many joins never produce a callback:
listen-host joins, and the token, ban, password, empty-ticket and
`BeginAuthSession` rejects that vanilla disconnects synchronously. No observation
is emitted when the original handler is skipped by another patch or throws.
It observes `SonsServerSteam.ValidateAuthTicketResponse`, not the separate Steam
user API or sample callbacks. Other patches and direct calls are not an
attestation boundary.

Results can change during a session. The payload identifies an account, not a
particular ticket or Bolt connection lifetime, so a previous `Ok` must not be
reused as proof for a reconnect. Missing observations imply neither approval nor
denial. These results are not roles, permanent ban decisions or proof that a
client is cheat-free. Host/dedicated-server delivery, rejection, later revocation
and reconnect behavior remain unverified in a live game.

### Incoming Connection Requests

`ServerAdmission` controls the live server transport's native
incoming-request gate. It requires no client component and works independently
of the optional session-tracking hooks. Calls require the game thread, a running
Bolt server and its live host socket/configuration.

`TryRead(out snapshot)` returns `Success` with an immutable observation of
`IncomingRequestsEnabled`, `AutomaticAcceptanceEnabled`, the raw `ConnectionLimit`
and `ObservedAt`. A true incoming-request flag does not mean a player can join:
capacity, password, Steam authentication and other native checks still apply.
The snapshot is not an atomic transport-state or connection-count read.

`SetIncomingRequestsEnabled(false)` closes the native request gate;
`SetIncomingRequestsEnabled(true)` reopens it. This changes only the current
socket's incoming-request flag, not automatic acceptance, the connection limit,
server discovery, passwords, bans or roles. The caller owns authorization and
when to restore the setting. There is no default policy, command, timer, lease,
automatic retry or persistent configuration change. A new server transport uses
its normal startup settings; the SDK does not reapply a previous value.

- `Success`: the write was read back on the same current socket/configuration.
- `AlreadySet`: the requested value was already observed; no write was attempted.
- `Unavailable`, `WrongThread`, `NotServer`, `TransportUnavailable`: preflight
  could not establish the required context; no write was attempted.
- `Failed`: an exception occurred before a write attempt.
- `ApplyUncertain`: a write was attempted, but current-transport readback failed
  or disagreed. The flag may have changed; no rollback is attempted.

The inspected native receive path checks this flag before pending/manual or
automatic acceptance of a new connection request. A disabled gate uses the
native refusal response, not a custom message. Token parsing occurs first, so
this is not a packet filter or denial-of-service defense. Existing connections
are not disconnected. Requests already admitted or pending, in-flight transport
work and explicit acceptance calls are not canceled. It is not a barrier proving
that no further player can enter. Local readback is neither a transport-worker
acknowledgment nor proof of a client receiving or displaying the refusal. Live
hosted/dedicated-server and vanilla-client behavior remains unverified.

### Server Identity Reads

`ServerSessions.TryReadIdentity(session, out snapshot, out status)`
reads identity and stored role data for an exact current remote session. It
requires working session tracking, the game thread and a running server, but
no client extension or local host player. Failure returns false with a null
snapshot and explicit status; a zero game identity reports `IdentityUnavailable`.

`GameSteamId` is the nonzero identifier the game uses for that connection.
`IdentitySource` distinguishes `DedicatedRegistration` from `TransportEndpoint`.
Dedicated reads use the game's registration table; listen-server reads use its
Steam-ID resolver, which reads the transport endpoint's overlapping identity
field. Neither verifies authentication independently or proves that an endpoint
is using Steam transport. `RegisteredSteamId` is a separate fresh table read;
it can be absent or differ from a listen-server identity or cached session data.

`RoleStatus` distinguishes an unavailable role manager/backing data, a lookup
with no record, and a present record. `StoredRole` is null unless the record is
present. A stored `Unknown` remains `Unknown`; an unrecognized numeric value is
preserved. No missing value becomes Trusted, and reads do not create records,
normalize roles or invoke role-change callbacks. A mismatched/unreadable record
fails the read instead of associating another player's role.

`GameRole` names the known native values without requiring SDK types.
They are not flags or permission ranks: Banned has a larger number than Guest.
A stored Trusted value does not reveal whether it was explicitly assigned or
created as the game's default. This API grants no permission and is not a
replacement for a toolkit's authorization policy.

Snapshots retain managed copies and the session handle only. Identity/role fields
stay fixed, while that handle can later close or refresh its metadata. Reads are
not atomic across the game tables; `ObservedAt` is completion time, not a login
or role-change timestamp. Live listen-server/dedicated-server identity and role
behavior remains unverified.

### Server Role Changes

`ServerSessions.SetStoredRole(expected, role)` changes the game's
role record for the live remote session in an identity snapshot. It needs the
game thread, a running server and working session tracking, but no client
extension. The caller must authorize the operation before calling it.

The API reads identity and role again, comparing the exact session, game ID,
identity source, fresh registration and raw role presence/value with `expected`.
Changed input returns `PreconditionChanged` without calling the setter. A null
snapshot or one whose role data was unavailable returns `InvalidSnapshot`.
Guest, Banned, Trusted, Admin and Owner are accepted explicitly; Unknown and
other numeric values return `InvalidRole`. Unknown is not a way to clear a role:
the game's setter would silently convert it to Trusted.

The result contains `RequestedRole`, a fresh `Before` snapshot when available,
and an `After` snapshot only if a setter was attempted and its subsequent read
succeeded. `Unchanged` means the expected snapshot matched and already held
the requested role; no setter ran. `Updated` means the setter returned and the
same live identity's record matched on readback. A previously missing record
can be created. `MutationAttempted` is true for `Updated` and `UpdateUncertain`;
other outcomes leave the record untouched by this API.

`UpdateUncertain` means the setter was attempted but its final effect could not
be confirmed, including an exception, a departed session or a mismatched
readback. Inspect any returned `After` snapshot before deciding what to do.
No retry or rollback is performed. These are sequential observations, not an
atomic transaction or proof that no other mod changed the record between reads.

This is a stored-role operation, not a complete ban, ownership transfer or
permission policy. Banned does not disconnect the current client; use the
separate session disconnect operation when that is intended. Owner does not
edit the owners whitelist. The game's regular server update copies stored
roles into its replicated role array; this API neither sends an extra update
nor confirms client receipt. It does not force a save or promise durability.
The changed record is still game-owned and may be included in a later normal
save, so this is not a temporary-role API. Serialization and loading reapply
the owners whitelist: listed players become Owner even if currently Banned;
unlisted Owners become Admin. A save can therefore change the role itself,
not just persist the value previously read back. Toolkit policies must account
for that native behavior. Live role effects, replication and save/reload
behavior remain unverified.

### Stored Roles Without A Live Session

`ServerRoles.TryRead(gameSteamId, out snapshot, out status)` reads
a record by nonzero game Steam ID, including players who are not connected.
The immutable snapshot contains `GameSteamId`, raw nullable `StoredRole`,
`HasRecord` and `ObservedAt`. Null means no record; a present Unknown or
unrecognized numeric role is preserved without applying a default.

`SetStoredRole(gameSteamId, expectedRole, role)` changes that record only when
its current raw value matches `expectedRole`. Pass null to require a missing
record. Guest, Banned, Trusted, Admin and Owner are accepted as the new role;
Unknown is not removal. The result reports `Before`, `After`, `RequestedRole`
and `MutationAttempted`. A mismatch returns `PreconditionChanged`; a matching
record already at the requested value returns `Unchanged` without a write.

`RemoveStoredRole(gameSteamId, expectedRole)` removes the entire native player
role record, including its cached name, only when its raw role matches.
Null expects an absent record and returns `AlreadyMissing` without a write;
a presence/value mismatch returns `PreconditionChanged`. Unknown and other raw
stored values can be matched for removal. The result contains `Before`,
`After` and `MutationAttempted`. `Removed` means the native call returned and
the same store no longer contained the ID. After an attempted removal, a
remaining record, changed store, exception or unavailable readback returns
`RemovalUncertain`. Only one removal is attempted, even if duplicate records
exist.

All operations require the initialized game thread, a running network server,
configured multiplayer save paths, no active load, and an active role manager
with available data. They do not require session hooks or a client extension.
`Updated` confirms that the native setter returned and the same manager/data
store held the requested value afterward. A setter exception, changed store
or failed/mismatched readback returns `UpdateUncertain`, without retry or
rollback. These checks are sequential, not an atomic transaction.

The caller chooses the current world and target ID and owns authorization.
This API does not verify a Steam account, bind the request to an earlier world
or session, disconnect a player, force replication or save, or modify the
owners whitelist. A stored Banned value is not a complete ban policy.
Removing a record does not set an explicit replacement role or guarantee an
effective permission change. It is not permanent erasure: later game activity
or owner-whitelist application can recreate the record.
The native persistence and whitelist behavior described above still applies.
For identity-bound changes to a connected player, use
`ServerSessions.SetStoredRole` instead.

### Server World Reads

`ServerWorld.TryReadPlayer(session, out snapshot, out status)` reads
the game's registered player entity for an exact live remote session.
`TryReadEntity(networkId, out snapshot, out status)` looks up a current-world
entity by its complete network ID, including a target ID from a policy request.
Both run only on the initialized game thread while hosting a network server.
Neither requires a client extension or an in-world local host player.

Failure returns false with a null snapshot and an explicit status. A connection
without an attached player entity reports `EntityUnavailable`, not a zero-position
player. Zero or missing entity IDs also report `EntityUnavailable`. Player reads
require working session tracking; entity reads can still return fields when
tracking is unavailable, with null session associations.

Snapshots copy the network ID, Bolt prefab ID, world position and rotation,
local-owner flag, and observed source/controller connection IDs. Positions and
rotations use `System.Numerics` values. Optional source/controller session handles
provide lifetime association without retaining native entities, transforms or
connections. The copied fields stay fixed, but those handles' metadata can change.
`ObservedAt` is read completion time, not a simulation or replication timestamp.

The native player lookup selects from the game's player registry by source
connection reference; the SDK additionally requires an attached, live entity
whose source still matches. This is not a name or claimed Steam-ID lookup.
General entity lookup grants no permission and does not identify an entity as
a player. Network IDs are not persistent save or cross-session identities.

These are server-visible observations, not authoritative movement verdicts.
`IsServerOwned` is Bolt's local-owner flag on the server; it does not prove that
every field originated there. A null source/controller reference does not mean
server control. Transforms are copied without normalization or validation and
may reflect client replication, including invalid values. Mods must make their
own validity, proximity, ownership and permission decisions. Reads are not an
atomic world transaction or an inventory query, and mutate no gameplay state.
Live player/target association and pose reads remain unverified.

### Native Range Validation

Modstoke SDK exposes the game's existing validators through
`ServerWorld.ValidatePositionRange(session, expectedSteamId,
worldPosition, maxDistance)` and `ValidateStructureHitRange(session,
expectedSteamId, targetNetworkId, maxDistance)`. Both require an exact live
remote session on the initialized server game thread. Vanilla clients need
no extension. The expected nonzero Steam ID is the game's identity, not a
separate authentication claim.

Position validation takes a `System.Numerics.Vector3` in world space and an
integer distance limit. Structure-hit validation takes the complete current-world
target network ID and a finite floating-point limit. Limits must be nonnegative;
there is no default. The methods call the corresponding native validators,
including the structure-hit validator's player-state check. They do not substitute
a managed distance calculation or resolve parent-relative placement coordinates.

`NativeRangeResult.Status` is `Accepted` or `Rejected` only when the
native call completed with a stable binding. Every other status means unavailable
evidence, not rejection. Before and after the call, the SDK checks the current
session, connected transport, expected game identity, attached registered player,
source connection, and the native `connection.UserData` player. Structure-hit
validation also checks the returned player and the attached target. Positions
must be finite. Missing, changed or mismatched objects and exceptions cannot
produce an attributed rejection. No local-player fallback is used.

Results retain no native objects. `PlayerNetworkId` is present only for a
completed verdict; `TargetNetworkId` is the requested ID, not proof of a successful
lookup. `ObservedAt` is local completion time. A result is not an atomic snapshot
or a guarantee that its binding remains valid after return. Native methods can
log diagnostic messages; a failed post-call check does not mean they were never
called.

These APIs do not reject events, authorize actions, refund inventory, disconnect
clients or establish absence of cheating. Replicated transforms can still be
client-influenced. Keep enforcement separate and distinguish a native rejection
from all unavailable statuses. Live client/server acceptance remains pending.

### Replicated Player State

`ServerWorld.TryReadPlayerState(session, out snapshot, out status)`
copies selected fields from the current player entity's replicated state. It
uses the existing server/game-thread/session checks and works without an
optional client extension or local host player. A connected client without an
attached player reports `EntityUnavailable`, not an empty inventory.

`PlayerStateSnapshot` contains the exact `Session`, current-world
`NetworkId`, read-only `HeldItemIds`, separate `RightHandHeldItemId`,
`HeldOnlyItemId` and `HeldOnlyItemCount`, plus `RawDyingState` and
`ObservedAt`. `LifeState` recognizes Alive (0), Dying (1) and Dead (2); unknown
values remain available through the raw field and return null from `LifeState`.
The API does not expose or infer a health amount.

Held-item entries retain their native order and zeros. The game's publisher
adds IDs to vacant entries and suppresses duplicates; indices are not fixed
left/right-hand slots, and the list cannot identify individual item instances
or quantities. Held-only items have a separate controller and replicated
ID/count. Its native publisher caps the count at the number of active presentation
renderers; this is not a complete inventory count or a constraint enforced by
the read API. The right-hand field is copied independently.
No field is used as a fallback for another, and no ID or count is normalized.

Before returning, the read rechecks the same connection, attached player,
network ID, source, state and held-item array. A null or replaced state or array
reports `PlayerStateUnavailable`, appended to the existing world-read status
enum; exceptions during a read report `Failed`.
A failure returns false and a null snapshot. The result retains no native
objects; its copied collection and scalar values stay fixed.

These are server-visible observations of client-replicated fields, not proof of
item ownership, a valid attack, a player's actual health or absence of cheating.
Values can be stale, inconsistent or modified by another mod. Copying them is
not an atomic replication snapshot, and `ObservedAt` is only local read time.
The API sends nothing, adds no polling, changes no state and makes no enforcement
decision. Live vanilla-client, equipment-change and respawn acceptance remains
open.

### Player Pose Reads

`ServerWorld.TryReadPlayerPose(binding, out snapshot, out status)` copies the
host's transform for an exact `PlayerHandle`, beside `BoltNetwork.frame`,
`BoltNetwork.serverFrame` and the raw Bolt entity `LastFrameReceived`,
`IsFrozen` and `UpdateRate`. For a remote player it also copies the source
connection's raw `RemoteFrame`, which reads Bolt's `_remoteFrameEstimated`: a
host-stepped estimate derived from client packet frames, not the client-written
counter. `ConnectionSnapshot.RemoteFrameLatest` holds the client-written value.
It is a pull read with no hooks, rechecked within one Unity frame.

The request bases `TransportRequest`, `VehicleRequest`, `PickupRequest`,
`InteractionRequest`, `WorldPlacementRequest` and `ConstructionRequest` stamp
`ReceivedUnityFrame` and `ReceivedBoltFrame` next to `ReceivedAt`. A handler
can compare `snapshot.UnityFrame` with `ReceivedUnityFrame` to show that its
read ran in the dispatch frame. Bolt fixed steps and Unity frames advance at
different rates and are never converted.

For a remote player the pose is Bolt's interpolated or extrapolated proxy, not
the client's latest sample or its position when it sent the event. Events and
state updates are applied in different phases, so the pose can lag or lead an
event. Frozen proxies keep a stale pose. The received-frame numbering is not
established, so no frame age is derived, and no pose decoded from a specific
packet is available. For the listen host's own player `IsLocallyOwned` is true
and the received-frame fields mean nothing.

### Local Locator Observations

`WorldLocatorReads.TryRead(locatorId, out snapshot, out status)`
reads local observations for a locator GUID, including the ID from
`ServerWorldLocators.Receiving`. It works on the initialized game
thread with a live active locator manager, on a server, client or in single-player.
It does not require policy hooks, session tracking or a network connection.

The read uses the same 12-byte key as the native receiver. Either a raw request
GUID or its normalized `LocatorId` is accepted; the snapshot returns the normalized
key with the final four bytes zeroed. This is not a Bolt entity ID.

A successful snapshot keeps three sources separate:

- `RegisteredState` is the raw current state of a matching registered holder's
  locator. `RegisteredPosition` is that locator's stored position, not the live
  transform of a falling, spawned or moving object. Neither implies visibility
  or an active collider.
- `RecordedState` is the entry in the manager's local recorded-state collection.
  It is not evidence that the state was written to disk.
- `UnappliedState` is the separate local unapplied-state entry, not a prediction
  of which state will eventually win.

Each state is nullable. A missing entry is not replaced with `Default`, and the
three values are not merged or given an invented precedence. The corresponding
`KnownRegisteredState`, `KnownRecordedState` and `KnownUnappliedState` are also
null for unknown integers; raw values remain available. `HasAnyObservation=false`
means these sources contained no matching entry, not that the world object does
not exist. This can still be a successful read.

Unavailable manager, registry or state collections return false with a null
snapshot and an explicit status. A registered but stale, missing or mismatched
holder/locator reports `LocatorUnavailable`, not a fabricated absence.
Exceptions report `Failed`; reads from other threads report `WrongThread`.

Snapshots contain only managed GUID, numeric, position and timestamp values.
`ObservedAt` is local read-completion time. The sources are not an atomic world
transaction, persistence receipt, permission verdict or proof of agreement with
other peers. No spawning, state application, dictionary changes, network send,
automatic polling or disk access occurs. Live world/scene transitions, registered
and unloaded cases, absent IDs and client/server agreement remain unverified.

### Server Water Columns

`ServerWorld.TryReadWaterColumn(position, searchRadius, waterLayerMask,
blockerLayerMask, out snapshot, out status)` reads native surface geometry among
loaded colliders near an explicit position. It requires the server game thread
and a current world; it does not look up a local player or ocean renderer.
The caller must supply layers appropriate to the world's water and depth blockers.
Layer membership alone does not establish a valid water body.

The collider search includes triggers, accepts a positive radius up to four
metres, and rejects a full 32-entry buffer. The native column ray then uses the
selected colliders' vertical bounds and the position's X/Z. Its Y affects the
nearby-collider search, but does not restrict the surface ray. A depth-blocker
hit adjusts the depth below that surface; it does not exclude a cave or reject
the water surface. Native raycasts require global trigger queries to be enabled;
the SDK returns `UnsupportedProfile` instead of changing that setting.

Successful snapshots copy surface height, depth, normal, maximum collider-bounds
height, search parameters and a transient collider instance ID. They retain no
native objects. `IsCurrentWaterColumn` checks only the world and frame lifetime.
Each call reserves eight units of the shared 256-per-frame collision budget for
the overlap, up to six native surface rays and one depth ray. Temporary collider
references are cleared after the read.

No nearby collider or usable surface returns `SurfaceUnavailable`, not a dry
position. A full buffer, changed context, invalid geometry or failed read also
returns no snapshot. These observations do not establish head submersion,
trusted player movement, ocean sampling, cave exclusion, oxygen reserves or a
damage grant. Dedicated-server geometry availability and multiplayer behavior
remain unverified.

`TryReadWaterVolume(position, waterLayerMask, blockerLayerMask, requiredTag,
out snapshot, out status)` narrows that read to supported non-ocean volumes
containing the supplied point. It searches a 0.1-metre neighbourhood, excludes
colliders with a native `IsOcean` marker on the collider itself, and optionally
requires one exact tag. Pass null for no tag filter; an empty tag is invalid.
Layers and tags remain caller policy, matching the game's configurable admission
model rather than assuming a universal water component or fixed layer number.

Containment accepts boxes, spheres, capsules and convex meshes with a live mesh.
It uses exact equality with Unity's closest-point result, which includes boundary
contact; an unsupported shape returns `UnsupportedProfile`. This follows
[Unity's documented closest-point behavior](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Physics.ClosestPoint.html).
Only containing colliders contribute to the surface ray. The selected collider
is checked again after the native read. Each closest-point operation consumes
another unit of the shared collision budget, including that final check.

Snapshots distinguish `NearbyColumn` from `ContainingNonOceanVolume` through
`Selection`, and copy the optional `RequiredTag`. A volume result establishes
the point's relation to the selected physics shape. It does not establish that
the point is a player's head, that its movement is legitimate, that it lies
strictly below the surface, or that drowning is allowed. Ocean water remains
outside this method, and an unavailable result still does not mean dry. No
sensor tracking, cave cache, player state or air reserve is changed.

`TryReadPlayerWaterline(binding, out snapshot, out status)` reads the native
root and vertical offset used by the player's waterline calculation. It follows
the remote setup's exact Animator reference to its unique PlayerAnimatorControl,
requires an initialized root belonging to the captured player attachment, and
copies `rootTr.position + worldUp * _underWaterOffset`. It does not substitute
a head bone, collider centre or constructor default. Missing, ambiguous or
replaced references return no observation.

The snapshot exposes the exact binding, frame, root position, configured offset,
resulting point and controller-enabled flag. A disabled controller can supply
geometry; it does not prove that native swimming logic is running.
`IsCurrentPlayerWaterline(snapshot, out status)` rechecks the reference chain,
attachment, copied values and world/frame on the server game thread. Reads and
freshness checks each reserve one unit of the shared collision budget; setup
lookup is bounded to 32 candidates. Gameplay life remains a separate caller check.

`TryReadPlayerWaterVolume(binding, waterLayerMask, blockerLayerMask, requiredTag,
out snapshot, out status)` combines that point with a containing non-ocean volume
and rechecks the player after the water query. Its `IsBelowSurface` property uses
a strict Y comparison; equality is not below. Requery after native mutations or
for later frames. An unavailable result is never treated as dry.

These reads qualify geometry only. The game's local swimming path also uses
sensor membership, rope state, cutscenes and swimming thresholds; the SDK does
not invoke that path or claim to reproduce those decisions. Ocean coverage,
dedicated prefab availability, movement authority, equipment ownership and
oxygen or damage policy remain separate concerns. No life, air reserve or
native underwater flag is changed.

`TryReadOceanHeight(position, minimumSpatialLength, out snapshot, out status)`
samples the current ocean's synchronous CPU Gerstner collision provider. It accepts
only that exact provider type, with a live renderer/root, an existing time source
and coherent finite wave arrays containing at most 1,024 components. GPU,
asynchronous, flat-fallback and unknown providers return `UnsupportedProfile`;
the SDK does not change the game's selected provider. A failed sample never becomes
a sea-level or zero-height fallback.

The position must be finite and the minimum spatial length finite and nonnegative.
Pass the intended sampling width explicitly; the native sensor uses its configured
object width. The result copies absolute surface height, root sea level, position,
width, frame and native sampling time. Sampling reserves five units of the shared
collision budget for the bounded native wave passes. It can populate the native
sea-level cache but does not alter sensor tracking, player state or resources.

`IsCurrentOceanHeight(snapshot, out status)` uses one budget unit to recheck the
world/frame, renderer, selected provider, root height, wave-array identities and
time-source value. These checks do not freeze wave contents, establish their update
age or prove a convergence/error bound. The installed `Crest.dll` game wrapper is
an explicit SDK build reference; no original loader or additional bundled runtime
is introduced.

`TryReadPlayerOceanHeight(binding, minimumSpatialLength, out snapshot, out status)`
pairs this surface with the remote player's qualified waterline and rechecks both
observations. `IsBelowSurface` is only their strict Y comparison. Ocean collider
membership and cave eligibility still need qualification, and dedicated-server
provider availability remains unverified. Neither success nor failure establishes
clear air, submersion, movement authority or permission to restore oxygen or apply
drowning damage.

`TryReadWaterCoverage(position, waterLayerMask, requiredTag, out snapshot, out status)`
classifies the complete result of a 0.1-metre overlap in the loaded physics scene.
It counts containing non-ocean and direct `IsOcean` volumes separately, along with
nearby colliders excluded by the tag or outside the point. An empty successful
query returns a snapshot with zero counts. A full 32-entry buffer, unsupported
containment shape or failed query returns no snapshot. This distinction does not
establish that the configured layers cover all water or that the world is fully loaded.

Coverage uses one shared budget unit for the overlap and one for each accepted-tag
collider's containment check. `IsCurrentWaterCoverage` repeats those operations in
the same frame, comparing candidate identities, filters, bounds and point relations
independently of physics result order. It does not compare complete mesh contents or
resample a surface. These APIs share the existing once-per-frame transform synchronization.

`TryReadTerrainHeight(position, out snapshot, out status)` reads absolute height from
exactly one containing active terrain. It inspects the entire active-terrain array,
up to 64 entries, and supports finite, positive terrain sizes with translation-only
transforms. Missing coverage, overlapping terrains and unsupported profiles fail;
the SDK does not substitute the active terrain when the point is outside its bounds.
Each read reserves three shared budget units for two bounded candidate scans and
one height sample. `IsCurrentTerrainHeight` performs another complete read and
compares the height and candidate configuration. Heightmap holes, cave meshes and
native cave overrides remain unclassified. The existing `UnityEngine.TerrainModule.dll`
wrapper is an explicit build reference, with no additional packaged dependency.

`TryReadPlayerWaterEnvironment` combines the qualified player waterline, loaded-water
coverage and a surface under explicit layer, tag and ocean rules. `Include` prefers
an ocean surface when a containing ocean volume is present. `Ignore` uses only
non-ocean volumes. `IgnoreBelowTerrain` excludes ocean when a qualified terrain
height exceeds the waterline Y plus one metre, matching the native height heuristic
at that supplied point. It requires a successful terrain read; it does not reproduce
cave membership, sensor caches or override delegates.

The result reports `BelowSurface`, `AtOrAboveSurface` or `NoApplicableLoadedVolume`.
The last outcome includes volumes excluded by the explicit ocean rule. Failure is
never converted into one of those outcomes. Coverage, optional terrain/ocean and
the player binding are rechecked before returning. All component reads consume
the shared budget, so callers must allow deferral rather than assume every player
can be sampled in every frame. Surface contents are not frozen. The observation
does not itself authorize oxygen restoration or damage: world coverage, masks,
movement authority and server gameplay policy still need qualification.

### Local Clock And Weather

`EnvironmentReads.TryReadClock(out snapshot, out status)` and
`TryReadWeather(out snapshot, out status)` read the current local environment.
They work on the initialized game thread with an active native clock or weather
system, including on clients and in single-player. They do not require a network
publisher, an attached player, session tracking or a server role.

Clock snapshots contain `GameTime` and `EffectiveTime` as managed `TimeSpan`
values. `GameTime` copies the underlying clock; `EffectiveTime` copies the native
override when `IsOverrideActive`, otherwise it is the same value as `GameTime`.
Day counts and ticks are preserved without a display-day adjustment or rounding.
`IsNetworkManaged` reports the holder's flag, not network health or synchronization.

Weather snapshots contain the current precipitation strength, cloud phase, snow
flag and rain-block flag. They do not report the last SDK request. Known values
map to `Precipitation` and `CloudState`; unknown values are
preserved in `RawPrecipitation` and `RawCloudState`, with null typed properties.
Cloud phases are `Idle`, `GrowingClouds`, `ReducingClouds` and `Raining`. The last
is the game's cloud-state label, not proof of visible rain. Likewise, `IsSnowing`
is a native flag, not a particle-rendering check, and `IsRainBlocked` is not a
comprehensive wetness or effects override check.

Successful snapshots are immutable managed copies with no retained native
objects. `ObservedAt` is read completion time, not a game tick or replication
timestamp. Reads are independent local observations, not an atomic world
transaction, readiness guarantee, response receipt or proof of client agreement.
An override, scene transition or another mod can make the values unsuitable for
a caller's decision even when the read succeeds. Failure returns false, a null
snapshot and an explicit `EnvironmentReadStatus`.

These reads do not change gameplay, request synchronization or send packets.
They are separate from `GameRuntime.Environment`, which describes runtime
metadata and paths. Live server/client and scene-transition reads remain unverified.

### Dedicated Server Saves

`ServerSaves.RequestConfiguredAutosave()` explicitly requests a save
through the native dedicated-server autosave controller. It requires the
initialized game thread, a running dedicated network server, a live save
manager with initialized paths and multiplayer save mode, no active load, and a
live active autosave controller with its timer. Hosted player games and clients
return `NotDedicatedServer`; there is no quicksave fallback.

The controller must already pass its native readiness check. The SDK does not
force initial-world readiness, change the save slot, select a path, rename the
save, alter configuration or invoke an immediate save. Native queueing also sets
the controller's initial-save flag, which is why readiness is checked first.
That check can refresh the controller's cached loaded-game flag.

Results describe only the request:

- `Queued`: the native queue call returned and the same live controller reported
  its queued flag. This is not proof that a save started or reached disk.
- `AlreadyQueued`: a request was already pending; the SDK made no new request.
- `NotReady`: loading, save-path/mode prerequisites or native readiness blocked
  this call. Existing native requests are not canceled.
- `Unavailable`, `WrongThread`, `SaveManagerUnavailable` and
  `AutosaveUnavailable`: the required local context was unavailable.
- `Failed`: an exception occurred before invoking the queue operation.
- `RequestUncertain`: the operation may have changed native state, but completion
  of the call or its readback could not be established. It is not retried.

Native Update consumes the queued flag, resets its timer and uses the server's
configured slot and normal save pipeline. Configuration is read when the save
runs, not pinned by this API. Requests coalesce in one native flag; there is no
request ID, completion handle, cancellation, retry, shutdown barrier or new
autosave schedule. Queued work may be lost when the controller or world ends.
The existing native save can overwrite its configured slot; its normal backup
and retention behavior is unchanged.

Existing `SaveLifecycle` hooks and mod-sidecar handling still observe
the normal save entrypoint. An `After` event, sidecar write or native completion
log is not a durable-save receipt and cannot be correlated uniquely with this
request. Native after-save callback Booleans describe player-only saving, not
success. This does not instruct remote clients to save their local player data.

No client extension is needed. The SDK adds no default command, authorization,
timer or automatic request; the calling toolkit owns permission and scheduling.
The implementation has been compiled, but no save request was invoked during
development. Actual configured-slot writes, backup behavior, failures and
lifecycle/sidecar integration remain unverified in a dedicated-server session.

### Server Time Of Day

`ServerTime.SetTimeOfDay(hour, minute = 0, second = 0)` adjusts the
server clock through the game's native time holder and registered network
publisher. Hours must be 0-23; minutes and seconds must be 0-59. The current day
is preserved, milliseconds become zero and the normal clock speed is unchanged.
This is a clock adjustment, not sleep or a simulation of elapsed hours: the call
suppresses the native elapsed-time delta while notifying time receivers.

The operation requires the initialized game thread, a running Bolt server, an
active time holder and a single registered, active, attached, locally owned
`NetworkTimeOfDay` publisher with state. It does not create a publisher, take
ownership, send console commands or directly overwrite replicated fields.
Vanilla clients already consume this native state; no extension or handshake
is required. Normal synchronization timing and client clock locks still apply.

The result is `ServerTimeStatus`. `Applied` means the local native
setter returned true, not that every callback succeeded or every client applied
the time. Native receiver errors can be handled internally, and other mods can
change the clock again. `ApplyUncertain` means the setter threw after it may
have changed the clock or some receivers. There is no automatic retry or rollback.

`Locked` respects an existing native clock owner. `Busy` covers a temporary
hold, override time, an active time jump, a network-managed clock or a nested
SDK adjustment. These states are observed, not cleared. `Rejected` means the
native setter returned false; `Failed` is a preflight exception. Other statuses
identify invalid input, the wrong thread, an inactive server or missing clock
and replication prerequisites.

Callers own authorization, timing and rate limits. No command, schedule, weather
change, season change or persistent time lock is installed automatically. Day
and time-dependent game systems still receive the clock change. This is separate
from `GameRuntime.Environment`, which describes runtime metadata and paths.
Live host/dedicated-server synchronization, late joining and clock-lock behavior
remain unverified.

### Server Precipitation

`ServerWeather.RequestPrecipitation(precipitation)` uses the native
`WeatherSystem.ForceRain` path. `Precipitation` offers `None`, `Light`,
`Medium` and `Heavy`. Strength is not a choice between rain and snow: the game
selects the appropriate effects from its own conditions.

This is a transition request, not an instant weather lock. For precipitation,
the native path selects a cloud profile and storm target; normal weather updates
start precipitation when the clouds are ready. `None` follows the native stop
path, including cloud clearing, effect cleanup and possible after-storm effects.
It does not promise permanently clear skies. Automatic checks, timers, overrides
and other mods can delay, replace or end the requested weather.

The API requires the initialized game thread, a running server, an active weather
system and its cloud manager, loaded rain types, and an active seasons manager
when starting precipitation. It also requires an unambiguous active native
publisher in the registered wind receivers: `NetworkWeather` or `CoopWeatherProxy`
must have its native server flag, a live attached locally owned entity and weather
state. Multiple distinct publishing entities are refused. No publisher is created,
ownership transferred or replicated field overwritten by the SDK.

Both inspected native publishers copy rain type and cloud state into
`IWeatherState`; their client rain callbacks call the native start/stop handlers.
This uses a protocol vanilla clients already understand, without an extension or
handshake. It does not prove equal visual effects or synchronized transition
completion across clients, and it cannot make a modified client cooperate.

`ServerWeatherStatus.Requested` means the native call returned, not
that rain started or clients received an update. `RequestUncertain` means the
call threw after it may have changed state or notified receivers. Neither result
triggers a retry or rollback. Repeating a request can restart cloud transitions
and effects. `Blocked` preserves the native rain-block flag for precipitation
requests; stopping remains allowed. `Busy` refuses a nested SDK request.
Other statuses report invalid input, thread/server/readiness failures or a
preflight exception (`Failed`).

Callers own permission checks, timing and rate limits. The API installs no weather
schedule, season change, permanent override or automatic admin action. Live host
and dedicated-server operation, vanilla transitions, snow selection, stopping
and late-join behavior remain unverified.

### Player Spawn Assignments

`ServerPlayerSpawns.QueueSpawnAssignment(session, position, rotation)`
sends the native `SetPlayerSpawnPoint` message to an exact remote session using
reliable delivery. It needs installed session and spawn-authority hooks, the
game thread, a running server and an attached player whose source still matches
that session. Position and rotation use `System.Numerics`. Nonfinite coordinates,
nonfinite rotations and zero quaternions are rejected; other rotations are
normalized. The current replicated `IPlayerState.PlayerRace` must be a defined
native race and is copied into the assignment, not chosen by this API.

This is more than a teleport. With a local player present, vanilla handling
changes position and look direction and reapplies that race. It also caches
the position, rotation and race in the native player-actor manager for spawn
handling. It does not establish a persistent respawn setting. The look path
uses pitch/yaw rather than preserving roll. The receiver does not use the
debug teleport helper's cave-transition preparation.

The caller owns permission checks, destination selection and gameplay timing.
Finite coordinates do not establish safe terrain, loaded areas or valid cave,
vehicle, cutscene and death-state transitions. The replicated race is an
observation and can be stale; it is not a verified avatar identity. No local-host
fallback, server-transform overwrite, automatic retry or client extension is
used. `Queued` only means Send returned; `SendUncertain` means Send threw after
possibly queueing. Neither proves that a client moved or stayed at the destination.

The loader also guards this exact event before native dispatch and relay.
Remote-origin spawn assignments are rejected on a modded server; optional
clients accept received assignments only from their current server connection.
Locally raised messages retain native handling. Denied events release their
storage once; allowed events keep native ownership. Off-thread dispatch and
authority-read failures reject with bounded diagnostics. No subscription is
needed, and ordinary server-authored join assignments remain on the native path.

`State.Status` and `Detail`, also at `openacai.sons.serverPlayerSpawns`, report
hook registration only. This is an authorship check, not payload validation or
cryptographic authentication. An optional client's immediate-server check
cannot identify the original author of a message relayed by an unprotected
server. Native decoding occurs before the guard. Live joining, spawn assignment,
relay rejection and ordinary gameplay after movement remain unverified.

### Player Revival

`ServerPlayerRevives.QueueRevive(session)` requests native multiplayer
revival for an exact remote session, including a vanilla client. It requires the
game thread, a running server, installed session/revive hooks, an attached player
whose source matches, and replicated `IPlayerState.Dying` equal to the native
`Dying` value. It rechecks the session, player and observed state before sending
a reliable targeted `PlayerHealed` event. Other observed states return `NotDying`.
These observations may be stale or client-controlled; they are not permission
checks or confirmation of the client's current condition.

The native receiver compares `HealTarget` with its local player and calls
`HealedMp(false)`. That method only acts while the local state is `Dying`:
it changes the state to alive, applies native recovery health and runs the
normal recovery animation/reset path. This is not arbitrary healing, instant
recovery or resurrection of a fully dead player. The receiver ignores
`HealingItemId`; the SDK sends zero and does not consume or grant an item.

The caller owns authorization. `Queued` means Send returned, not policy approval,
delivery or completed revival. `SendUncertain` means it threw after possibly
queueing; no automatic retry is made. The SDK does not call local-player recovery
methods on the server or overwrite replicated health/state. The client may have
recovered, died or changed entities by the time the message arrives.

`Receiving` exposes synchronous `PlayerReviveRequest` decisions
before server dispatch and relay. It includes received peer requests and local
events, including those sent by `QueueRevive`. Native player-to-player revival
uses this message, so there is no server-only sender restriction or default
proximity/permission rule. With no subscriber, native handling is unchanged.
Subscribed context/handler failures and off-thread server dispatch reject.
Sticky `Reject()` stops later handlers; the decision window closes on return.

Requests copy the source connection ID, exact source session when available,
game-registered Steam ID, raw routing targets/envelope IDs, raw payload target
network ID, item field and receive time. Routing and payload targets are distinct;
the fields do not establish valid recipients, item ownership or nearby players.
No native event, entity or connection is retained.

Rejection releases this event's storage once and stops its native routing. It
does not undo earlier client effects or send a denial response: the native revive
interaction disables its own game object after sending, and server rejection
does not restore it. Direct local healing and other recovery paths are outside
this hook. `State.Status` and `Detail`, also at
`openacai.sons.serverPlayerRevives`, report hook registration only. Live peer
revival, policy denial and SDK-requested recovery remain unverified.

### Multiplayer Sleep Policy

`ServerSleep.Receiving` exposes synchronous policy decisions before
the server routes a native `Sleep` event. It covers server-local broadcasts and
received events without requiring a client extension. It is an event gate, not
an individual player's sleep vote or a replacement sleep system.

`SleepRequest` copies the immediate source connection ID, exact
source session when available, game-registered Steam ID, raw routing targets,
routing-envelope entity/connection IDs, `Aborted` and receive time. Server-local
broadcasts have no remote source session. These fields do not establish voter
identity, consent, permission or recipient eligibility. No native event, entity
or connection is retained. The inspected player receiver ignores `Aborted`;
that field must not be treated as a working cancellation command.

With no subscriber, native handling is unchanged. With subscribers, rejection
is sticky and stops later handlers; context or handler failures and off-thread
server dispatch reject. `Reject()` returns false after synchronous dispatch or
on another thread. Client and offline dispatch are not governed by this policy.
`State.Status` and `Detail`, also at `openacai.sons.serverSleep`, describe hook
registration, not evidence of a received or blocked event.

Rejection releases the skipped event's storage once and prevents that dispatch's
routing and local delivery. It does not roll back earlier changes or prevent
the producer from continuing after Send. The native dedicated-server producer
sets its five-second cooldown after sending; the host interaction also updates
its icons and waiting state afterward. Denial does not undo those actions,
clear other players' waiting state, send feedback or cancel sleep already in
progress. Native producers may later send another event, which is evaluated
separately. Direct sleeping-manager calls, time changes, `ValidSleepTime` and
other messages remain outside this hook.

No default sleep restriction, permission rule, forced sleep or retry is added.
Live dedicated/host sleep, denial and subsequent player interaction remain
unverified.

### Server Item Grants

`ServerInventory.QueueItemGrant(session, itemId, amount = 1)` sends
fresh-item requests through the game's existing `PlayerAddItem` protocol. It
supports remote vanilla clients without a handshake or extension. It requires
the initialized game thread, a running server, working session tracking and an
attached player entity whose source matches that exact session. A disconnected
handle cannot select a new player that reuses its connection ID. The local host
player is not inferred from a missing remote session.

Item IDs must be 1 through `MaxItemId` (32767) and resolve in the current server
item database. The native PlayerAddItem setter clamps at 2047 and the storage
events at 1023; the Loader carries reserved mod ids past those setters (Bolt
sends the full integer), and 32767 is the limit of the item instance token. Amount must be 1 through `MaxGrantAmount` (1024), an SDK per-call
limit, not a protocol limit or inventory-capacity claim. The server catalog does
not prove that custom items exist on a recipient; this API installs no client
code or assets. Callers own permissions, item eligibility and rate limits.

Each item gets a fresh native instance/token with the same ID as the event and
its own reliable targeted request with `Amount = 1`. The inspected vanilla
receiver has a bulk-overflow discrepancy: after partially accepting a request,
its non-bundled drop loop can use the original amount instead of the remainder.
One-item requests avoid that specific discrepancy. This does not patch the
client or change grants made through other mods or native senders.

The managed result contains `Status`, the original `RequestedAmount`,
`QueuedCount` and `HasUncertainSend`. `Queued` means all native send calls
returned. Any other status can still have a positive `QueuedCount` if the batch
stopped partway through. `SendUncertain` means one additional send threw and may
have queued its item. Earlier sends are not rolled back; there is no automatic
retry, fallback recipient or all-or-nothing transaction. Retrying the whole
request can duplicate items.

Queueing is not proof of policy acceptance, delivery or inventory acceptance.
The [inventory routing policy](#server-inventory-routing-policy) can reject each
queued event independently before it reaches the recipient. The vanilla receiver
checks the local player entity, then may add, auto-equip or drop the item based
on inventory state and item rules. It can display the game's item/full-capacity
HUD and invoke normal inventory callbacks. It does not acknowledge the accepted
quantity or report pickup-spawn failure back to the server. Capacity and game-mode
rules still apply, and mods cannot read remote inventory contents through this
API. Live delivery, overflow, equipment and HUD behavior require multiplayer
acceptance.

These are new default-state items, not copies of a removed instance's modules,
upgrades or contents. The API does not debit storage, amend saves, confirm a
purchase, or automatically refund rejected deposits. An optional-client notice
may accompany a grant, but its send result is independent and is not a receipt.

### Server Item Removal

`ServerInventory.QueueItemRemoval(session, itemId)` queues one
reliable, targeted `ItemRemoveFromPlayer` request. The existing vanilla receiver
calls its local inventory's `RemoveItem` with an amount of one and no item
instance. No client extension or handshake is required by this protocol.

The API requires the initialized game thread, working session tracking, a running
server and an attached player whose source matches the exact current session.
It rechecks the session and player immediately before sending. A missing session
never selects the local host. Item IDs must be positive and resolve in the current
server database. This event preserves a full 32-bit ID; the grant API's
`MaxItemId` limit does not apply. A server catalog entry does not prove that a
custom item exists on a vanilla client.

`ItemRemovalStatus.Queued` means the native send returned, not that an
item was removed or a later inventory routing policy allowed it.
`Failed` means preparation failed before sending;
`SendUncertain` means sending threw and may already have queued the request.
There is no automatic retry: repeating an uncertain request can remove another
item. Other statuses identify unavailable hooks, the wrong thread, an inactive
server, an invalid ID or an unavailable session, player or item definition.

The game chooses a matching item and applies its normal held-item, equipment,
inventory callback and quick-slot behavior. Missing items or game rules can
prevent removal. This is not exact-instance confiscation, a remote inventory
query or an inventory receipt. The packet has no player-entity field and acts on
the receiving client's then-current local inventory, even if its player changes
after the server's final check. A modified client can ignore it.

Callers own permission checks, item eligibility, timing and rate limits. The API
does not transfer the removed item to storage, preserve its modules, grant a
replacement, amend saves or install an automatic anti-cheat rule. Live vanilla
delivery and equipped, held-only and absent-item behavior remain unverified.

### Server Inventory Routing Policy

`ServerInventory.Receiving` lets server mods inspect and reject
`PlayerAddItem` and `ItemRemoveFromPlayer` at Bolt dispatch, before forwarding
or local handling. It covers server-local sends, including the SDK grant and
removal APIs, and matching received events that reach server dispatch. Vanilla
clients need no extension. Check `State.Status` or service key
`openacai.sons.serverInventory`; `Installed` reports hook registration, not
verified enforcement.

Each managed request separates the immediate source, routing destination and
payload. `SourceConnectionId`, `SourceSession` and event-time `RegisteredSteamId`
describe the sender when available; local events have no remote source session.
Registration is not authentication or permission. `RoutingTargets`,
`RoutedEntityNetworkId` and `RoutedConnectionId` describe the envelope, not proof
that a recipient exists or is authorized. Connection IDs can be reused.

`Operation` identifies Add or Remove, not an observed inventory change.
`RequestedItemId` is the event field. Add requests also expose the raw
`RequestedPlayerNetworkId`, `RequestedAmount`, `HasItemInstanceToken` and
nullable `RequestedInstanceItemId`. The player ID is copied without resolving
an entity, so zero and unknown IDs remain visible. Token presence is separate
from a readable token item ID; a present but incompatible token is not treated
as an absent token. Instance modules, capacity and item ownership are not
validated or exposed. No native event, entity or item instance is retained.

The native add receiver requires its local player to match the payload player.
Without a token it constructs an item from the event ID; with a token it uses
that instance, without checking that both IDs agree. A policy that checks only
`RequestedItemId` therefore does not validate the instance-selected item.
Removal has no player, quantity or token fields: their snapshots are null or
false, not invented values. Its native receiver removes one item by ID from
the recipient's then-current local inventory using normal game rules.

Call `Reject()` during synchronous dispatch to skip this event's native routing
and local delivery. Rejection is sticky, stops later subscribers and returns
false on another thread or after dispatch. With subscribers present, wrong-thread
server dispatch, context-read failures after recognition and handler exceptions
reject the event. Rejected storage receives one release attempt; forwarded
storage remains native-owned. With no subscribers, on clients, or for unrelated
event types, native behavior is unchanged.

This is not an inventory transaction or a default anti-cheat rule. Native
storage withdrawals can already remove an item before queueing a grant;
rejecting that later grant does not restore it. Earlier client changes, other
messages, direct inventory calls and other mods' effects are outside this hook.
There is no denial reply, refund, retry or receipt, and each event in a bulk SDK
grant is decided independently. `QueuedCount` still counts completed send calls,
not policy approvals. No automatic sender, item, quantity or rate-limit rule is
enabled. Actual vanilla delivery, denial and storage interactions remain
unverified in a live host or dedicated server.

### Vehicle Control Policy

`ServerVehicles` provides opt-in policy before server dispatch routes
these native requests or delivers them to listeners:

| Event | Request fields beyond shared context |
| --- | --- |
| `SledGrabbing` | `RequestedPlayerNetworkId`, `RequestedSledNetworkId` |
| `SledReleasing` | The same IDs, plus raw `RequestedPosition` and `RequestedRotation` |
| `GolfCartShoving` | `RequestedShoveVector` |
| `GolfCartSlotChanging` | `RequestedSlotIndex`, `RequestedOccupantNetworkId`, `RequestedDisconnect` |

The shared `VehicleRequest` supplies `SourceSession`,
`SourceConnectionId`, `RegisteredSteamId`, `RoutingTargets`,
`RoutedEntityNetworkId`, `RoutedConnectionId`, local `ReceivedAt`, `IsRejected`
and synchronous `Reject()`. All request fields are unvalidated copies. The
source is the immediate transport sender, not proof of original authorship or
permission. A registered Steam ID is the game's registration observation, not
independent authentication. Zero or unresolved payload IDs remain available;
no native event, entity or connection is retained by the request.

Golf-cart requests use `RoutedEntityNetworkId` as their vehicle target, separate
from the slot request's claimed occupant. The inspected senders use
`EntityTargets.OnlyOwner`; the gate precedes both local owner handling and remote
owner forwarding when the event reaches this server dispatcher. A slot occupant
can be an AI entity and must not automatically be equated with the sender.
Sled requests instead carry a separate claimed player and sled. The native
release receiver compares the claimed player with the current translation
controller; it does not use the position/rotation payload in this game build.
That comparison alone does not bind the claim to the transport sender.

Rejecting stops this dispatch and later policy handlers, but sends no feedback
and does not undo prediction or earlier changes. Subscribed server context or
handler failures and off-thread server dispatch reject. Rejected storage receives
one release attempt; forwarded events remain native-owned. Clients, unrelated
events and requests without subscribers pass through. Check `State.Status` or
`openacai.sons.serverVehicles` for hook registration, not enforcement evidence.
Unsubscribe on shutdown.

No driver, owner, distance, rate-limit or seat rule is enabled by default.
Direct controller calls, replicated motion, vehicle damage, other vehicle
families and the audio-only `GolfCartImpactEvent` are outside these four gates.
Native vanilla clients need no extension to submit these requests; live host,
dedicated-server, owner-relay and rejection behavior remain unverified.

On the server game thread, `ServerWorld.TryReadGolfCart(networkId,
out snapshot, out status)` reads an attached cart or ghost cart. The managed
snapshot has `Entity`, `Kind` (`Cart` or `Ghost`), ordered
`ConnectedSlotNetworkIds` and local `ObservedAt`. The reader requires one matching
controller component bound to that exact network entity and its golf-cart state.
It rechecks entity/component/state/array identity, source/controller bindings,
slot count and every raw slot ID before returning.

These IDs come directly from replicated storage, not entity-resolution getters.
A nonzero unresolved link stays nonzero. Zero means no replicated reference was
observed, not that every local or offline seat is empty. Native cart conventions
use slot 0 for the driver and 1-3 for passengers, but the snapshot preserves the
actual array length. Do not call every link a player: the native driver helper
follows a ghost-cart state and its slot 0, while passenger links can refer to AI.
Use the explicit component kind and separate entity/player reads to investigate
the chain; a successful read alone does not authenticate the occupant.

`TryReadLogSled(networkId, out snapshot, out status)` requires the target's mobile
storage component and exactly one linked log-sled controller whose cached Bolt
entity matches. Its snapshot has `Entity`, raw `TranslationControllerNetworkId`
and `ObservedAt`. It rechecks component/state/entity identity, source/controller
bindings and the raw translation link. This link is distinct from the sled's
Bolt controller connection; it is not inferred from a resolved player or local
connection flag.

Both snapshots reuse `EntitySnapshot` for the vehicle's observed
transform, prefab, source/controller connections, session handles and server-owner
flag. Those are server observations, not proof of authoritative movement or
permission. Readers add no polling, mutation, packet, ownership transfer or
default decision. Missing/ambiguous components return `VehicleUnavailable`;
missing compatible state returns `VehicleStateUnavailable`; changed bindings
return `BindingChanged`. Other failures use the existing world-read statuses.
Failure returns false and null, not zero-filled vehicle data. Reads are not
atomic transactions, and separate reads can observe different moments. Runtime
prefab binding, replica freshness and multiplayer acceptance remain unverified.

### Server Construction Policy

`ServerConstruction` provides independent, synchronous policy events
for vanilla construction requests. Its placement and relocation callbacks are:

- `BlueprintPlacing` receives `StructureCraftingNodeCreate` before the server
  resolves the recipe, clears held relocation state or places a crafting node.
- `StructureRelocating` receives `DismantleScrewStructure` before the server
  checks the target and starts relocating it. Despite the native event's name,
  this is not a general destruction hook.

Check the corresponding `State.BlueprintPlacementStatus` or
`State.StructureRelocationStatus`. One hook can fail while the other installs;
`Installed` means patch registration succeeded, not verified enforcement.

Both requests have managed source connection, registered Steam ID, session and
receive-time fields. Game registration is not independent authentication or
permission, and requested targets are not source identity. Placement includes
raw prefab ID, position, quaternion, supporting-tree GUID, parent network ID and
`RequestedPlaceBuilt`. The prefab ID is not an inventory item ID; pose values
are unvalidated and can be interpreted relative to a parent. `PlaceBuilt` requests
use of held relocation state, not permission to create an arbitrary completed
structure. Relocation contains the raw target network ID, even when unresolved.
No native objects are retained.

Call `Reject()` inside the handler to skip that server callback. Rejection is
sticky, stops later handlers, and returns false after dispatch or from another
thread. Subscribed read/handler failures and off-game-thread dispatch reject the
request with bounded SDK diagnostics. Each hook leaves native handling unchanged
when its own event has no subscribers. Unsubscribe on shutdown. Allowing a request
only resumes native handling, including the relocation target/distance/support
checks and placement recipe/held-state handling; it is not validation or a receipt.

Relocation and later placement are separate operations. Denying the initial
relocation stops this callback before it registers dismantling and starts
removing the original structure. Denying a later placement does not restore an
original that was already removed, clear held state or send a replacement event.
Client placement can already close its preview and invoke local callbacks before
the server decides. No denial acknowledgement, retry, refund, rollback or UI reset
is supplied. A subscribing mod may send a separate optional-client notice.

These hooks cover only the two `FreeFormSystemNetworkProxyServer.OnEvent`
overloads. The inspected client blueprint sender uses `Others`, so this callback
gate does not suppress network relay or other recipients' callbacks. Ordinary
host-local blueprint placement calls the crafting system directly and bypasses
the hook. Free-form elements, procedural blueprints, ingredient changes, ghost
cancellation, held-structure throws, damage and other removal paths are separate.
Earlier mod callbacks and Harmony postfixes are also outside this boundary;
native event storage remains dispatcher-owned. No default permissions, area
claims, resource accounting or complete building protection are enabled.

Inside `BlueprintPlacing`, call
`ServerWorld.TryResolveBlueprintPose(request, out snapshot, out status)`
to resolve the request's current world pose. A zero parent ID leaves the raw pose
in world space. A nonzero ID must resolve to a live attached parent; its full
native `TransformPoint` handles scale and hierarchy, and its world rotation is
multiplied before the payload quaternion. The resolver checks the parent entity,
transform, matrix and rotation again before returning. It does not replace a
missing parent with the local player or treat unresolved local coordinates as
world coordinates.

Failure returns false with a null snapshot and an explicit
`PlacementReadStatus`. Wrong-thread, closed-request, missing/changed
parent and nonfinite-pose failures are distinct. Successful snapshots contain
`System.Numerics` world `Position` and `Rotation`, the matched `ParentNetworkId`
(zero for world-space input), and local `ObservedAt`. Quaternions are not
normalized or otherwise validated. A pose read is not permission to build,
recipe validation, source authentication or proof that later placement succeeded.
The resolver changes no state and sends nothing; parent movement after return
can change the pose eventually used by native placement. Use the world position
with a separately identity-bound range check when applying proximity policy.

Live remote placement/relocation, parent-relative poses, denial feedback and
native acceptance still require multiplayer verification. No client extension
is required to submit the native requests.

### Blueprint Ingredients

`ServerConstruction.IngredientAdding` receives
`StructureCraftingNodeAddIngredient` at server dispatch, before native routing,
forwarding or local listeners. Unlike the placement callback above, this gate
also precedes the inspected sender's `Others` broadcast relay. It covers matching
remote events and server-local sends that reach this dispatcher. Direct node
method calls, other event types and earlier client-side changes are outside it.
Check `State.IngredientAdditionStatus` independently of the placement and
relocation statuses. `Installed` means registration succeeded, not enforcement.

`BlueprintIngredientRequest` inherits source/session/receive-time
fields and synchronous `Reject()` from the construction request. It adds raw
`RequestedTargetNetworkId`, `RequestedItemId`, `RequestedAmount`, `RoutingTargets`,
`RoutedEntityNetworkId` and `RoutedConnectionId`. The payload target and routing
destination are separate. Item and amount are claims, not proof of possession,
proximity, valid ingredients, progress or a completed structure. No native event
or target is retained. The immediate source session does not establish original
authorship of previously relayed traffic.

`Reject()` stops this native dispatch and later policy handlers. Context/handler
errors after recognition and off-thread subscribed server dispatch also reject;
rejected event storage receives one release attempt. Forwarded storage stays
native-owned. With no subscribers, unrelated events or a client callback, native
handling is unchanged. Unsubscribe on shutdown.

Rejection sends no notice or native refresh, refunds no items and does not undo
sender-side prediction. The vanilla client can already apply its ingredient and
inventory changes before sending. Allowing routing is not acceptance: the
inspected native `TryAddIngredient` can return true without adding an ingredient.
Its return value must not be treated as a resource receipt.

On the server game thread, call
`ServerWorld.TryReadBlueprintProgress(networkId, out snapshot, out status)`
to read the current attached target's crafting-node ingredient links. It requires
the exact registered entity, matching node entity, recipe and link list. The
reader checks entity/node/recipe/list identity, list version, node flags and each
link's identity and counts again before returning. A missing component or recipe
is `BlueprintUnavailable`; detected changes are `BindingChanged`. Other context,
entity and failure statuses use `WorldReadStatus`. Failure returns
false and a null snapshot, not an empty or zero-filled progress report.

The managed snapshot contains `NetworkId`, `RecipeId`, `IsActive`,
`IsPendingDestroy`, `HasRuntimeIngredients`, ordered `Ingredients` and local
`ObservedAt`. Each link preserves `ItemId`, `DeclaredCount`, `DisabledCount`,
`AppliedCount`, `TotalCount` and `RemainingCount`. Native total excludes disabled
ingredients; remaining excludes applied ingredients as well. Counts are not
clamped or aggregated, and duplicate item IDs and a present empty list survive.
This is a current-node read, not an atomic transaction, player inventory,
contributor attribution, permission or proof that a built object was created.
No polling, mutation or network traffic is added. Live ingredient routing,
denials and progress reads still require multiplayer verification.

### Blueprint Completion

`ServerConstruction.CompletionRequested` gates
`StructureCraftingNodeComplete` before server routing. The native receiver can
call `StructureCompleted(false)` without checking that all ingredients are
present. This is a command, not a completion receipt. Its request exposes the raw
`RequestedTargetNetworkId`, routing fields, source/session context and synchronous
`Reject()`. Rejection and storage ownership follow the ingredient gate above.
No rule is enabled by default, and direct local completion calls are outside
this gate. Check `State.CompletionRequestStatus` independently.

`CompletionObserved` collects managed observations during server-side
`StructureCompleted` calls. Check `State.CompletionObservationStatus` for the
separate observation-hook group. Partial registration disables collection.
Subscribe before the operation and unsubscribe on shutdown; each invocation
uses its entry-time subscribers. No subscribers means no native context reads
or retained results for a new operation.

The observation contains nullable `BlueprintNetworkId` and `RecipeId`,
`WasRelocated`, `Outcome`, nullable `Source`, `CreatedEntities`,
`RegisteredStructures`, `HasReadFailures` and local `ObservedAt`.
`Outcome` describes the patched call at the observer's finalizer:

- `Returned`: the original ran and reached this hook's postfix.
- `Skipped`: the original was skipped and the postfix ran.
- `Faulted`: an exception reached this finalizer, including errors in other patches.
- `Unavailable`: neither a postfix outcome nor an exception was observed, for
  example when another finalizer already suppressed an exception.

None of these means construction succeeded. The native completion callback runs
before result creation, and a normal return can occur without producing a built
object. Later patches can still affect the operation after these hook points.

`CreatedEntities` records matching delayed-attach Bolt factory returns for the
blueprint's built prefab. Each result has a final `ReadStatus`, nullable
`PrefabId`, `IsAttached` and `NetworkId`. A network ID is exposed only while the
returned object is attached and resolves to that exact entity. Unattached
factory results are still observations; they are not replication receipts.

`RegisteredStructures` records previously absent database memberships observed
during the blueprint's matching last-child free-form deployment. Results retain
`TypeId`, `InstanceId`, `RegistrationOutcome`, `DeploymentOutcome` and a final
`ReadStatus` for that exact membership. Registration precedes parts of native
element conversion, so a registered structure can be reported even if conversion
later faults. It is not proof of a finished, saved or replicated structure.

`Source` is present only within an inspected ingredient or completion-command
receiver for the exact same node and network ID. It copies `Kind`,
`RequestedTargetNetworkId`, optional `RequestedItemId`/`RequestedAmount`,
`SourceSession`, `SourceConnectionId` and `RegisteredSteamId`. It identifies an
enclosing request and its immediate transport source, not the only contributor,
an authenticated human, or the original author of relayed traffic. Local calls
have no inferred host or owner identity.

The matching rules describe synchronous scope, not exclusive causality: another
mod can invoke matching factories or registrations within that scope. Nested
completion/deployment scopes are isolated, and operations started by observation
handlers are excluded to prevent feedback loops. Handlers receive no native
objects or decision window; their exceptions are isolated. Finalizers release
temporary references and leave the incoming exception unchanged. Empty results
or `HasReadFailures == false` do not prove that every possible completion path
was covered. Live hook execution, failure paths and multiplayer correlation
remain unverified.

### Free-Form Building Policy

`ServerFreeForm.Receiving` exposes `FreeFormPlaceElement` before the
server's native callback resolves the player, validates the module/target, relays
an animation or applies placement. `State.Status` reports hook registration, not
multiplayer enforcement. With no subscribers, placement-request handling is
unchanged. On ordinary game-thread client callbacks the shared native method
also remains unchanged.

`FreeFormRequest` inherits managed source/session fields and the
synchronous decision window from `ConstructionRequest`. It adds the
raw database version, module/item IDs, placement mode, element index, pickup and
claimed-player network IDs, pose vectors, supporting-tree GUID, held-item count,
prediction flags and action number. Target, editing and four support selections
each contain a construction type ID, instance ID and direction. Those are not
Bolt network IDs. With the corresponding anticipation flag set, the instance
field can instead name the action that predicted that structure. Interpret it
with the flags and database version, not as an unconditional world-object ID.
No world lookup, normalization, inventory ownership check or
permission verdict is implied. `ClaimedInvalid` is client input, not validation.

`RequestedPosition`, `RequestedAxis` and `RequestedLookAtPosition` can be
parent-relative: the native sender substitutes relative values when its
`TargetInfo` selects a relative transform. Anticipation flags identify predicted
structure references; they are not coordinate-frame flags. Do not pass these
raw vectors to a world-space distance check. The blueprint pose resolver above
does not resolve free-form placement frames.

During `Receiving`, call
`ServerWorld.TryResolveFreeFormGeometry(request, expectedSteamId, out snapshot, out status)`
for world-space position, axis and look-at values. The nonzero expected Steam ID
must match the current game identity of the exact remote source session. The
resolver checks its live transport, registered player and native `UserData`
binding before and after resolution. Server-local requests have no such remote
binding and return `SourceUnavailable`; the claimed-player field is not a fallback.

Positive target type and instance IDs select the target-structure frame. Ordinary
references use the native database-version check before structure lookup. With
the targeted anticipation flag, the instance field selects that source player's
predicted action instead. Without a target, a live pickup can select a parent
`BoltEntity`, including through its nesting-parent provider. With no relative
frame, the input vectors already describe world space. An unresolved nonzero
pickup or target is unavailable, never a substitute world-space result.

Position and look-at use native `TransformPoint`; axis uses `TransformDirection`
without normalization. Source and frame identities, matrix and rotation are
checked again before return. The managed snapshot contains world `Position`,
`Axis`, `LookAtPosition`, `InputFrame`, resolved construction IDs or pickup-parent
network ID where applicable, `PlayerNetworkId` and local `ObservedAt`. Construction
instance IDs are not Bolt IDs or prediction action numbers. The raw hit normal,
editing structure and supports are not resolved by this API.

Use `TryResolveFreeFormStructure(request, expectedSteamId, selection, out snapshot, out status)`
to observe one selected structure independently. `FreeFormSelectionKind`
selects `Target`, `Editing` or `Support1` through `Support4`. The result contains
resolved type/instance IDs, the structure transform's world origin, whether the
source player's anticipation lookup was used, the bound player ID and read time.
Editing and support origins do not replace the request's coordinate frame.

This read requires a live server receiver, the exact remote source/player binding
and a claimed player ID matching that binding. It follows each selection's own
anticipation flag; ordinary references preserve the database-version check and
its effective instance ID. Resolved identity, transform and source are checked
again before returning. Missing predictions return without invoking the native
rejection fallback. No native structure, player or transform is retained.

`SelectionAbsent` returns false with a null snapshot when the native selection
branch would be skipped: Target requires positive type and instance IDs; Editing
and supports require a positive type only. A selected but unresolved reference
returns another failure status. A prediction action number is not the resolved
instance ID. These are per-selection reads, not one atomic observation of the
entire request, full structure extents, snapped placement, support cascades or
every object a construction module could mutate. They grant no permission and
provide no inventory or completion receipt. Existing geometry behavior is unchanged.

Failure returns false, a null snapshot and `PlacementReadStatus`,
including separate source, target, prediction, stale-structure and changed-frame
outcomes. The resolver is valid only inside the synchronous callback on the
server game thread. It borrows the actual callback proxy only until dispatch
closes; retained requests and snapshots hold no native proxy afterward.

This is a geometry read, not full native `TargetInfo` generation, recipe/module
validation, permission, inventory accounting or proof of placement success. It
does not place, reject, send feedback or invoke the native rejection path; a
native lookup may emit its own diagnostics. Source or frame state can change
after return. Use the world position with a separate identity-bound range check
when applying proximity policy. Remote, predicted and nested-parent placement
still require live multiplayer verification.

`RequestedIsPlacing` distinguishes placement processing from the animation path.
They are separate messages, not one atomic operation. The sender's action number
correlates prediction state; it is not a unique or authenticated transaction ID.
Rejecting an animation message does not automatically reject a later placement,
and rejecting placement does not undo an earlier animation. Evaluate each request.

`Reject()` skips this native callback without sending anything. First rejection
stops later handlers; late or cross-thread decisions return false. Subscribed
read/handler errors reject the request, and subscribed off-game-thread callbacks
are rejected before native reads. SDK diagnostics are bounded and omit payloads.
Allowing a request resumes native checks; it does not establish that placement
will succeed. Unsubscribe when the mod shuts down.

`RejectWithNativeFeedback()` also requests one native `FreeFormStructureRejected`
notice using the captured action number. After handlers finish, the SDK closes
the decision window, resolves the exact source session again and sends only to
that connection using reliable ordered transport. It never substitutes another
session with the same connection ID. This native notice needs no client extension.
It is not an OpenACAI free-text announcement or a tracked optional-client notice.

The inspected vanilla receiver uses the action number to clean up matching
prediction lists. It destroys surviving predicted creations only while their
instance ID is still zero, and reactivates surviving predicted dismantled
objects and their children. It does not recreate destroyed objects, restore all
previous element/transform states, replay dependent actions, or refund inventory.
If no matching prediction entry remains, that cleanup does nothing. The notice
contains no reason text and is not a general placement-error dialog.

Retain the request to read `NativeFeedbackStatus`. It starts `NotRequested`, becomes
`Pending` when explicitly requested, then reports `Unavailable` for an absent or
departed source/session service, `Failed` for preparation failure, `Queued` when
the send returns, or `SendUncertain` if the send throws after being attempted.
Neither send outcome confirms receipt or client cleanup. No automatic retry is
performed. Ordinary rejection and handler exceptions do not request feedback on
their own; an earlier explicit feedback choice remains effective if a handler throws.

Rejection deliberately does not run the native failure branch's fallback pickup
spawning, refund inventory, cancel server animations or restore authoritative
world structures. Clients may already have consumed items or predicted changes.
A placement denied after consumption may therefore cost the client its item.
A native rejection notice is not a complete rollback or an inventory receipt;
policy authors must account for those effects before denying valid placements.

This is a callback policy, not a general network firewall. Earlier transport
relay, other callbacks, Harmony postfixes, direct module calls, blueprint and
procedural placement, and other construction messages are outside its boundary.
The SDK does not change the event payload, manually release event storage, or
enable default construction rules. Live phase ordering, native feedback,
prediction cleanup and inventory behavior remain unverified.

### Construction Snapshot Requests

`ServerFreeForm.SnapshotRequested` runs before the server's
`FreeFormRequestAllData` callback serializes and sends the full construction
database on native blob channel 1. Vanilla clients can use their normal request
path; they do not need an extension.

The request contains managed source context: connection ID, any event-time game
registration, exact session when resolvable, and receipt time. The native request
has no payload fields. A missing session or registration can occur during joining;
neither a connection ID nor a registration is independent authentication.

Call `Reject()` synchronously to skip that native callback. Rejection is sticky,
stops later handlers and is unavailable after dispatch or from another thread.
With a subscriber, context-read failures, handler exceptions and off-game-thread
server calls also reject. Without a subscriber, native behavior is unchanged.
`State.SnapshotRequestStatus` and `SnapshotRequestDetail` report installation
independently of the placement and replication-authority hooks.

There is no automatic throttle, denial message, deferred send or retry. Denying
an initial request may leave the client waiting for construction data. This hook
does not cancel earlier transfers, intercept direct snapshot sends, release event
storage, or confirm that an allowed request queued or delivered a snapshot.

Native snapshot application clears and rebuilds the client's construction
database and can wait for supporting entities. It is not an incremental repair.
The SDK does not expose a forced-resync operation here. Live request rejection,
normal joining and snapshot application remain unverified.

### Client Snapshot Application

The optional client defers `FreeFormSystemNetworkProxyClient.LateUpdate` while
the native construction database's `IsLoadingSave` flag is set. The game's blob
application uses this flag while rebuilding structures, including waits for
supporting entities. Its normal replication queue checks a separate blob-wait
flag; the loader adds the loading-state check before that queue is processed.

Received updates can still enter the native queue. The loader does not remove
commands, advance versions or clear loading flags. Native snapshot completion
sets the database version and discards obsolete queued commands. When native
loading ends, the next client update resumes its normal checks and processing.

`ConstructionSync.State.Status` and `Detail`, also exposed through
`openacai.sons.constructionSync`, report hook registration, not loading progress
or snapshot success. Off-game-thread updates and loading-state read failures
are deferred, with bounded diagnostics. No subscription is required.

This covers the client's native command-queue update only, not every construction
callback or direct world mutation. It does not repair a failed snapshot, force a
stuck native loading flag to clear, or protect vanilla clients from the server
side. Native completion notifications may still run after a caught apply error;
they are not proof of a successful rebuild. Live load/queue ordering and ordinary
construction after loading remain unverified.

### Requesting a Client Snapshot

`ConstructionSync.RequestSnapshot()` lets an optional-client mod
explicitly request the server's current construction snapshot after joining.
Call it on the game thread at a quiet point in construction. It requires the
sync guard, an active connected client, initialized construction components and
a registered construction blob channel. It returns `Busy` while native loading,
blob waiting, blocking construction work, pending local predictions or an
incomplete command batch prevents a new request. Missing components return
`Unavailable`; host/server and disconnected callers return `NotClient`.

The call uses the existing client blob helper to begin waiting, then the game's
reliable server-only request. `Requested` means that native call returned, not
that the server accepted it or that the rebuild succeeded. `RequestUncertain`
means the wait/request step threw: waiting may already be set and a request may
have been queued. `Failed` means preparation threw before either step;
`WrongThread` makes no native calls.

There is no automatic retry, timeout, denial response or forced wait reset.
The normal native completion path owns version reconciliation and wait cleanup.
A server policy rejection or failed transfer can leave construction updates
waiting until the native lifecycle recovers, potentially requiring a reconnect.
This is not a repair for stuck loading or unresolved predictions. It does not
cancel local actions or prevent new ones while waiting; callers must keep their
own construction workflow idle until native loading has finished. No new wire
message or server extension is required by this API, but live refresh and
post-refresh construction remain unverified.

### Free-Form Replication Authority

The loader checks these server-authored construction messages before native
dispatch and relay:

| State | Messages |
| --- | --- |
| Lifecycle | `FreeFormStructureRejected`, `FreeFormStructureCreationValidated`, `FreeFormStructureCreated`, `FreeFormStructureDestroyed` |
| Edits | `FreeFormStructureEditedTransform`, `FreeFormStructureEditedElements`, `FreeFormStructureInsertedElement`, `FreeFormStructureEditedLinks`, `FreeFormStructureCascadeRepositionSupported`, `FreeFormStructureDamagedStatus` |
| Connections | `FreeFormFreeLinkChange`, `FreeFormSupportingScrewLinkChange`, `FreeFormSupportedByScrewLinkChange`, `FreeWireConnectorChange` |
| Batches | `FreeFormLargeBatchBegin`, `FreeFormLargeBatchFinished` |

`ServerFreeForm.State.AuthorityGuardStatus` and `AuthorityGuardDetail`
describe this hook independently of the placement-policy hook. The original
`RejectionGuardStatus` and `RejectionGuardDetail` properties remain available and
report the same shared hook. `Installed` means patch registration succeeded, not
live enforcement proof. The guard runs without a `Receiving` subscriber.

On a server, these events are dropped when received from any remote connection,
regardless of their requested recipients. Native and SDK messages created
locally on the server remain allowed. This protects vanilla clients through the
server; they do not need the optional client extension.

On a client running the loader, a received message in this list must come from the
current `BoltNetwork.server` connection, with matching native pointer and connection ID.
Locally created events remain unchanged. A received event outside an active
client/server role, an authority-read failure or an off-game-thread dispatch is
dropped. Dropped events do not reach the original dispatcher; the guard makes
one storage-release attempt. Diagnostics are bounded and omit player data.

The native creation-validation message associates a local prediction with a
server-assigned structure ID. Created and destroyed messages reconcile client
prediction and queue versioned structure changes. Edit and connection messages
feed the same command queue; batch messages delimit groups of commands. The guard
checks authorship before those callbacks run; it does not change their payloads,
ordering, version handling or resulting world operations. These messages are not
client placement or dismantling requests, which retain their separate policy hooks.

The client-side check identifies the immediate connection, not the original
author of traffic relayed by an unprotected server. Server-side protection is
therefore required to prevent peer-originated construction-state relay. This does
not authenticate a server, validate payloads or action numbers, distrust local
mods, prevent direct callback calls, or cover construction messages outside the
list above. Decoding happens before this guard; it is not a malformed-packet or
rate-limit defense. It does not change the game's event filter or add permissions,
inventory refunds or world rollback. Live relay suppression, prediction
reconciliation, legitimate updates and batch completion remain unverified.

### Blob Endpoint Guard

Full construction snapshots use the game's separate blob transport, not Bolt
events. The optional client checks each incoming data block against that
transport's configured connection endpoint before native block assembly.
The native endpoint comparison includes the IP address and port, when present,
and the Steam ID. Mismatched or missing senders are dropped. An endpoint-read
failure also drops the block, with a bounded diagnostic that omits endpoint data.

`BlobTransport.State.Status` and `Detail`, also available through
`openacai.sons.blobTransport`, report hook registration. `Installed` is not proof
of a received or rejected block. No subscription is required. The check runs on
the transport worker and does not read game-thread player/session state.

This guard covers connected blob transports across channels. Listening
transports, which have no configured connection endpoint, retain native handling.
Allowed blocks keep their native acknowledgment, assembly and delivery paths.
Dropped blocks do not enter those paths; the loader does not free or alter the
transport's reusable input buffer.

The guard does not authenticate the server cryptographically, prevent source
address spoofing, validate block contents or sizes, or change control packets.
It does not secure a vanilla client from the server side, add a file installer,
or enable custom client behavior. Native transfer completion, joins and snapshot
application with this guard remain unverified.

### Server Chat Policy

`ServerChat.Receiving` lets a server mod inspect and reject chat before
the game's `CoopServerCallbacks.OnEvent(ChatEvent)` body. It requires no client
installation and is installed independently of the legacy SDK startup. Check
`ServerChat.State.Status` before relying on it; `Installed` means
patch registration succeeded, not that multiplayer behavior has been verified.

Each request contains managed copies of the message, claimed sender, transport
connection ID and any Steam ID in the game's registration table for that
connection. `ClaimedSenderNetworkId` is untrusted payload, not actor identity.
`RegisteredSteamId` can be absent and is not a separate Steam authentication or
permission verdict. A null connection denotes a server-local event; connection
IDs must not be retained as player identity across sessions.

Call `Reject()` synchronously from the receiving handler to stop that callback
and the later owned SDK command prefix. The first rejection stops dispatch.
Rejection cannot be undone, and attempts after dispatch or from another thread
return false. A handler exception or unreadable context rejects that message
when a policy is subscribed. With no subscribers or matching OpenACAI command,
ordinary game-thread handling is unchanged. Mods should unsubscribe when shutting down.

The same policies also receive negotiated optional-client commands, distinguished
by `Origin == ClientCommand`. Native chat uses `GameChat`. Rejecting either origin
prevents its command handler; the optional route never falls through to native chat.

The native callback contains the ordinary chat rebroadcast, but this hook is
not a general packet firewall. Other routes, earlier mod callbacks and Harmony
postfixes are outside its boundary. It does not send announcements, validate
all client traffic, or prove that a client displayed or suppressed a message.
In-game and multiplayer acceptance remain pending.

### Native Admin-Request Policy

`ServerAdminRequests.Receiving` lets a server mod inspect or reject
native `AdminCommand` requests from vanilla clients. It runs before
`CoopServerCallbacks.OnEvent(AdminCommand)` calls the native command receiver,
including that receiver's logging, permission checks and mutations. Check
`State.Status` before depending on it; `Installed` reports hook registration,
not verified enforcement.

The inspected game receiver recognizes these exact, case-sensitive names:
`playerkick`, `playerban`, `setplayerguest`, `setplayertrusted`, `setplayeradmin`,
`setcheatsenabled` and `setpvpdamage`. Its native sender uses a reliable ordered,
server-only event. This is separate from slash commands registered through
`Commands` and is not a general console or RCON command hook.

Each `AdminRequest` contains raw `Command` and `Data` strings,
`ReceivedAt`, the transport `SourceConnectionId`, event-time `RegisteredSteamId`
when available, and an exact `SourceSession` when resolvable. Strings may be null
or unknown; no parsing, normalization or target lookup is performed. A requested
player ID in `Data` is not source identity. Game registration is not independent
authentication or permission. A null connection denotes a server-local event;
the SDK does not treat it as a separate authorization verdict.

Call `Reject()` synchronously to skip this native callback. The first rejection
stops policy dispatch and cannot be undone. Later or cross-thread attempts return
false. A subscribed handler exception, unreadable context or off-game-thread
dispatch rejects the request. SDK dispatch diagnostics are bounded and omit
command data; permitted requests still reach native logging. With no subscribers,
the hook leaves handling unchanged. Unsubscribe when the mod shuts down.

This is an additional restriction, never a permission grant. Allowing a request
only resumes the game's existing source-role and protected-target checks; it
does not establish authorization, execution or success. No default rules,
role changes, denial messages, retries or rollback are supplied. Rejection does
not undo client-side UI actions or other callbacks. Direct native helper calls,
other command routes, earlier mod callbacks and Harmony postfixes are outside
this hook. Native event storage remains owned by the dispatcher.

Mods can use the source session for their own identity policy and send an
optional-client notice separately. Vanilla clients do not gain a new free-text
reply channel. Live menu requests, denial, preserved native permissions and
listen-server handling still require multiplayer acceptance.

### Session-Bound Commands

`Commands.Register(name, authorize, execute)` adds a server command
without a SonsSdk dependency. Remote players submit `/name arguments` through
ordinary game chat; no client extension is required to invoke it. Registration
does not expose local menu actions or native admin/debug commands. No commands
or permissions are enabled by default. `HooksInstalled` reports the required
chat/session hook registrations, not successful multiplayer operation.

Names contain 1-64 ASCII letters, digits, dots, underscores or hyphens, without
the leading slash. Matching is ordinal and case-insensitive; duplicates throw.
Keep the returned `IDisposable` for the mod's lifetime and dispose it on shutdown.
Registration and disposal are thread-safe. Disposal removes only that exact
registration, not a later replacement. A command captured before disposal can
still finish; this is not cancellation of an in-flight callback.

The chat hook captures the matching registration, reserves server command
capacity, then runs every subscribed chat policy before command authorization.
Rejection or a policy error prevents
execution. Once accepted, a recognized command is consumed on success, denial,
invalid input or failure; it cannot fall through to a same-named SDK command or
ordinary chat relay. Unregistered messages retain the compatibility/native path.
Removing a registration inside chat policy does not cancel a captured command;
use `Reject()` to stop the current request.

The mandatory `authorize` callback must explicitly return true. It and `execute`
run synchronously on the game thread, only after resolving the actual source
session and a nonzero game identity. They never use the claimed chat sender as
identity. The context contains `Name`, the exact session, a managed identity/role
snapshot, receive time and untrusted `Arguments`. No game role is automatically
trusted. Apply the toolkit's own authorization policy, including its treatment
of missing roles, banned players and identity provenance.

Identity and role are read again after authorization; a changed identity/role,
closed session or unavailable service prevents execution. This is not an atomic
transaction, independent Steam authentication or automatic
re-authorization for deferred work. Handlers must finish synchronously and
validate every argument before performing an operation.

Remote commands share a game-thread intake budget across native chat and
negotiated submissions: each session can burst eight commands and sustain four
per second, while the server can burst 64 and sustain 32 per second. Excess
requests are consumed without running chat policy, authorization, execution,
reply or `Completed` handlers. The local listen-host command path is separate.

The first space or tab separates the name from arguments. `Arguments` preserves
everything after that one separator, including further whitespace and quotes;
there is no built-in tokenization or quoting language. This API accepts at most
4096 UTF-16 code units including slash/name, rejects malformed surrogate pairs,
line separators and controls other than tabs. This is an SDK limit, not a claim
about the vanilla input widget's configured limit or usable packet capacity.

Return an `CommandResult` with a status and optional reply. A null
reply sends nothing. By default, non-null replies use `SendToSession` and its plain-text,
256-byte UTF-8 limit; they reach only peers that negotiated notice support.
To reply through existing vanilla chat, use the four-argument registration
overload with `CommandReplyChannel.GameChat`. That channel applies
to success, denial and error replies, requires the pre-forwarding chat hook,
and leaves commands already registered by compatibility mods in place.
The three-argument overload retains negotiated notices for compatibility.

`CommandReplyChannel.InlineOnly` is for request/reply traffic from a client
mod. The reply is returned only to the submitting `ClientCommands` handle in
its command result (512-byte UTF-8 limit) and is never shown as a notice or
chat line. Typed game-chat invocations, and submissions from a client whose
Loader did not negotiate command replies, are refused as `Unavailable` before
input checks, authorization or execution; typed input is not forwarded to chat.
Like GameChat, it cannot take the name of a registered compatibility command.
`CommandContext.Origin` reports `GameChat` or `ClientCommand` for every channel.
On a remote host, an unregistered name returns `NotFound` to the submitter, which
a client can use to detect whether the host runs a given command. A listen host's
own submissions reach only `RegisterHost` handlers, so a mod that wants the same
command for the host's local player registers it there as well.

`ServerChat.SendToSession(session, text)` also sends ordinary chat
directly to one current remote session. It accepts up to 512 UTF-16 units of
plain single-line text, rejects rich-text brackets, malformed surrogates and
control frames, and uses a server sender rather than a client-claimed identity.
It does not require a local player, client mod or notice handshake. Native
delivery and display still require multiplayer acceptance on the target build.

For game-chat commands, `Completed.ChatReplyStatus` reports the reply attempt;
the existing `ReplyStatus` and `ReplyDelivery` remain null. `Queued` means the
native send returned, and `DeliveryUncertain` means sending began before an
error. Neither confirms client display. `TrackReply=true` returns
`UnsupportedTracking` without sending a game-chat reply; it does not undo the
command. Use negotiated notices when a client receipt is required.
No player sender is borrowed and no unconfirmed client receives a notice payload.

`Commands.RegisterHost(name, authorize, execute)` opts a command
into the listen host's local chat input. Its separate context contains `Name`,
`Arguments`, `ReceivedAt` and a captured `Player`, rather than a remote session.
`Player.SteamId` is the local Steam account observation; `Player.IsCurrent`
checks the same live player attachment and account on the game thread.
`LocalHost.TryGetPlayer(out player)` offers the same capture for
operations such as selecting the host as an invitation target. It is unavailable
on dedicated servers, remote clients, or before the host player is attached.
No ticket validation, client integrity or administration permission is implied.

Host registration uses the same name and input rules, but has its own lifetime
handle and does not replace a remote registration. Earlier chat-input prefixes
can suppress the command. Host authorization and execution do not raise the
remote `Receiving` or `Completed` events; enforce local policy in the host handler.
Recognized host input is consumed, including denied or failed commands. Replies
use the same live native chat box, with no network send. Game-chat text limits
apply, and `TrackReply=true` suppresses the unsupported tracked reply. The host
player is rechecked after authorization and before displaying a reply.
`HostHooksInstalled` reports registration only; host input, mute ordering and
local display still require in-game acceptance.

The existing `(status, reply)` constructor remains untracked. Use the overload
`new CommandResult(status, reply, trackReply: true)` to request
negotiated notice receipts instead. It calls `SendTrackedToSession` with the same
source session and text rules. There is no untracked fallback: an older notice
peer reports `ReceiptsUnsupported`, and a vanilla or unavailable peer cannot
receive the reply. A null reply still sends nothing even with `TrackReply` set.
Automatic invalid-input, authorization-denial and exception replies remain
untracked; the option belongs to the handler's returned result, not registration.

`Completed` observes routed outcomes after the reply attempt, including denial,
invalid input and callback failure. It carries the command name, source session,
first identity observation, last identity-read status and nullable `ReplyStatus`,
without copying submitted arguments or reply text. Reads can succeed but detect
a changed identity, leaving the command `Unavailable`. A null identity-read status
means no read was attempted; a null reply status means no reply was requested.
Observer exceptions are isolated. Requests stopped by intake limiting, chat
policy or context-read failure never reach this event.

For an opted-in reply, `Completed.ReplyDelivery` retains the managed tracking
handle when a notice was reserved, including `SendUncertain`. It is null for
untracked replies, absent text or sends rejected before reservation. Retain it
and read `State` later to observe progress; `Completed` fires once after the send
attempt, not again for each receipt. `ReplyStatus` stays the original send result,
while the handle's state may advance. Client acceptance or a reported HUD call
does not establish that the command succeeded, nor that a person read its reply.

Callback exceptions and null handler results report `Failed`. Default denials,
invalid-input errors and unavailable-session messages also use the optional
notice path. Reply failure does not change a handler's result, undo side effects
or retry execution. `Succeeded` is the handler's report, and `Queued` is not
delivery/display confirmation. Live vanilla invocation, policy ordering and
optional-client replies still require multiplayer acceptance.

### Optional Client Commands

Client mods can call
`ClientCommands.Submit("/name arguments", out request)`
on the game thread to invoke a registered OpenACAI server command without posting
to public chat. `IsSupported` reports whether the current connection negotiated
command version 1. This requires the optional component on both ends; vanilla
players retain the existing slash-command chat path.

Only commands registered through `Commands.Register` are reachable.
The server captures the registration, runs `ServerChat.Receiving`
policies, then uses the same mandatory authorization and identity/role recheck
as ordinary chat commands. `Origin` on the server's chat-policy request is
`ClientCommand` for this path and `GameChat` for native chat. The reserved command frame has a zero claimed sender;
its source session comes from the current negotiated transport connection.
Neither the origin nor the handshake grants permission. No commands or default
permissions are added.

Submissions include the leading slash and accept at most 256 UTF-8 bytes.
Command-name and argument rules are otherwise unchanged, including preserved
whitespace and no quoting parser. Invalid input, wrong thread, unavailable
transport/session, unsupported servers and a full pending queue send nothing.
The API has no fallback to native chat, legacy SDK commands, debug commands or
local menu actions. Older peers can retain notice support without supporting
commands. If session tracking was unavailable during negotiation, command support
is not enabled for that handshake.

`Queued` means native sending returned. `SendUncertain` means the request was
reserved but sending did not complete normally; it may have reached the server.
Both return a managed handle. Read its immutable `State` from any thread:

- `ServerReported` includes an `Outcome`: `Succeeded`, `InvalidArguments`,
  `Denied`, `Unavailable`, `Failed`, `NotFound` or `RejectedByPolicy`.
- `TimedOut` means no accepted result was observed within 30 seconds. The command
  may still execute, may already have executed, or may have been silently dropped
  by the server's command intake limit.
- `ServerUnavailable` follows observed disconnect/session reset; `Unavailable`
  follows disabled transport hooks. Neither undoes server work.

At most 16 requests are pending locally. Expiration runs during game-thread
maintenance, submission and receipt processing, not on a background timer.
Maintenance expires commands before refreshing native session state, so a
failed refresh cannot keep an overdue request pending. At a maintenance pass
that observes both expiry and a session change, expiry takes precedence.
Terminal states never reopen. Handles contain the per-connection sequence,
timestamps and outcome, not submitted text, tokens or native objects. Sequence
numbers are correlation values, not globally unique identifiers.

`ClientCommands.Completed` reports the first terminal transition of
each admitted request. Notifications are queued, never invoked inline by a
state update. A later game-thread maintenance tick drains a detached batch
before native session maintenance, so a failed refresh cannot hold up an already
queued completion. Completions created during that tick or its callbacks wait
for the next tick; reentrant maintenance does not drain them early. Each handler
is isolated from exceptions in other handlers.

The request's terminal state is visible before its callback. Dispatch is separate
from result processing, pending-request removal and session reset. This covers
server reports, timeouts, disconnects and transport disablement, not nonterminal `Queued` or
`SendUncertain` changes. Match the returned handle by reference; sequences can be
reused by a later connection. An old request's notification may arrive after a
new connection is established. No command text or capability token is exposed.

Subscribers present at dispatch receive the queued notification. Subscribing does
not replay previously dispatched completions; queued notifications can reach a
new subscriber. Progress remains readable through `State` without subscribing.
Delivery requires another maintenance tick, so shutdown without one can leave a
terminal handle without a callback. A completion is still only the recorded
local outcome, not proof of server execution, delivery or saved game state.

Requests use the existing reliable, server-only reserved channel; results are
targeted to the originating negotiated peer. The server consumes each increasing
sequence before policy or handler callbacks. Repeated or older sequences are
discarded without re-execution or replaying a result. Sequence exhaustion refuses
further submissions for that connection. Sequence tracking does not provide
exactly-once delivery, cross-connection deduplication, cancellation or rollback;
the separate server command intake budget limits callback work. There is no
automatic retry, including after timeouts or uncertain sends.

`Succeeded` is a server handler's report, not proof of game state, persistence,
honest server behavior or HUD presentation. A failed callback may already have
made changes. Existing `Completed` events and optional notice replies still run;
unknown or policy-rejected commands do not invent a command completion event.
Policy exceptions report failure and prevent execution. Reply notices and the
typed result are independent: either can be lost, and receiving one does not
prove receipt of the other. No reply text or arguments are copied into result
frames or this transport's logs.

This adds SDK access for client mod controls, not a built-in admin panel or an
automatic installation protocol. Live negotiation, policy enforcement,
mixed-version coexistence, disconnects and result handling remain unverified.

### SDK Command Compatibility

The owned `SonsSdk.Networking.ServerCommands` implementation keeps the SDK's
public registry, command delegates, result enum and argument helpers. Names
still use current-culture lowercasing; arguments retain the original space
splitting, including empty arguments. This is the compatibility command path,
not a new OpenACAI-only command registry or permission service.

Remote command identity comes from the event's source connection through the
game's Steam-ID resolver, not its claimed chat sender. An unresolved zero ID,
banned role, unavailable role manager or failed permission lookup cannot execute
a user command. For other identities, the game's role/default-role behavior
and numeric permission threshold remain intact. This is not independent Steam
authentication. The explicit `fromUser=false` local execution bypass remains;
mods must not use it for untrusted requests.

Null, empty, non-slash or missing commands return `InvalidCommand`; unavailable
handlers and execution exceptions return `CommandFailed`. An exception does
not undo an action's earlier side effects and never causes an automatic retry.
Chat handling consumes slash commands even when execution or the reply fails.
An event without a source connection neither executes nor broadcasts a reply.
Diagnostics omit the submitted command text and arguments.

Replies retain the legacy targeted chat formatting and caller-supplied sender
attribution. They are not genuine server-branded announcements. Earlier chat
policy rejection still prevents the later SDK command handler from running.
Standalone upstream SDK behavior is unchanged; live command and permission
outcomes remain unverified.

### Server Item-Drop Policy

`ServerItemDrops.Receiving` runs before the native server's
`OnEvent(DropItem)` body creates a pickup or moves a pre-spawned object. It needs
no client installation. `State.Status` reports hook registration, not successful
multiplayer enforcement.

The managed request separates the source connection and registered Steam ID
from requested prefab, bundle count, transform, physics flags and existing
entity ID. The prefab ID is not an inventory item ID. All requested values are
untrusted; neither a requested entity nor an item count proves ownership.
Vectors and rotation use `System.Numerics` value types. The request retains no
native event, entity, item-instance token or connection.

Call `Reject()` inside the handler to skip that native body. Rejection is sticky
and stops later handlers; exceptions and unreadable context also reject when a
policy is subscribed. Late or off-thread decisions return false. Without
subscribers, native handling is unchanged. Unsubscribe when shutting down.

Rejection does not refund inventory. In the inspected held-item path, vanilla
clients remove the held object before sending the request, without waiting for
approval. A rejection can therefore lose that item for the player. This hook
is for server enforcement, not inventory-preserving cancellation. It does not
delete a pre-existing entity, validate item-instance data, or cover other spawn,
pickup, storage or destruction routes. Earlier mod callbacks and Harmony
postfixes remain outside its boundary. No automatic rejection rules are enabled;
the toolkit owns policy. In-game acceptance remains pending.

#### Drop Results And Spawn Attribution

`ServerItemDrops.Handled` observes the scoped server callback after
it returns, is skipped, or faults. `State.ObservationStatus` reports registration
separately from the existing policy hook. Subscribe only when results are needed;
the loader does not poll the world or enable automatic cleanup.

`Source` copies the immediate connection, session handle, game-registered Steam
ID, requested prefab/count/pose and pre-spawned ID at callback entry. A missing
source means context was unavailable, not that the server originated the request.
`RequestedRawPreSpawnedNetworkId` preserves the wire value even when it cannot be
resolved. The policy request also exposes this additive property; its existing
nullable `RequestedPreSpawnedNetworkId` retains its resolved-entity meaning.

The inspected native callback moves an existing resolved `PreSpawned` object and
sends it `OnSpawned`; otherwise it calls
`BoltNetwork.Instantiate(PrefabId, Vector3, Quaternion)`. `FactoryResults` records
returns from that overload whose prefab and pose match the entry request while
the callback is active. It excludes factories invoked by this loader's policy
handlers or result observers. Each result copies the returned entity's prefab,
attachment flag and attached network ID, then checks the same binding before
delivery. `Status` distinguishes available, unavailable and failed reads; copied
IDs in unavailable results are historical observations, not live cleanup targets.

`Outcome` is the managed callback outcome, not a successful spawn receipt.
`PolicyAllowed` reports only this loader's policy decision. `HasReadFailures`
identifies incomplete reads. An empty result list does not prove that nothing
was created. Other Harmony patches can change inputs, results or control flow;
matching factory calls inside other callbacks remain dynamic-scope observations,
not a verified native call-site trace. Observers cannot reject retroactively;
their exceptions are isolated, and native exceptions are preserved. Native
references are discarded before observers run.

This API does not establish original client vehicle-spawn intent. Bolt's remote
entity materialization also handles rescope and reconnect traffic, and the wire
network-ID connection component is not authenticated player identity. Skipping
that decoder before consuming state would leave unread packet payload. Neither
the drop observation nor the vehicle-control APIs provide a general per-entity
spawn veto or justify punishing a player for an entity first appearing locally.
Use `Receiving` for the supported drop-request boundary; other spawn routes and
live multiplayer correlation remain outside this contract.

### Container And Breakable Requests

`ServerWorldObjects.Receiving` runs before the native server callbacks
for `StorageContainerOpened` and `BreakableObjectBroken`. These are loot-container
opening and network-object breaking, separate from structure-storage add/remove
requests. Vanilla clients need no extension.

A request contains `Operation`, the raw `RequestedTargetNetworkId`, an optional
`RequestedContentsSeed` for container opening, immediate source connection ID,
event-time registered Steam ID and the exact source session when available.
The target ID is copied without resolving an entity, so zero and missing targets
remain inspectable. Seeds, including negative values, are not normalized. These
are unvalidated request values, not proof of identity, proximity or permission.
`ServerWorld` can provide separate current entity observations.

Call `Reject()` during synchronous dispatch to skip that native callback.
Rejection is sticky; later handlers do not run after rejection. Calls from other
threads or after dispatch return false. With subscribers present, context-read
failures, wrong-thread callbacks and handler exceptions reject the callback.
With no subscribers, native handling is unchanged. Local server events are also
presented, with no remote session. Requests retain no native objects.

The container callback resolves `ContainerItemSpawner` and calls
`OpenContainer(true, seed)`. Native handling can choose a seed for negative
values, update replicated open/seed state, spawn contents and change triggers
or single-use save state. The break callback resolves `BreakableObject` and
calls its native break path while networking runs. Rejection prevents those
effects from this callback; allowing it only resumes native handling, not proof
that an object opened, broke or produced loot.

This is not complete object protection. Native breaking can already change the
requesting client and send a separate `WorldObjectLocatorState` event before
`BreakableObjectBroken`. These hooks do not intercept that event, direct calls
to object methods, other listeners or Bolt relay. They do not restore objects,
remove prior drops, refund items or send a denial response. Desynchronization is possible.
A blanket break-denial rule cannot be advertised as comprehensive protection.
The separate world-locator policy below can intercept that additional message;
it does not make the callback policy a complete or atomic destruction guard.

`State` reports container-open and object-break hook installation independently
under `openacai.sons.serverWorldObjects`; registration success is not enforcement
evidence. No default anti-cheat, ownership, distance or rate-limit rule is added.
Live host/dedicated-server acceptance, allowed and denied requests, loot behavior
and interactions with the separate locator-state path remain unverified.

### World-Locator State Policy

`ServerWorldLocators.Receiving` intercepts `WorldObjectLocatorState`
at the server's Bolt dispatch boundary, before native forwarding and local
listeners. It complements the container/break callback hooks above. Native
breaking can send this separate message to other peers before the break request;
a local listener alone is too late because Bolt queues forwarding first.
Vanilla clients need no extension.

Each request exposes `RawLocatorId`, `LocatorId`, `RequestedState`,
`KnownRequestedState`, raw `RoutingTargets`, immediate source connection ID,
event-time registered Steam ID, exact source session when available, and
`ReceivedAt`. The GUID and integer are copied without resolving a game object.
Native locator lookup uses only the first 12 GUID bytes. `LocatorId` zeroes the
final four bytes for comparisons; `RawLocatorId` preserves the complete payload.
These keys are not Bolt network IDs or proof that an object exists. Do not use
the full raw GUID as an exact native-identity key.
`WorldLocatorReads.TryRead(LocatorId, out snapshot, out status)`
provides separate local observations without changing the request or world.

Known states are `None=-1`, `Default=0`, `Cleared=1`, `Damaged=2`,
`Destroyed=3` and `Burning=4`. Unknown integers remain available unchanged,
with a null `KnownRequestedState`. A known value is not an authorization or
valid-transition verdict. Source registration is not proof of permission.

Call `Reject()` during synchronous dispatch to skip this message's native
forwarding and local delivery. Rejection is sticky and stops later subscribers.
Calls from another thread or after dispatch return false. With subscribers
present, wrong-thread server dispatch, context-read failures after recognition,
and handler exceptions reject the message. Rejected storage gets one release
attempt; forwarded storage remains native-owned. With no subscribers, unrelated
event types, or on clients, native handling is unchanged.

Both remote and server-local messages pass through this policy. Client-authored
locator changes are normal gameplay, so the SDK does not impose a server-only
sender rule. Local events have no remote source session. No default ownership,
distance, transition, rate-limit or ban policy is enabled.

Allowing a message resumes native routing, not proof of delivery or application.
The native receiver applies the state to a loaded locator or records it for an
unloaded locator and invokes registered callbacks. Rejection does not reverse
earlier client changes, remove drops, restore objects, send a denial response or
block separate `BreakableObjectBroken` callbacks. The two policies are independent,
not an atomic transaction. Other messages, direct native calls, earlier mod
effects and Harmony postfixes remain outside this boundary.

`State` reports registration under `openacai.sons.serverWorldLocators`.
Successful registration is not enforcement evidence. Live host/dedicated-server
relay, allowed and denied requests, unloaded locators, storage lifetime and
interaction with the break callback remain unverified.

### Server Locator State Requests

`ServerWorldLocators.RequestState(locatorId, newState)` explicitly
queues a native state-change event for the server and its peers. It requires
the installed locator policy hook, the game thread, a running network server,
a live save manager with no load in progress, and a successful local locator
read with at least one registered, recorded or unapplied observation. An
unobserved ID returns `LocatorNotObserved`; this does not prove that no such
object exists elsewhere. Read failures return `ReadUnavailable`.

Raw and canonical GUIDs use the same native 12-byte key as
`WorldLocatorReads`. All six defined locator states are accepted;
unknown enum values return `InvalidState`. A known value is not proof of a valid
transition for a particular object. The calling mod owns authorization, target
selection and transition rules. There is no default command or automatic change.

The call uses `WorldObjectLocator.SetStateNetwork` with native `Everyone` routing.
It does not call a holder setter or apply a local change first. The existing
`Receiving` policy can reject the queued server-local event before native relay
and local handling. Its source session is null: the native event does not carry
the identity of a player who may have asked the toolkit to perform the operation.
Authorize that player before calling this API, rather than treating a local
event as proof of permission.

`Queued` means the native network call returned. It is not proof of policy
acceptance, application, delivery, display or persistence. `Failed` describes
an exception before invoking that call; `SendUncertain` means it may have queued
work. No retry, cancellation, readback receipt or rollback is attempted.
Preflight reads do not pin a world generation or make the later dispatch atomic.

Native dispatch queues peers before applying local callbacks. Each receiver
uses its own loaded-holder or unapplied-state path, with the game's normal
refresh, callback and recording behavior. Local callback failure cannot recall
already-queued peer events. Registered, recorded and unapplied state can differ;
assigning the current value does not force a refresh or repair those differences.

This is locator-state assignment, not a general repair, respawn, inventory refund
or undo operation. `Default` is not a promise to recreate every destroyed object;
other break, drop, container, construction and save effects are outside this
request. No state dictionaries or save files are written directly by the SDK,
and no optional client extension is required for the native event format.
Actual object-specific transitions, server/client agreement, policy denial and
later save/reload behavior remain unverified in a live game.

### Server Storage Policy

`ServerStorage.Receiving` lets a server mod reject native
`ItemStorageAddItemEvent` and `ItemStorageRemoveItemEvent` requests before the
server callback changes structure storage. Vanilla clients need no extension.
Check `State.AddStatus` and `State.RemoveStatus` separately: one hook can install
even if the other fails. `Installed` reports registration, not runtime proof.

Each managed snapshot contains `Operation`, the source connection and registered
Steam ID, plus requested target network ID, storage slot, signed count and item
IDs. `RequestedItemId` is the separate event field; the native callback consumes
the item-instance token instead. `RequestedInstanceItemId` copies that token's
item ID, or is null when the expected token or instance is absent. Neither ID
proves item ownership or valid instance modules. The target ID remains available
even if the entity cannot be resolved, and does not establish permission to use
the container. No native object is retained.

`RequestedCount` is not normalized or converted to a delta. The native remove
callback negates it; validate the raw value before assuming the normal effect
of an add or remove operation. The source connection is transport context, not
a durable player ID; the optional game-registered Steam ID is not a separate
authentication verdict.

`Reject()` is synchronous, dispatch-thread-only and irreversible. It stops later
handlers and skips the native body, including its replies and fallback pickups.
Handler exceptions and unreadable context also reject when subscribed. With no
subscribers, native behavior is unchanged. Unsubscribe when shutting down.

Deposits and withdrawals have different client behavior in the inspected
standard storage UI path. Deposits send a request and then remove local inventory
without waiting for approval. Rejecting one also skips the native fallback pickup
and can lose that item; this API provides no refund. Withdrawals do not immediately
credit local inventory: successful server handling sends `PlayerAddItem` later.
A rejected withdrawal skips that grant. Neither operation receives a new denial
notification from this hook.

This covers these two callbacks, not every container, direct storage mutation,
pickup or item-transfer route. Earlier callbacks and Harmony postfixes are outside
the boundary. There are no automatic access rules, quantity limits or inventory
transactions. Live enforcement and specialized storage UI paths remain unverified.

### Server Structure-Hit Policy

`ServerStructureHits.Receiving` lets server mods inspect and reject
`StructureHitEvent` requests before the native server callback. It requires no
client modification or optional-client handshake. Check `State.Status`
or service key `openacai.sons.serverStructureHits`; `Installed` means the hook
registered, not that a multiplayer hit has been blocked.

The managed request contains the source connection ID, optional session handle
and event-time game registration, plus the requested target network ID, raw
damage, impact type, center and radius. Requested values are untrusted and are
not normalized. The target ID does not establish proximity, ownership or even
that the target supports structure damage. `RequestedDamage` is not a health
delta: the native handler uses its configured basic/explosion damage and falloff.
Use `ServerWorld` for separate server-visible actor and target reads;
its transforms are observations, not proof of valid movement.

Handlers run synchronously on the game thread, before the game's own target,
player-state and distance checks. Letting a request continue leaves those
checks intact; it does not force the game to accept the hit. `Reject()` is sticky
and ends handler dispatch. It returns false after dispatch or on another thread.
When subscribed, unreadable context, handler exceptions and off-thread calls
reject the request. With no subscribers, native behavior is unchanged.

This hook covers the `CoopServerCallbacks.OnEvent(StructureHitEvent)` route to
`ScrewStructureDestruction.ReceivedImpactFromClient` and its server-side health
changes and destruction. It is not a general building-protection switch: direct
server impacts, other construction systems and other mods' side effects are
outside this callback. No protection zones, rate limits or damage rules are
enabled automatically.

The inspected client sends its hit to the server instead of calling that damage
handler locally, but can already have played impact effects. Rejection does not
undo client effects, refund weapon costs or send a denial message. Live rejection
and replicated structure state still need multiplayer acceptance.

### Server Player-Hit Policy

`ServerPlayerHits.Receiving` lets server mods inspect and reject native
`HitPlayer` events before server routing. It requires no client extension. Check
`State.Status` or service key `openacai.sons.serverPlayerHits`; `Installed` means
hook registration succeeded, not that multiplayer damage prevention was verified.

Player hits differ from structure hits: the inspected `BoltPlayerSetup` receiver
handles the event only on the target entity's owner, where it calls
`PlayerStats.OnNetworkHit`. A remote victim's health is not changed in an ordinary
server callback. This policy instead runs at `Bolt.EventDispatcher.Dispatch`,
after the receive filter and before forwarding or local event delivery. It also
sees server-local `HitPlayer` events that pass through that dispatcher.

Each request separates transport source from routing and payload data:

- `SourceConnectionId`, `SourceSession` and event-time `RegisteredSteamId` identify
  the source connection when available, not necessarily a player-controlled attacker.
  Server-local events have no remote source; unavailable tracking leaves a null session.
- `RoutingTargets` is the raw native routing mode. `RoutedEntityNetworkId` is the
  envelope's entity, when present. Ordinary impacts use `Everyone` (1); the
  inspected rock/projectile branch uses `OnlyOwner` (9).
- `RoutedConnectionId` is an explicit envelope connection, if supplied. It is not
  the resolved owner of the routed entity and is not a durable session identity.
- `RequestedTargetNetworkId` is the separate payload Target field. The inspected
  player receiver uses its own routed entity, not that field, to choose the victim.
- Damage, impact type, weight, burn time and claimed attacker position are copied
  without normalization or validation. They do not prove a weapon, a legal attack,
  proximity or the resulting health change.

Use the session/world APIs for separate source and routed-entity observations.
Do not validate only the payload Target while ignoring the routing destination.
Unknown modes and missing destinations are reported as-is, not silently approved
as valid player hits. The loader installs no automatic damage or permission rules.

Handlers run synchronously on the game thread. `Reject()` is sticky, ends handler
dispatch, and returns false after dispatch or on another thread. When subscribed,
an unreadable recognized hit, policy exception or off-thread server dispatch stops
that event. The hook attempts one storage release for a rejected event because
native routing was skipped; release errors are logged without retry. Allowed
events retain native routing and storage ownership.
Without subscribers, ordinary routing is unchanged. Acquired strict-combat
connections remain subject to their retained legacy-event exclusion even after
the policy mod stops. The policy does not run on a client-only instance and is
separate from optional notice handling.

The inspected ordinary-impact and rock/projectile senders use unreliable entity
events, not `OnlyServer` requests, and neither fills the separate payload Target.
Their sender bodies do not directly change player health before sending. This
does not rule out effects in earlier weapon code or other impact delegates.

Rejection stops this server dispatch before it queues the hit for a remote owner
or invokes a local receiver. It does not undo earlier effects, refund ammunition,
send a denial message or control every route to player health. Direct/local damage,
other event types, traffic that never reaches this dispatcher, earlier mod effects
and Harmony postfixes are outside its boundary. It is not universal anti-cheat or
server-authoritative health. Live host/remote-victim rejection, normal allowed
damage and routing/storage behavior still require multiplayer acceptance.

### Server NPC Melee Contacts

`ServerMeleeImpacts.Receiving` lets a server mod reject a qualified
NPC melee contact before the direct `DamageNode.OnImpact` call changes node
resources or forwards damage. It supports vanilla clients. Check `State.Status`
or `openacai.sons.serverMeleeImpacts` for hook registration status; registration
does not establish working interception on a listen host or dedicated server.

The callback retains the exact `MeleeWeapon.OnTriggerEnter` contact, cached
payload and collider-local receiver. It requires a live, server-owned, unfrozen
NPC attachment, the actor/network backlink and one enabled actor weapon entry
whose GameObject exactly matches the source weapon. Descendant weapon objects,
temporary uncached payloads and ambiguous receiver lists are not qualified.

Requests copy original, remaining and accumulated damage, weight, melee type,
demonic flag, position, velocity, native impact ID and source identity. The
native ID can be zero or reused; it is not an attack sequence or replay token.
`HasRunningAction` reports a live actor-owned sequence, without claiming that
the sequence authorized the weapon activation. A zero animation-state hash is
valid for one of the game's native enable paths.

`ActorTypeId` and nullable `ActorClassId` copy the actor's type and an entry
from an already-existing class mapping; reading them never initializes that
mapping. A copied entry does not prove that population has finished.
`StatusEffectCount` is zero for an absent list and null when its count cannot
be read. It does not describe individual effects. `GameTime` captures the
native scaled time for this callback. Reads also require that time to remain
unchanged.

Use `TryReadPlayerDamageNode` with an existing player binding to verify the
target attachment and its exact node/controller/player callback route. The
target network ID and session alone are only routing hints. The resulting node
snapshot uses the shared collision-read budget and expires when this callback
closes. Identity comparison can relate it to a later fresh snapshot of the same
node, world and player binding. These reads do not calculate final health damage
or claim ownership of node resources.

Melee snapshots expose `MeleeSettings` with copied blunt, sharp and blocking
multipliers. Projectile snapshots leave this property null. These settings
do not resolve blocking, team multipliers, parent nodes or native admission;
the mod must qualify those paths before calculating damage.

`TryGetActivation` can return an `MeleeActivation` identity for an
observed native `OnEnable` that completed and replaced the weapon's cached
payload. A repeated enable request alone does not create another identity.
The SDK retains at most 256 current activations and eight nested activation
scopes. Missing observations and exhausted capacity leave the identity
unavailable; a later contact cannot manufacture its history.

The identity follows that component activation and payload lifetime, which
can include multiple contacts. It is not a unique animation swing. Its
`InitialRawDamage` is a copied observation, not a resource grant. `Matches`
requires a current request from the same retained source; `IsPristine` also
requires unchanged raw damage, zero accumulated damage and a zero native
impact ID. All native reads remain on the game thread.

`SetDamage` invalidates the existing identity, including when it writes the
same value. It never creates a replacement identity or refills a mod's
account. Changed source ownership, payload, configuration or world context
also makes the identity unavailable. Once invalidated, an identity cannot
become current again. Mods that own damage must retain their original grant
and consume its remaining resources across contacts.

`Reject()` is sticky and only accepts decisions synchronously on the game
thread. Rejection skips this node call, including its explosion fallback; it
does not undo earlier contacts or suppress later work in the outer weapon
callback. Handler exceptions and changes to the qualified context after policy
dispatch also skip the node call. Reads never generate an impact ID or invoke
damage callbacks.

Observation considers at most 128 matching node attempts per frame and retains
at most eight nested contact scopes. For ordinary connections, absent subscribers,
unqualified contacts, exhausted budgets and nested policy dispatch keep native
handling unchanged.

Acquiring `StrictCombatServerHits` also retains native melee exclusion for
that remote connection. Before optional observation, the hook follows the invoked
node's single `DamageController.OnImpact` callback and single
`BoltPlayerSetup.SendPlayerHit` callback to its cached player attachment. The
remote ownership source is matched to the retained connection; collider ancestry,
the event payload's target and current player registration are not substitutes.
This check does not consume the observation or collision-read budgets.

For an identified owned target, native handling remains suppressed without
subscribers, during nested policy dispatch, after failed source qualification
or when an observation budget is exhausted. Qualified observations still reach
policy so it can commit owned damage. Removing the subscriber, retiring Admin or
losing session tracking does not release the retained exclusion. A newly resolved
connection lifetime does not inherit it. Acquisition now requires both the combat
event dispatcher and melee hooks to be registered on the game thread.

This remains a specific native boundary. Unreadable or ambiguous callback routes,
server-local owners, non-melee senders, direct `DoImpact` calls and work before
the hooked method are not covered. Exclusion establishes neither a current damage
grant nor a legal attack. Authoritative damage still needs qualified source and
target identities, duplicate handling and owned resources. Live interception and
multiplayer behavior remain unverified.

### Managed lung oxygen delivery

`StrictCombatBreathingProtocol` pairs immutable lung oxygen with an unchanged
version 4 combat receipt. Servers must require capability `1UL << 12`, and clients
must offer it. The version 1 packet is 128 bytes: a 32-byte resource header followed
by the 96-byte receipt. Capacity and remaining air use separate double values;
unprovisioned state is explicit. Existing combat and rebreather formats are unchanged.

`StrictCombatClient.TryReadOwnedBreathing` returns only resources matched to the
accepted binding, life and health revision. When armour or tank delivery is also
required, the client waits for all matching fragments before accepting health.
Pending and final shot receipts remain separate. Conflicting fragments or resource
regressions retire the client; a retry cannot combine old health with current oxygen.

This delivers server state. It does not project native lungs, authorize breathable
air, apply drowning damage or enable strict combat. The shipping Client does not
advertise this capability.

### Managed rebreather wear decisions

`StrictCombatRebreatherWearProtocol` adds an optional server-owned wear decision.
Its capability is negotiated separately from refill requests. A client queues
`TryRequestRebreatherWear` with the accepted life and resource revision, then reads
`TryReadRebreatherWearResponse` for that exact decision. The server checks its
resource ownership and current permissions before changing the worn state.

A changed decision advances the resource revision without granting air or
canisters. Requesting the current state returns `Unchanged`; active refill work
prevents changing it. Retries retain the original accepted or denied decision.
Wear and refill requests share the client's single pending operation, timeout
and retry budget, with separate sequence histories on the server.

The wear response describes a historical decision. The client follows it with an
ordinary state request and accepts wear, tank resources, health and any required
armour together. Only the combined receipt becomes current projection state.
When wear negotiation is active, tank projection requires the accepted worn
state. Removing permission does not release native mutation guards.

Version 1 tank receipts and existing resource factories remain available for
peers without this capability. Those values have `HasWearState == false`; that
means the wear decision was not provided, rather than a confirmed removal.
Negotiated peers use version 2 tank receipts containing the frozen worn state.
The combat receipt and existing refill protocol remain unchanged.

This is an ownership contract, not a native equip or removal operation. A remote
held-item report, a local visual change or a worn-refill request cannot replace
server permission. Native equipment presentation, air consumption and oxygen
behavior still need their complete integration before this can enable strict
rebreather gameplay. Runtime and multiplayer behavior remain unverified.

### Managed rebreather ownership and refill requests

Native rebreather ownership is tied to the life captured at acquisition. Advancing
the shared health/life owner does not advance that equipment capture. After a
committed, correlated respawn, wait for the matching new-life resource receipt,
then call `StrictCombatClient.TryHandoffNativeRebreatherBarrier`. The handoff
requires the same native inventory, item, module, controller and breathing-data
objects, with stable alive/idle equipment and current accepted resources.

The retained hook entry is replaced in place, so no native receiver becomes
unowned during transfer. Old tokens retire; repeated successful handoff calls
validate the current capture without changing it. Missing or replaced equipment
is not adopted. Reacquisition cannot bypass the previous life owner. A temporary
native transition can make the handoff unavailable until the existing equipment
settles; failure never releases its mutation suppression.

Handoff does not grant resources or write native air. Use `ProjectLocalRebreather`
afterward to apply accepted server values. Respawn resource conservation remains
server policy; the client does not infer it from native inventory or air readings.
Native respawn equipment continuity and multiplayer behavior remain unverified.

`StrictCombatClient.TryRequestRebreatherRefill` queues an inventory or worn refill
start against the accepted server-owned resource revision. Requests require the
separate `StrictCombatRebreatherRefillProtocol.Capability` (`1UL << 9`) as well as
tank delivery (`1UL << 8`) and life-bound combat. Both required and offered
capabilities must include the complete mask. Tank delivery alone does not imply
refill support. These capabilities remain unadvertised by the shipping Client.

The request captures the exact binding, life, mode, resource revision and an
independent refill sequence. It shares the client's single pending-operation
slot, eight-attempt limit, 500 ms retry interval and eight-second deadline.
Retries retain the original payload. Ordinary combat requests and new respawn
requests cannot replace a pending refill.

Pass datagrams to `StrictCombatClient.TryReceive` and drive `Update` as usual.
Refill replies return no pistol receipt and leave `requestCompleted` false;
`TryReadRebreatherRefillResponse` exposes their separate result. After a correlated
reply, the adapter requests ordinary combat state so health, armour and tank
resources are accepted together before projecting changed tank values. Resources
in the refill reply describe that decision, not a current projection grant.

The separate version 1 protocol uses 96-byte requests and 168-byte responses;
existing version 4 combat packets and tank-delivery packets are unchanged.
Server implementations must retain accepted and denied results, distinguish
transport sequences from resource decisions, and recheck current permissions
before mutation. Requests authorize only a refill start. They carry neither an
air amount nor a completion claim. Breathing timing, native equipment handoff
and multiplayer acceptance remain separate unfinished work.

### Server AI-Hit Policy

`ServerVailHits.Receiving` exposes `VailHitEvent` requests before the
native server callback looks up the target AI actor. It requires no client
extension. Check `State.Status` or service key `openacai.sons.serverVailHits`;
`Installed` reports hook registration, not verified multiplayer enforcement.

The managed request copies the source connection/session and event-time game
registration separately from the sender-selected target network ID. It also
exposes damage-node ID, damage, weight, direction, impact type/subtype, velocity,
claimed sender direction, status type/duration and the hit-behind, combo, knockdown,
power-attack, blocked and armor-blocked flags. No native objects are retained.
The damage-node ID selects a node on the target; it is not a weapon or item ID.

These fields are unvalidated request data, not evidence of weapon ownership,
range, a legal hit or an actual health change. Type values remain integers and
vectors are not normalized. Use the session/world APIs for separate server-visible
source and target observations. A missing session or registration is not a
permission grant; the loader supplies no automatic combat or protection rules.

Handlers run synchronously on the game thread. `Reject()` stops subsequent policy
handlers and skips `CoopServerCallbacks.OnEvent(VailHitEvent)`. It is sticky and
returns false outside that dispatch or on another thread. Subscribed context
errors, handler exceptions and off-thread callbacks reject the request. With no
subscribers, normal handling is unchanged. Unsubscribe when the mod shuts down.

Allowing a request preserves the game's target/component lookup and impact
handling; it does not certify that the request is valid. The inspected route is
`TryGetVailActor` to `VailActor.ReceivedImpactFromClient` and `ServerProcessImpact`.
That processing can change health through `ReceivedDamage`/`DamageActor`, add
status effects and stimuli, update combat memory/statistics, trigger knockback,
and apply damage-node/blood effects. Rejection precedes those operations for
this callback. Bolt keeps ownership of event storage; this callback hook does
not release it separately.

The inspected client sends this event reliably to `OnlyServer`, with the actor
entity in the payload Target. Its damage field comes from accumulated impact
damage, and it includes only the first status effect when several are present.
The sender body does not subtract actor health, but upstream damage-node handling
can already change node-local state. The impact path can also apply local ragdoll
effects after sending, without waiting for server confirmation. Rejection does
not undo those changes or guarantee prediction reconciliation.

This is not complete AI invulnerability. Direct server impacts, other event types
and earlier mod/client effects are outside this hook. `DismemberHitEvent` has its
own policy below; rejecting a Vail hit does not reject its paired dismember hit.
It does not reverse predicted effects, refund weapon costs or send a denial
message. Normal and rejected hits, status effects and replicated actor state
still require multiplayer acceptance.

### Server Dismember-Hit Policy

`ServerDismemberHits.Receiving` exposes `DismemberHitEvent` before
the native server callback resolves its target actor and dismemberment controller.
Vanilla clients need no extension. `State.Status` and service key
`openacai.sons.serverDismemberHits` report hook registration, not proof of a
received or blocked request.

The managed request copies the actual source connection/session and event-time
game registration, plus the requested target network ID, damage, part ID and
part position/rotation. Damage is a contribution to the part's dismemberment
accumulator, not an actor-health delta. The part ID is neither a Vail damage-node
ID nor an item ID. The supplied pose may be used to spawn severed parts; it is
not a server-observed transform. Quaternion components are copied without
normalization. No native event, entity or transform is retained.

Treat all payload fields as unvalidated. The loader does not impose damage,
distance, target or permission rules. Session/world reads provide separate
server-visible observations; absent identity is not permission to act.

Policy handlers run synchronously on the game thread. Sticky `Reject()` stops
later handlers and skips `CoopServerCallbacks.OnEvent(DismemberHitEvent)`.
Late or off-thread decisions return false. With subscribers, context errors,
handler exceptions and off-thread callbacks reject the request. With no
subscribers, the hook leaves native handling unchanged. Unsubscribe at shutdown.
Bolt retains event-storage ownership; this callback hook does not release it.

Allowed requests retain the game's actor/controller/part lookup and damage
processing. The inspected receiver calls `ApplyDismemberDamage`, which can
accumulate damage, mark the part dismembered, change active targets and shaders,
spawn instances and invoke a dismemberment callback. Rejection precedes those
operations on this route. It does not establish that other routes are governed.

The inspected client sends reliably to `OnlyServer`. Its damage comes from raw
impact damage, the part multiplier, dismember strength and a critical multiplier,
not the accumulated damage used by Vail hits. It reads a part locator for the
requested world pose. This client branch bypasses local dismember-damage and
severing operations, but has already consumed its last-impact ID. When one
impact produces both requests, this branch queues the dismember hit before the
ordinary Vail hit; that is local call order, not observed network delivery.

Rejecting this event alone does not cancel the separate Vail hit, restore client
bookkeeping or undo ordinary node/ragdoll effects. There is no denial message,
refund or general reconciliation guarantee. Direct server dismemberment and
other event types remain outside this hook. Allowed/rejected hits, part state
and replicated effects still require multiplayer acceptance.

### Optional Client Notices

`ServerNotices` sends short notices to remote players with OpenACAI
installed. The client displays them in the game's HUD message area, separately
from player chat. Server mods can enumerate `Peers` and call `Send(peer, text)`
on the game thread. `Queued` means the native send call returned, not delivery
or display. A retained peer handle becomes invalid on disconnect; do not select
a recipient by a connection ID saved from a previous session.

Server tools can subscribe to `PeerAvailable` and `PeerUnavailable` instead of
polling for capability changes. `PeerAvailable` is queued after the acknowledgement
send returns and the peer is published in `Peers`. It is an opportunity to attempt
a welcome notice, not proof that the client received the acknowledgement.
`PeerUnavailable` is queued when a previously acknowledged peer is retired through
disconnect, stale-session cleanup, server shutdown or hook disablement, or when a
peer first acknowledged without a session repeats the handshake once its session
exists; a new `PeerAvailable` then follows for the same connection. Its pending
tracked notices have already been completed and its token erased before delivery.
A handshake whose acknowledgement send fails is not announced by either event.

Both events run during game-thread maintenance, outside native handshake and
disconnect callbacks. A detached batch preserves observation order; changes made
by a subscriber are delivered on a later tick. Subscriber exceptions are isolated
and logged by type only. Availability is skipped if notice hooks are disabled
when that queued observation is dispatched; retirement can still be delivered.
There is no background dispatcher or guarantee of delivery after game-thread
updates stop.

These are observations, not live connection checks. A handle may already have
been retired when `PeerAvailable` arrives; `Send` and `SendTracked` still revalidate
it. Subscribing does not replay current peers. Use `Peers` for an initial snapshot
and deduplicate by handle identity if a matching event is already queued. Neither
event grants permissions, starts a handshake, sends a notice automatically or
includes a token or native disconnect reason. Vanilla clients remain unaffected.
Handshake, disconnect and subscriber-driven changes still need live acceptance.

`SendToSession(session, text)` selects a peer by its exact connection-lifetime
handle, then uses the same message and live-recipient checks as `Send`. The peer
must already have negotiated notice support; a vanilla or unconfirmed session
returns `PeerUnavailable` without receiving a notice payload. `Session` on each
notice peer records the handle observed at its handshake, when tracking was
available. Bound peers are revalidated by handle even when using the original
`Send(peer, text)` API. Tracking failure makes bound sends unavailable; it does
not create a fresh handshake opportunity on the same connection. A genuinely
new observed session can replace a stale capability at a reused native address.

The original peer-based API remains usable for handshakes made without session
tracking, using its existing native membership checks; `SendToSession` cannot
target those unbound peers. Neither method guarantees that a notice queued before
a disconnect request will be delivered or displayed.

The client sends a capability request per connection using an existing native
chat event addressed only to the server, with a zero sender. Static inspection
of the current vanilla callback shows that it discards this request without
rebroadcasting it. The client accepts notices only after a reply matching its
fresh private token. There is no custom packet sent to an unconfirmed peer, and
the server sends notice payloads only to peers that requested the capability.
Modified servers that rebroadcast otherwise-invalid chat are not covered by
the vanilla compatibility analysis.

Reserved frames are intercepted after Bolt's existing event filter and before
routing. A frame this process sent itself continues to native routing only on a
route that delivers it to another process: a client's frame to the server, or a
server's frame to one connection. Malformed or misdirected remote frames are
consumed rather than passed to chat or relayed. The server holds one handshake
token per live connection, replaced only by the repeats described below; the
token is never included in ordinary chat or application logs. Disconnect clears
the capability, pending HUD notices and client readiness.

The client asks again while no answer has arrived or the answer has no commands,
usually because it came before the player's session existed: up to four more
times, about 3, 9, 21 and 45 seconds after the first request. A host without
Modstoke never answers and receives at most five requests per connection. A host
answers a repeat with the same token again with what it negotiated, up to four
times, and completes command support once the session resolves. A repeat with a
new token on the same connection starts the handshake over, at most twice. A
repeat with the same token only adds command support, and messenger stays as
first negotiated. A host also accepts a repeat that replaces a handshake whose
answer was never sent.

The request uses envelope version 2; every other frame stays at version 1, and
hosts accept requests of either version. Earlier builds dropped their own
outgoing frames, so a host on one of them could never answer, yet it reserved a
capability for any request it read. Those builds reject version 2, which keeps
their players on the native fallback paths.

The client logs when an answer changes command support, the first frame it
ignores, and when it stops asking. The host logs once per connection for each of
ignored, answered and commands-enabled. No log line includes a token, message
text or Steam ID.

If transport hooks are disabled, the next game-thread maintenance pass completes
pending commands and tracked notices as `Unavailable`, erases held capability
tokens, and clears peer state and queued HUD notices. Existing terminal outcomes
are preserved. Cleanup sends no receipts or cancellation messages and does not
re-enable the hooks or start another handshake.

`ClientReady` means the current server echoed the connection's token.
`Received` reports an accepted notice, before HUD display. Neither authenticates
a server operator or an official OpenACAI server. The trust boundary excludes
compromised server/client mods and network eavesdropping; this is not encryption,
client attestation, a permission grant, or an automatic installation protocol.

Messages are single-line plain text, limited to 256 UTF-8 bytes. Empty messages,
control characters, line separators and angle brackets are rejected. The client
keeps at most 16 pending notices, discards the oldest on overflow, and expires
them after two minutes. Once the player and HUD are ready, notices display for
six seconds each. Native transport, disconnect behavior, vanilla coexistence and
HUD presentation still require multiplayer and in-game acceptance.

### Client Signals

`ServerSessions.TryReadClientSignals(session, out snapshot, out status)` copies
what one remote session has said about itself and what it has sent, on the game
thread. Every value is client-controlled. It is evidence for an operator to review,
not proof that a client is vanilla, modded or tampered with, and it is not a reason
to strike, kick or ban on its own. A missing `Hello` means no Modstoke notice
handshake arrived: a vanilla client, an older or failed Modstoke, or a client
hiding it.

`Hello` is the first handshake of the session. Current clients add four optional
claims to it: `LoaderName` ("modstoke"), `LoaderVersion`, and how many native and
legacy (RedLoader and BepInEx) mods `LoadedMods` reports. Mod names are not sent;
the mod list still requires managed admission and the player's opt-in. Older
clients send no claims, and hosts that predate them ignore the extra members, so
mixed versions keep their handshake. Claims are accepted only in a hello and must
be short ASCII values; a malformed hello is rejected like any other bad frame.
`HelloCount` includes the client's automatic retries, and `HelloClaimsChanged`
reports a later hello in the same session that claimed something different.

`Traffic` holds bounded counters and the first few distinct names:

- legacy SonsSdk packets for events this host registered, with their ids, and for
  events no host mod registered, with hashes only (ids never cross the wire);
- notice frames with a bad envelope or body, and well-formed frames of a kind only
  the host sends;
- negotiated client commands naming no registered command, rate-limited commands
  and replayed sequence numbers;
- chat messages that start with a control character but are not Modstoke frames,
  as another loader's chat tunnel would.

`FirstUnexpectedAt` records when the session first sent traffic current Modstoke
clients never produce: malformed or unexpected notice frames, or replayed commands.
Unregistered SDK packets and tunnel chat usually come from mods this host does not
run, including first-party client mods, so they are only counted. A host cannot see
event types it does not know, because they never deserialize, or a client's stream
channel names, which never cross the wire.

`Enrollment` and `Admission` are the existing snapshots, included only while the
session is current and those features are selected. The record itself lives with
the session handle, so a `Closed` handler can still read it; a reconnect is a new
session with a new record. `Revision` increases whenever the hello or traffic
record changes, and `ReadClientSignalsRevision(session)` reads it without a copy,
so a periodic sweep can re-read only changed sessions. Recording adds no hooks
beyond the existing notice, command and SDK packet paths. Live multiplayer
acceptance is still required.

### Optional Notice Feedback

`SendTracked(peer, text, out delivery)` and
`SendTrackedToSession(session, text, out delivery)` add per-notice progress for
peers that explicitly negotiated receipts. Check `peer.SupportsReceipts`; ordinary
notice support alone is insufficient. On the client, `ClientSupportsReceipts`
reports the server's acknowledgement of that capability. The original `Send`
and `SendToSession` remain untracked, including default command replies.
Older clients and servers retain that original path. A tracked send to an older
peer returns `ReceiptsUnsupported` without silently sending an untracked notice.

A successful tracked send returns a managed handle with `Id`, `Peer`, `CreatedAt`
and an immutable `State` snapshot. Read `State.Status`, `ObservedAt` and
`IsTerminal` from any thread; sending still requires the game thread. State is
updated by notice processing and periodic game-thread maintenance, not by a
background timer. The handle retains no message text, token or native objects.

`Queued` means the native send returned. `ClientAccepted` means the client
reported adding the notice to its HUD queue. Terminal client reports are
`ClientHudInvoked`, `ClientExpired`, `ClientQueueEvicted` and `ClientDisplayFailed`.
The normal client sends `ClientHudInvoked` only after `DisplayGeneralMessage`
returns, not after six seconds of visible presentation or a human response.
These remain untrusted client claims, not delivery attestation, proof of
rendering, or evidence that someone read the message. `ObservedAt` is the
server's observation time, not the client's display timestamp.

There are at most 32 pending tracked notices per peer. `PendingLimitReached`
sends nothing. Pending requests expire after 150 seconds during maintenance,
becoming `TimedOut`; observed disconnect or server shutdown produces
`PeerUnavailable`, while unavailable tracking produces `Unavailable`. Timeouts
do not prove non-delivery. Terminal results cannot be revived by late receipts.
Receipt loss can leave a displayed notice marked timed out.

If an exception occurs after reserving the notice, `SendUncertain` returns its
handle for continued observation. The notice may have been queued; neither it
nor its receipts are retried automatically. Other rejected sends return a null
handle. There is no fallback recipient and no tracked notice sent to a vanilla
client. Receipt IDs are correlation values, not credentials.

The handshake advertises and echoes receipt version 1 as an additive field in
the existing version-1 envelope. Receipts travel reliably only to the server,
with the connection token and notice ID. The server requires a current matching
peer/token and a still-pending ID before accepting a report. Reserved remote
frames remain consumed before chat routing, including forged or malformed
frames. Client queue callbacks retain their originating connection/token and
cannot report to a later server session. Disconnect clears queued callbacks
without attempting stale replies.

The HUD queue still holds 16 notices for up to two minutes, with six-second
spacing. Tracked and untracked notices share it; either can evict a tracked
notice. Receipt sending failures do not interrupt HUD processing. Live
negotiation, mixed-version behavior and each receipt outcome remain unverified.

## Public SDK Surfaces

The current public surface already has useful building blocks:

- `Modding`: lifecycle events, snapshots, service registry, startup
  warnings, mod inventory, and runtime paths.
- `GameRuntime`: Sons scene, world, and pause-menu state.
- `GameRuntime.Environment`: OpenACAI-owned Sons runtime/game/path snapshot
  under service key `openacai.sons.environment`.
- `Items`: read-only Sons item metadata catalog under service key
  `openacai.sons.items`.
- `ItemRegistration`: OpenACAI-owned custom item definition
  planning and registration intent under service key
  `openacai.sons.itemRegistration`.
- `Inventory`: Sons LocalPlayer/inventory readiness plus
  OpenACAI-owned inventory action planning under service key
  `openacai.sons.inventory`.
- `Networking`: Sons multiplayer/session readiness plus
  OpenACAI-owned network channel/message planning under service key
  `openacai.sons.networking`.
- `ServerChat`: synchronous server chat policy and hook registration
  state under service key `openacai.sons.serverChat`.
- `ServerAdminRequests`: cancellable native admin-menu requests before
  native permission checks, under service key `openacai.sons.serverAdminRequests`.
- `Commands`: session-bound chat commands with explicit authorization,
  managed input/results and capability-aware optional-client replies.
- `ServerSessions`: connection-lifetime handles for all remote server
  clients, observed lifecycle events, identity/role reads and explicit stored-role changes,
  with tracking and recovery state under service key `openacai.sons.serverSessions`.
- `ServerWorld`: managed, read-only observations of a session's player
  entity or a current-world network entity, with explicit read status.
- `ServerSaves`: explicit requests to the dedicated server's native
  configured autosave queue, without claiming save completion.
- `ClientCommands`: negotiated client submissions to registered server
  commands, with shared policies and authorization plus typed result tracking.
- `ServerTime`: explicit server clock adjustment through the native
  time holder and registered replication publisher, preserving clock locks.
- `EnvironmentReads`: immutable local clock and weather observations,
  including override clocks and native cloud phases, for server and client mods.
- `WorldLocatorReads`: separate registered, recorded and unapplied
  locator-state observations using the native locator key.
- `ServerWeather`: precipitation requests through native cloud and
  weather transitions, with current rain blocking and replication prerequisites.
- `ServerInventory`: session-bound fresh-item grants and single-item
  removal requests over the native client protocol, with explicit send results.
- `ServerWorldObjects`: cancellable native container-open and
  object-break callbacks, with raw target/seed/source context and explicit limits.
- `ServerWorldLocators`: cancellable locator-state dispatch before
  server forwarding and local listeners, with raw and normalized locator keys.
- `ServerItemDrops`: synchronous item-drop policy before the native
  server callback, under service key `openacai.sons.serverItemDrops`.
- `ServerStorage`: structure-storage add/remove policy with separate
  hook states, under service key `openacai.sons.serverStorage`.
- `ServerStructureHits`: cancellable structure-hit requests before
  native server validation and impact processing, under service key
  `openacai.sons.serverStructureHits`.
- `ServerConstruction`: separate blueprint-placement, pre-routing
  ingredient and crafted-structure relocation policies, under service key
  `openacai.sons.serverConstruction`.
- `ServerFreeForm`: free-form placement/animation policy with optional
  native rejection feedback and an independent lifecycle-authority guard, under
  service key `openacai.sons.serverFreeForm`.
- `ServerPlayerHits`: cancellable native player-hit events before
  server routing, under service key `openacai.sons.serverPlayerHits`.
- `ServerVailHits`: cancellable AI-hit requests before native server
  actor lookup and impact processing, under service key `openacai.sons.serverVailHits`.
- `ServerDismemberHits`: cancellable dismemberment requests before
  native actor/part lookup, under service key `openacai.sons.serverDismemberHits`.
- `ServerNotices`: targeted optional-client notices, connection-bound
  capability handles, managed receive events and negotiated per-notice feedback.
- `Building`: OpenACAI-owned blueprint roots, custom blueprint
  registration, and building placement planning under service key
  `openacai.sons.building`.
- `Assets`: OpenACAI-owned per-mod asset/audio paths, discovery,
  and AssetBundle load/unload helpers under service key `openacai.sons.assets`.
- `SaveData`: OpenACAI-owned save-data isolation roots and per-mod
  file helpers under service key `openacai.sons.saveData`.
- `SaveLifecycle`: OpenACAI-owned Sons save/load event state under
  service key `openacai.sons.saveLifecycle`.
- `SaveRegistry`: OpenACAI-owned per-mod sidecar JSON save registry
  under service key `openacai.sons.saveRegistry`.
- `InputBindings`: OpenACAI-owned keybind registration, context gates, and
  loader UI keybind state under service key `openacai.loader.input`.
- `LoaderConfig`: file-backed config reading, simple key/value parsing,
  friendly edits, and saving.
- `UiState`: current UI surface visibility and tooltip state.
- `LoaderRuntime`: compatibility shim health for BepInEx, RedLoader, IL2CPP
  hooks, and SonsSdk compatibility.
- `Timings`: bounded loader/UI timing diagnostics with a typed state
  snapshot, aggregate metrics, process-relative elapsed timing, and recent
  event sequence data.
- `OpenACAIModSources`: source-family constants for Modstoke-native packages
  and legacy RedLoader/BepInEx compatibility entries.
- `ModInventorySnapshot`: derived `Mods`, `Libraries`,
  `OpenAcaiMods`, compatibility, hostable, hosted, and issue views over the
  current inventory.
- `ModInventoryCatalogSnapshot`: compact section counts and entry ids
  under service key `openacai.loader.modInventoryCatalog`.
- `BepInExPluginCompatibility`: Modstoke-owned legacy BepInEx plugin
  discovery and load gate under service key
  `openacai.loader.bepInExPluginCompatibility`. `BepInExPluginHost` loads the
  eligible plugins it selects.
- `ModHost`: OpenACAI-owned native mod host planning and
  manifest/entrypoint validation under service key
  `openacai.loader.nativeModHost`.
- `ModRuntime`: native mod startup results, per-mod status, failures and
  bound lifecycle hooks under service key `openacai.loader.nativeModRuntime`.
  The older `Lifecycle*` planner classes are obsolete views derived from it;
  see Retired Lifecycle Planning below.
- `ServerPolicy`: public protected-runtime/server policy registry under
  service key `openacai.loader.serverPolicy`.
- `FileIntegrity`: installed loader manifest verification under service
  key `openacai.loader.fileIntegrity`.
- `CompatibilityDependencies`: curated RedLoader/SonsSdk dependency
  readiness under service key `openacai.loader.compatibilityDependencies`.
- `RuntimeAttestation`: disclosed unsigned runtime evidence digest under
  service key `openacai.loader.runtimeAttestation`.
- `IConfigProvider`: contributes typed settings that can be edited
  through the native and fallback Modstoke Mods menu.

The SDK provider shape is:

- `IMenuProvider`: contributes a page, panel, or action group to the Mods
  menu. Mod pages are listed under the `Extensions` tab.
- `IConfigProvider`: contributes friendly settings backed by files,
  live compatibility settings, or mod-owned state.
- `ICommandProvider`: contributes safe user actions such as refresh,
  reload, open config, open logs, or server actions.
- `IServerPolicyProvider`: contributes protected-server requirements,
  mod allow-list state, and runtime capability status.

The exact names can change, but the shape should stay provider-based.

## Current Registry Slice

`Modstoke.Loader.MenuRegistry` now exposes the first provider registry:

- service key: `openacai.loader.menu`
- built-in provider metadata for All Mods, OpenACAI, RedLoader, Libraries,
  BepInEx, Issues, Credits, Extensions, Mod Config, Runtime Preferences, Runtime
  Commands, Input Bindings, Protected Runtime, BepInEx Gate, Native Host,
  Sons Environment, Sons Save Data, Sons Assets, Sons Inventory, Sons Items,
  Sons Save Lifecycle, Sons Save Registry, and Sons Networking
- `IMenuProvider` for public provider declarations
- `IMenuContentProvider` for providers that publish structured detail
  panels
- `ICommandProvider` for providers that publish safe command rows
- `MenuProviderDescriptor` for simple registration
- `IMenuProviderPresentation` for optional owning-mod and group hints
- `MenuProviderSnapshot` for state inspection
- `MenuContentSnapshot`, `MenuSectionSnapshot`, and
  `MenuRowSnapshot` for menu-safe provider content
- `MenuRegistry.TryRegisterProvider`, `RegisterOrUpdateProvider`,
  `TryUnregisterProvider`, `TryGetProviderContent`, `NotifyContentChanged`,
  and `Snapshot`

The built-in native and fallback tabs now consume provider metadata for labels
and tooltips. The `Extensions` tab lists built-in settings/status providers and
external providers registered by mods or OpenACAI companion tools. Extension rows
now open provider detail panels in both the native Unity UI path and the IMGUI
fallback path. Providers that only expose metadata receive a default details
page; providers that implement `IMenuContentProvider` can publish their
own sections and rows without referencing Unity UI types.

Public provider registration cannot replace built-in provider ids. OpenACAI
runtime-owned built-ins can use an internal hydration path to replace placeholder
metadata with content while preserving `builtIn: true`. `Protected Runtime` uses
that path to expose structured public loader/protected-server readiness signals
without publishing private admin or anti-cheat implementation details.

Config-backed providers registered through `LoaderConfig` now receive editable
detail pages in both renderers. Boolean settings render as toggles, choices can
cycle through options, and text/numeric values apply through the provider's
`TrySetValue` callback. The renderer never writes provider state directly.
Command-backed providers now receive detail pages in both renderers. Enabled
commands without confirmation gates render `RUN` buttons and invoke through
`MenuRegistry.TryInvokeProviderCommand`; confirmation-gated commands are visible
but not run by the current safe-action panel. The registry itself refuses any
unconfirmed call to a command whose current snapshot requires confirmation.

## Current Input SDK Slice

`Modstoke.Loader.InputBindings` now exposes the first OpenACAI-owned input and
keybind service:

- service key: `openacai.loader.input`
- default loader keybinds for the Mods menu toggle and back action
- `TryRegisterKeybind(...)` overloads for mod-owned keybind registration
- keybind context gates: `any`, `title`, `loading`, `game-scene`, `in-world`,
  and `not-in-world`
- optional suppression while a Modstoke UI surface is visible, set with the
  `suppressWhenModstokeUiVisible` argument and read back from
  `KeybindSnapshot.SuppressWhenModstokeUiVisible`; the old
  `SuppressWhenOpenAcaiUiVisible` property still works but is obsolete
- `IsPressed(id)`, `IsHeld(id)`, and `IsReleased(id)` with the same gate evaluation
  before Unity key-down, held, and key-up polling
- `SetKey(id, key)` changes an existing binding without replacing its metadata;
  `KeyCode.None` leaves it unbound and never polls Unity
- `TryUnregisterKeybind(id)` removes a registration; unknown IDs return false
- `IsContextAllowed(id, out reason)` for diagnostics and UI presentation
- loader state block `openAcaiInput` (schema 2: `modstokeUiVisible` and each
  keybind's `suppressWhenModstokeUiVisible` replace the schema 1
  `openAcaiUiVisible` and `suppressWhenOpenAcaiUiVisible` fields)
- capability state block `capabilities.input`
- built-in `Input Bindings` status provider in the `Extensions` tab

This is the first step toward replacing RedLoader/SonsSdk-shaped input globals
with OpenACAI-owned registration semantics. It does not yet own a full conflict
resolver or rebinding UI; those can build on the published keybind metadata.
Polling does not consume input, emit synthetic transitions, or publish state.
Changes take effect on the next poll. Actual registration, key/enabled changes,
and removal publish metadata and queue a coalesced loader-state refresh; no-op
setters do not. Bindings are process-local: mods own persistence and should use
their own IDs when changing or removing registrations. F6 and Escape remain the
default loader keys.

The built-in runtime preference provider stores the two shortcut enable flags in
`Modstoke/UserData/openacai-loader.settings.cfg`. Startup only reads this file;
missing files use defaults until the first edit, and read failures never trigger
a rewrite. Edits reread current text, preserve unrelated lines, and use the shared
config parser to update all occurrences of the selected key. A complete UTF-8
temporary file is flushed, then moved into place. An optimistic content comparison
rejects changes detected before replacement; this is not a cross-process compare-
and-swap guarantee. Save failures leave live preferences unchanged and return an
error. Key-code changes through `SetKey` are not implicitly persisted here.

## Current Inventory SDK Slice

`Modstoke.Loader.Modding` now exposes inventory helpers that preserve
the public mod/library split:

- `ModInventoryCatalog` and service key `openacai.loader.modInventoryCatalog`
- `GetMods()` for loadable/user-facing mod entries, excluding libraries
- `GetLibraries()` for SDK/runtime helper libraries and support assemblies
- `GetCompatibilityEntries()` for legacy RedLoader/BepInEx compatibility
  mods/plugins, excluding libraries
- `TryFindInventoryEntry()` for id/name/display-name/assembly-name lookup
- derived entry classifiers such as `IsLibrary`, `IsCompatibility`,
  `IsLegacyRedLoader`, and `IsLegacyBepInEx`

This gives future mods, companion tools, and private providers an OpenACAI-owned
way to reason about installed content without scraping the UI or treating
RedLoader libraries as mods.

The same split is now visible as a loader-owned status provider:

- provider id `openacai.menu.runtime.libraries`
- display name `Runtime Libraries`
- boundary rows for mods, libraries, catalog count agreement, hosted mods, and
  Vortex detection
- catalog rows for `Mods`, `Libraries`, compatibility entries, hostable/hosted
  entries, and issues
- library entry rows sourced from `GetLibraries()`
- built-in server-policy row `runtime-library-surface`

Modstoke-native packages can also declare a library role through manifest type
`Library`, `OpenACAILibrary`, `OpenACAI.Library`, or `OpenACAI-Library`. Those
entries use loader type `openacai-library`, appear only in the `Libraries`
inventory/menu section, and are excluded from `All Mods`, `Modstoke Mods`, and
`GetMods()`.

Legacy RedLoader `Libs` assemblies use loader type `redloader-library` and
follow the same rule: they can appear in `Libraries` and runtime-library status,
but not in public mod views, compatibility mod sections, or `GetMods()`.

## Current BepInEx Compatibility SDK Slice

`BepInExPluginHost` executes IL2CPP `BasePlugin` mods at startup and through
the compatibility chainloader's dynamic APIs. It loads each selected assembly
with `Assembly.LoadFrom`, which puts it in the default `AssemblyLoadContext`.
There is no per-plugin or collectible context and no unload. The planning and
attempt surfaces below are diagnostics and never load plugin code.

`Modstoke.Loader.BepInExPluginCompatibility` owns BepInEx plugin discovery and
the load gate the host reads:

- service key `openacai.loader.bepInExPluginCompatibility`
- loader state block `openAcaiBepInExPluginCompatibility`
- capability state block `capabilities.bepInExPluginCompatibility`
- `GetBepInExPluginCompatibilityCandidates()`,
  `GetBepInExPluginCompatibilityPlans()`,
  `GetBepInExPluginCompatibilityLoadAttempts()`,
  `PlanBepInExPluginCompatibility(idOrAssemblyName)` and
  `AttemptBepInExPluginCompatibilityLoad(idOrAssemblyName)` SDK helpers
- PE/metadata inspection for `BepInEx.BepInPlugin` attributes without loading
  plugin assemblies
- metadata-only assembly reference validation through already-loaded
  assemblies and Modstoke resolver roots, with the first unresolved reference
  reported for candidates and parser harnesses
- metadata-only execution plans for IL2CPP `BasePlugin`/`Load()` plugins and
  legacy `BaseUnityPlugin` plugins: plugin type shape, constructor evidence,
  lifecycle or entrypoint readiness, and bounded base-chain traversal through
  resolved dependency DLLs, with the matched base type, chain depth and
  external base assembly that proved it
- static-constructor and module-initializer evidence, and the first
  execution-plan and lifecycle-method issue for each failure
- optional parser-harness evidence from
  `Modstoke/ValidationProbes/BepInExPluginMetadataHarness` and candidate-probe
  evidence from `Modstoke/ValidationProbes/BepInExPluginCandidateHarness`.
  Probe candidates are not mod inventory entries, and the host never loads
  them
- candidate snapshots expose `CandidateSource` and `ValidationProbe`, so SDK
  callers can tell installed candidates from validation probes without path
  heuristics
- each compatibility-ready inventory candidate gets an automatic load plan.
  `PlanSource` is `candidate`, or `lookup` when nothing matched. Planning
  never invokes plugins
- `AttemptBepInExPluginCompatibilityLoad` records a diagnostic attempt with
  `PluginInvoked = false`. An accepted attempt reports reason
  `host-owned-load`, because loading belongs to the host
- plans and attempts carry an `InvocationPreflight` describing what the host
  does for that candidate as ordered stages: assembly load, instance creation,
  and `BasePlugin.Load()` or the `BaseUnityPlugin` lifecycle. Attempts add a
  record per stage
- `InvocationPreflight.ActivationPolicy` records static-constructor and
  module-initializer side effects and a per-candidate allow or flag decision
  for assembly load, construction and `Load()`. It is diagnostic; the host
  does not consult it
- `PluginLoadedByOpenAcaiCount` counts candidates the host has loaded
- libraries, Modstoke-native mods, and RedLoader compatibility mods are counted
  as excluded boundaries and never appear as BepInEx compatibility candidates
- built-in `Modstoke BepInEx Gate` status provider in the `Extensions` tab
- built-in server-policy row
  `openacai-bepinex-plugin-compatibility-gate`. It is ready when there are no
  compatibility, reference, plan, lifecycle or parser-harness issues, the
  library, Modstoke-native and RedLoader boundaries hold, and the host has not
  loaded more plugins than there are load-eligible candidates

Retired surfaces stay compiled for binary compatibility:

- the stage executor and the assembly-load chain it modelled are
  `[Obsolete]` and no longer recorded: `BepInExPluginInvocationExecutorSnapshot`
  with its transition and transition-request snapshots, and the resolver-plan,
  load-operation, load-context plan/allocation/registration, load-invocation,
  type-resolution, instance-creation and plugin-load-invocation snapshots. They
  described an isolated `AssemblyLoadContext` path the host never used. A load
  attempt's `InvocationExecutor` now returns an unavailable executor
- the `Invocation*Enabled` flags are `[Obsolete]` and always `true`
- the `Attempt*Request` stage methods are `[Obsolete]` and forward to
  `AttemptLoad`
- the provider has no `Validate ...` self-test commands; they asserted a
  disabled runtime gate. `BepInExPluginActivationProbe` and
  `openAcaiBepInExPluginActivationProbe` remain, but the menu does not run the
  probe

Parser harness and candidate-probe DLLs must stay under the OpenACAI validation
root; the policy row treats harnesses inside `BepInEx/plugins` as unsafe.
Unresolved candidate or parser-harness assembly references also keep the gate
in `CHECK` until the dependency is supplied by a loaded assembly or a resolver
root. Execution-plan issues, such as an abstract plugin type, missing BepInEx
base type, missing `Load()` entrypoint on an IL2CPP `BasePlugin`, or invalid
lifecycle method shape, also keep the gate in `CHECK` without loading the
plugin assembly.

### Dynamic BepInEx Loading

`IL2CPPChainloader.Instance.LoadPlugins(params string[] directories)` uses owned
metadata discovery, highest process-compatible version selection, dependency
ordering, and failure isolation. Directories are recursive; file paths are not
accepted. All paths are enumerated before any plugin is activated, and invalid
directory errors propagate to the caller. Duplicate directory/assembly paths are
deduplicated. Runtime discovery is not truncated by diagnostic display caps and
does not enumerate validation harnesses or replace the published mod inventory.

Disabled paths, loader runtime/mirror files, validation probes, native OpenACAI
packages, and legacy `Mods`/`Libs` roots remain outside dynamic BepInEx discovery.
DLL-containing directories from the accepted inputs are appended to the existing
resolver roots, subject to its existing exclusions. Readers retain one immutable
root snapshot per lookup; no resolver lock is held while assemblies are loaded.

Batch registration still occurs before construction; `PluginLoad` precedes
`Load()`, and `PluginInfo.Instance` is assigned after success. `PluginLoaded`
observes completed bookkeeping. Its observer failures remain warnings rather than
rolling back an already-running plugin. A nested batch may load a later member
of an outer batch, which then skips that GUID instead of constructing it again.
Registered/attempted GUIDs cannot be replaced or retried; failed activation needs
a restart because user/native side effects are not rolled back. Rejection before
activation does not consume an attempt. There is no unload or hot-replacement API.

The return value contains only successful entries from that invocation, not all
retained plugins or entries loaded by nested calls. Loader state
`openAcaiBepInExPluginHost` includes the latest completed batch's `invocationKind`,
counts/reports, and the cumulative `retainedPlugins` identities, including plugins
loaded outside the standard inventory roots. The inventory itself keeps its
normal directory scope.

Direct `LoadPlugin(PluginInfo, Assembly)` deliberately keeps the upstream low-level
contract: validate type/identity, construct, emit `PluginLoad`, invoke `Load()`,
and return the object. It does not register, assign `PluginInfo.Instance`, retain
the instance in the batch host, enforce batch dependency/process selection, emit
`PluginLoaded`, or run the batch's explicit module-initialization step. Errors
propagate and caller-owned registration/cleanup stays with the caller. This API
is not an at-most-once batch request.

Both APIs execute synchronously on the thread that started Core and reject calls
from other threads, stale registry instances, or a failed Core runtime. They do
not marshal work to Unity. Neither emits `Finished`; Core emits it once on
successful startup. `Initialize` and `Execute` remain unavailable on this adapter
to prevent a second upstream bootstrap. Compilation does not prove dynamic
third-party plugin behavior; ordinary runtime acceptance remains pending.

## Current Native Host SDK Slice

`Modstoke.Loader.ModHost` exposes the first OpenACAI-owned native
package host gate:

- service key `openacai.loader.nativeModHost`
- loader state block `openAcaiNativeModHost`
- capability state block `capabilities.nativeModHost`
- `GetCandidates()` and `PlanHost(modId)` for native host planning
- manifest entrypoint validation for Modstoke-native mod packages
- metadata-only assembly validation through PE/metadata inspection, including
  declared entrypoint type/method presence without loading or invoking mod code
- metadata-only assembly reference/dependency validation through already-loaded
  assemblies and OpenACAI resolver roots before any candidate code can execute
- metadata-only execution-plan validation, including static-method versus
  type-constructor invocation shape, manifest lifecycle hook intent, and
  per-hook method resolution
- startup execution through `ModRuntime` when the owned Unity
  driver is active; planning and refresh calls never execute user code
- built-in `OpenACAI Native Host` status provider in the `Extensions` tab,
  including a Runtime section with each native mod's status and bound hooks
- built-in server-policy row `openacai-native-host-gate`

The native host reads from `Modding.OpenAcaiMods`, so libraries remain
excluded from native mod candidates. Its state also publishes
`LibraryExcludedCount`, matching the inventory library count, to make the
boundary verifiable by policy and companion tools.

Native candidates are hostable only when their assembly metadata can be read,
their referenced assemblies resolve through the OpenACAI dependency surface, and
their execution plan is valid. Static-method plans require a public static
zero-argument method. Type-constructor plans require a public entrypoint type
with a public parameterless constructor. Manifest lifecycle hook declarations can
map to explicit hook methods through `lifecycleMethods`, `hookMethods`,
`onInitialize`, `onUpdate`, `onShutdown`, or per-hook `*Method`/`*Hook` aliases;
the native host accepts these declarations as strings, JSON arrays, or JSON
objects so mods can use either compact legacy-style values or structured
OpenACAI manifests.
When no explicit hook method is declared, static-method plans default each hook
to the entry method. Hook methods must resolve in PE metadata as public static
zero-argument methods before a native candidate is hostable. Metadata eligibility
is not evidence that a package has loaded; consult the runtime results below.
Bad images, unresolved references, missing entrypoint types, missing methods,
non-public/non-static methods, methods requiring parameters, and invalid
constructors are reported as validation issues. Manifest-declared assembly paths
are resolved only inside the native
package root, so a package cannot point the host at arbitrary runtime or game
assemblies.

### Native Startup Execution

`ModRuntime.State` (also `Modding.ModRuntime`) reports
actual outcomes under service key `openacai.loader.nativeModRuntime` and loader
state block `openAcaiNativeModRuntime`. Core loads the startup inventory once,
before `LoaderReady`. Packages added or changed later require a game restart.
`PlanHost().CanLoadNow` remains false: the planning API cannot load or reload mods.

- Only enabled, non-library packages declaring an entrypoint are executed.
  Duplicate enabled IDs or assembly paths are rejected. Diagnostic candidate
  display limits do not truncate the runtime's ownership list.
- The runtime binds public static, non-generic, synchronous `void` methods with
  no parameters. Async-void, non-void, ambiguous overloads, and open generic or
  non-public entry types are rejected. Constructor entrypoints require a concrete
  class with a public parameterless constructor; one instance is retained for the
  process. Lifecycle methods remain static even for constructor entrypoints.
- Assembly path and module identity must match the selected DLL. This uses the
  shared resolver and load context, not isolated contexts or a sandbox. Assembly
  initializers and native/user side effects are not transactionally reversible.
- After the entrypoint, startup hooks run in order: `initialize` (`init`), `awake`,
  `load`, `enable` (`onenable`). An initialize mapping to the static entry method
  is not called twice. Other hook mappings are separate events. Aliases for the
  same hook bind once; conflicting mappings are rejected.
- The existing Unity driver delivers `start` once, then `fixedupdate`, `update`,
  `lateupdate`, and `gui` (`ongui`). Shutdown visits mods in reverse load order,
  invoking `disable` (`ondisable`), `shutdown`, and `dispose` independently.
  Enable/disable describe process startup/shutdown, not dynamic toggles. No
  automatic `IDisposable` call is added beyond the declared static dispose hook.
- A startup or driver callback failure stops further owned frame callbacks for
  that package, logs the error, and leaves other packages running. Cleanup hooks
  are attempted if its entrypoint completed, including after a startup-hook
  failure. Manual subscriptions and partial user side effects are not rolled back.
- Unknown hook names are rejected. Payload-bearing scene, world, pause, and save
  events use the existing typed Modstoke SDK subscriptions; they are not inferred
  from manifest names. Startup does not imply game-world or asynchronous SDK
  initialization is complete.
- Legacy native Unity-support mode remains deferred because it does not create
  the owned driver. No duplicate driver or compatibility callback relay is added.

`Loaded` means the entrypoint and startup hooks completed, even if a later callback
fails. `Status`, `Failed`, and `Stopped` distinguish later outcomes. The independent
policy row `openacai-native-runtime` checks real failures and deferred packages.
Each `ModRuntimeEntry.Hooks` lists the hooks bound at startup (canonical
lowercase name and method name, in dispatch order), and `HookCount` totals
them. Native game acceptance remains a separate check from compilation or a
successful metadata plan.

### Retired Lifecycle Planning

`LifecycleRegistry`, `LifecycleDispatchPlanner`, `LifecycleInvocationPlanner`,
`LifecycleBindings`, `LifecycleTokenResolver`, `LifecycleJournal`,
`LifecycleSchedulePlanner`, and `LifecycleExecutionPlanner` used to restate
native-host metadata as a chain of dispatch, binding, token, journal, schedule,
and execution plans that never invoked anything. They are now `[Obsolete]`.
Use `ModRuntime.State` instead.

For one release the classes, their snapshot types, and the matching
`Modding.Lifecycle*`, `Modding.GetLifecycle*`, and `Modding.PlanLifecycle*`
members still work. Each is a read-only view built from `ModRuntime.State`:
one entry per bound hook, `READY` while the mod is active, `CHECK` once it has
failed, and `INFO` otherwise. Gate and eligibility flags follow the mod's
`Active` state, and `RuntimeInvocationEnabled` follows
`ModRuntime.ExecutionEnabled`. Planning-only fields such as entrypoint names,
method sources, plan request counters, and `LibraryExcludedCount` read as
empty or zero. Binding tokens take the form `modId:hook`.

These views are no longer published under their `openacai.loader.nativeLifecycle*`
service keys, loader state blocks, or capability blocks. Their status providers
and `openacai-native-lifecycle-*` policy rows are also gone, and nothing
refreshes the views when loader state is written. The Native Host status
provider's Runtime section shows each mod's status and bound hooks.

## Current Sons Runtime State, Environment, And Save Data SDK Slice

`Modstoke.Sons.GameRuntime.State`,
`Modstoke.Sons.GameRuntime.Environment`, and
`Modstoke.Sons.SaveData` expose OpenACAI-owned runtime path and
save-data isolation state:

- service keys: `openacai.sons.state`, `openacai.sons.environment`, and
  `openacai.sons.saveData`
- loader state blocks `openAcaiSons`, `openAcaiSonsEnvironment`, and
  `openAcaiSonsSaveData`
- current scene/world/pause state, scene kind classification, lifecycle event
  count, scene event count, world event count, pause event count, and last
  lifecycle event name
- game executable, Unity, persistent data, Endnight save data, Modstoke runtime,
  Modstoke Mods, Modstoke UserData, and legacy compatibility paths
- safe per-mod save-data directories under
  `Modstoke/UserData/Saves/Mods/{normalized-mod-id}`
- rooted relative-file helpers that reject absolute paths and path traversal
- built-in `Sons Runtime State`, `Sons Environment`, and `Sons Save Data`
  status providers in the `Extensions` tab
- optional server-policy rows `sons-runtime-state`, `sons-environment`, and
  `sons-save-data`

These surfaces are intentionally OpenACAI-owned runtime APIs. RedLoader
`LoaderEnvironment` paths and SonsSdk lifecycle events can still be mirrored for
compatibility, but new mods should consume the OpenACAI services directly.

## Current Sons Asset SDK Slice

`Modstoke.Sons.Assets` exposes the OpenACAI-owned asset and audio
service:

- service key: `openacai.sons.assets`
- `State`, `Ready`, and `Refresh()`
- safe per-mod asset directories under
  `Modstoke/UserData/Assets/Mods/{normalized-mod-id}`
- safe per-mod audio directories under
  `Modstoke/UserData/Audio/Mods/{normalized-mod-id}`
- rooted file path helpers for asset and audio files
- bounded file listing helpers for mod-owned asset/audio roots
- direct file lookup helpers for mod-owned asset/audio files
- safe AssetBundle load/unload helpers through Unity `AssetBundle.LoadFromFile`
- loaded AssetBundle snapshots and load/unload counters in runtime state
- non-blocking AudioClip load/unload helpers through Unity
  `UnityWebRequestMultimedia.GetAudioClip`
- pending and loaded AudioClip snapshots plus audio load/unload counters in
  runtime state
- loader state block `openAcaiSonsAssets`
- capability state block `capabilities.sonsAssets`
- built-in `Sons Assets` status provider in the `Extensions` tab
- optional server-policy rows `sons-asset-paths`, `sons-asset-loading`, and
  `sons-audio-loading`

AssetBundle and AudioClip loading are available through the OpenACAI service
and remain explicitly per-mod/path-safe. FMOD banks use the separate owned
`AudioBanks` service described below.

Use the Unity lifecycle thread for runtime asset operations; registry locks do
not make Unity calls background-thread safe. Each pending audio request is
polled, completed and disposed independently. A polling or disposal exception
records a failure without abandoning other requests. A request that fails to
start is also disposed. Cancellation detaches the request before cleanup so it
cannot later publish a clip; disposal is best-effort, with no automatic retry or
guarantee of native cleanup after a native exception.

Loaded bundle and clip registrations are removed only after Unity accepts their
unload/destroy call (or the object is already gone). A failed call retains the
registration for inspection or an explicit retry. Bulk unload continues after
individual failures and publishes its result even when nothing was released.
Unload counts describe accepted operations, not immediate native destruction;
Unity schedules AudioClip destruction. Canceling only a pending clip retains the
existing `TryUnloadAudioClip` false result and explanatory status, and does not
increment the loaded-clip unload count. Clip snapshots use metadata captured on
successful load, so inspecting state does not query damaged or destroyed clips.
These source/build contracts still require runtime acceptance.

### FMOD Bank Metadata

`Modstoke.Sons.AudioBanks` provides synchronous file-based bank
metadata loading, event enumeration and explicit unloading without RedLoader or
SonsSdk globals. Its service key is `openacai.sons.audioBanks`; the loader state
contains `openAcaiSonsAudioBanks`. No bank is loaded merely by reading state.

- `TryLoadBank(modId, relativePath, stringsRelativePath, out snapshot, out status)`
  resolves both files under the mod's existing Audio directory. Pass null for no
  strings bank; there is no guessed filename. Both files must exist before any
  native load. The call uses `LOAD_BANK_FLAGS.NORMAL`, not asynchronous loading.
- Successful loads retain both handles. Registrations are idempotent for the
  same canonical content/strings paths. Shared resources already owned by this
  service are reference-counted by canonical path. Any non-OK native load result,
  including already-loaded, fails without adopting the returned handle. Banks
  loaded by the game or other loaders are never borrowed or unloaded here.
- Failed partial loads attempt to release each acquired resource independently.
  Failed cleanup remains in `State.Banks`, with the retained ownership flags and
  error visible. Retry cleanup with `TryUnloadBank`; loads do not silently replace
  a failed registration. Native exceptions cannot guarantee rollback of native
  side effects that were not returned to managed code.
- `TryGetEvents` returns immutable GUID/path metadata, not native event handles.
  A missing string lookup leaves the path empty with `PathStatus` set to
  `ERR_EVENT_NOTFOUND`; other errors fail the query. This query does not play
  events or preload samples. Bank metadata loaded is not evidence of audibility.
- `TryUnloadBank` releases the selected registration. `UnloadModBanks` attempts
  every registration for that normalized mod ID and returns the number fully
  released; failures are retained and reported in state. The last owned reference
  triggers native unload. Shared references do not unload each other's resource.
- Operations require the captured Core startup/Unity thread and reject reentry.
  The game's existing initialized, valid Studio system is required. This service
  never creates, updates, flushes, releases or replaces that system. Resources
  from a different system handle are not passed to native unload.
- The owned driver attempts cleanup after mod quit callbacks; Core startup
  failure also attempts cleanup. Native shutdown ordering can prevent release,
  which is reported. No scene-wide unload, automatic retry, hot reload, memory
  loading is implemented in this slice. Sample preloading and playback are
  described separately below.

`State` is a thread-readable immutable snapshot of the last operation, not a
continuous FMOD liveness poll. `Initialized` means the service is initialized,
not that FMOD is ready. `Loaded` is the last checked metadata state; operations
revalidate handles. The game or another mod can still invalidate shared native
state externally. State publication uses the existing coalesced loader refresh.
File paths use the existing lexical mod-directory rules, not a filesystem sandbox.

The installed `FMOD.Unity.dll` wrappers supply the typed signatures. Native
semantics follow the official [Studio System documentation](https://www.fmod.com/docs/api/content/generated/FMOD_Studio_System_Create.html)
for blocking metadata loads and [Bank documentation](https://www.fmod.com/docs/2.02/api/studio-api-bank.html#studio_bank_unload)
for invalidation during unload. Source inspection and compilation do not replace
runtime load, shared-strings, failure-cleanup or playback acceptance.

### Bank Sample Preloading

- `TryLoadBankSamples(modId, relativePath, out status)` requests non-streaming
  samples for a loaded content bank. Each registration owns at most one accepted
  request; repeated calls are idempotent, not additional native acquisitions.
  A successful request is not a readiness result and does not start playback.
- `TryGetBankSampleState(modId, relativePath, out snapshot, out status)` queries
  FMOD explicitly and returns an immutable bank snapshot. A successful query can
  report UNLOADING, UNLOADED, LOADING, LOADED or ERROR. Only LOADED reports sample
  readiness at that instant, not audibility or streaming-file readiness. ERROR
  is a reported native state, not a failed query or an automatic retry trigger.
- `TryUnloadBankSamples(modId, relativePath, out status)` balances only this
  registration's accepted request. Repeated releases are no-ops; a failed native
  release retains ownership for explicit retry. This does not stop playback or
  guarantee immediate memory reclamation when other references remain.
- Bank unload stops retained playback, releases the sample request, then releases
  bank references. Failed sample release retains both bank references for retry.
  Invalid handles can be forgotten without calling native unload; changed system
  identities are rejected. Sample operations use the existing thread/reentry
  guards and never wait, poll automatically or flush the game system.

`SamplesRequested` tracks the registration's accepted, unreleased preload request.
`LastSampleLoadingState` starts UNKNOWN and resets before a query or native sample
load/release attempt; it is not a continuous readiness promise. Reading `State`
never calls FMOD. The queried state is bank-wide, not per-registration residency;
FMOD can report UNLOADED when samples were loaded only through event APIs. Strings
banks are not implicitly preloaded. These calls remain unverified in-game.

Reference balancing and bank-level state semantics follow the official
[Bank sample-data documentation](https://www.fmod.com/docs/2.02/api/studio-api-bank.html#studio_bank_loadsampledata).

### Owned Event Playback

The same `AudioBanks` service now owns Studio event instances. It
looks up the requested GUID within the selected owned bank, rather than resolving
arbitrary global event names. The bank must already be loaded. No native event
instance is exposed to callers.

- `TryPlayOneShot` accepts only events whose `isOneshot` query succeeds and is
  true. It configures and starts a new instance, then submits release to FMOD.
  Successful instances are no longer retained by the managed service; native
  destruction follows FMOD's stopped-state/asynchronous-update lifecycle.
- `TryStartEvent` returns a process-local playback ID for a retained instance,
  including looping events. Callers must eventually call `TryStopEvent` even if
  the event finishes naturally. There is no automatic completion polling, replay
  or restart on the same ID. New starts allocate new IDs.
- Both entrypoints take `AudioPlaybackOptions` (null means defaults):
  finite nonnegative Volume, finite positive Pitch, and optional Attributes.
  A 3D event requires explicit `AudioAttributes` before creation.
  Position and Velocity must be finite; Forward and Up must be perpendicular unit
  vectors. Defaults are forward +Z/up +Y, zero velocity. Coordinates pass through
  to the game's existing FMOD setup; the service does not move listeners.
- Retained instances support `TrySetEventVolume`, `TrySetEventPitch`,
  `TrySetEventParameter` (with explicit ignoreSeekSpeed), and
  `TrySetEventAttributes`. `TryGetPlayback` explicitly queries native playback
  state. All commands require the same normalized mod ID, owner thread and
  current system identity. Mod IDs express ownership, not a security boundary.
- `TryStopEvent` accepts an allowFadeout flag and independently attempts stop and
  release. Successful release is remembered if stopping fails, so retry does not
  submit release twice. Failed cleanup stays in `State.Playbacks`; a failed start
  returns a nonzero playback ID only when cleanup remains pending. A failed
  one-shot names its cleanup ID in status. Invalid instances can be discarded by
  an explicit stop without another native release call.
- Bank unload first stops/releases all instances belonging to that registration.
  Any failed instance cleanup keeps the bank references retained for retry and
  blocks new starts there. Other registrations still get their cleanup attempt.
  Already-released one-shots/fadeouts are under FMOD's lifetime; explicitly
  unloading their bank can terminate them.

State snapshots describe managed ownership and accepted commands, not audibility.
`StartSubmitted`/`ReleaseSubmitted` mean the native call returned OK;
`LastPlaybackState` is UNKNOWN until explicitly queried. Caller-owned instances
remain listed until stopped, bank unload or shutdown. No per-frame state writes
or event callbacks were introduced. Actual playback, 3D placement, fadeout and
cleanup behavior remain unverified in-game.

Native release timing follows the official [EventInstance documentation](https://www.fmod.com/docs/2.02/api/studio-api-eventinstance.html#studio_eventinstance_release).

## Current Sons Item SDK Slice

`Modstoke.Sons.Items` exposes the first OpenACAI-owned item
metadata service:

- service key: `openacai.sons.items`
- `Catalog`, `Items`, `Ready`, `Refresh()`, `TryFindItem(id, out item)`,
  `TryFindItem(nameOrTitle, out item)`, `SearchItems(...)`, and
  `FindItems(ItemQuery)`
- bounded read-only item snapshots with id, name, editor name, item type, UI
  title/description/translation keys, max amount/pickup caps, spawn/hotkey/
  quick-select flags, prefab presence flags, and tags
- query fields for id/name/title/translation lookup plus type, tag, spawnable,
  hotkeyable, quick-select, perishable, and prefab filters
- loader state block `openAcaiSonsItems`
- capability state block `capabilities.sonsItems`
- built-in `Sons Items` status provider in the `Extensions` tab

Catalog capture and uncached numeric-ID lookup use the installed typed Sons.Item
bindings on the initialized Unity lifecycle thread. Off-thread or pre-initialize
`Refresh()` returns a `CHECK` result without replacing the published catalog;
an uncached `TryFindItem(id, out item)` returns false without native access.
Published catalog searches remain managed-only, and their results can be stale
until the next successful refresh. A false lookup is not proof that an item ID
does not exist outside the bounded catalog.

Capture retains the existing source order: `ItemDatabaseManager.Items`, cache
values, then the database's item list. If all are null, it calls the existing
no-argument `Initialize()` once and rereads. Metadata snapshots retain no native
objects. Item and tag traversal uses IL2CPP enumerators with native disposal,
bounded to 512 items and 12 raw tags per item. Stored UI text fields are copied,
not replaced with localized method results. Destroyed prefab objects do not
count as present. Native read failures report an unavailable catalog or failed
lookup instead of silently substituting default field values.

This slice does not register, mutate, craft, spawn, or patch item definitions.
The native initializer/getter side effects and actual catalog output still need
in-game acceptance; wrapper metadata does not prove that those calls are pure
or safe at every lifecycle stage. RedLoader/SonsSdk item-registration behavior
remains a compatibility reference for the later OpenACAI-owned mutation API.

## Current Sons Item Registration SDK Slice

`Modstoke.Sons.ItemRegistration` exposes the OpenACAI-owned custom
item definition planning and registration-intent service:

- service key: `openacai.sons.itemRegistration`
- `State`, `Ready`, `Refresh()`, `PlanningEnabled`,
  `RuntimeItemRegistrationEnabled`, `SupportedDefinitionKinds`, and
  `SupportedAssetReferenceKinds`
- custom item definition plans through `PlanCustomItemRegistration`,
  `PlanBaseItemClone`, and `PlanItemRegistration`
- in-memory OpenACAI definition registry through `TryRegisterItemDefinition`,
  `TryUnregisterItemDefinition`, and `GetRegisteredItemDefinitions`
- validation for mod ownership, normalized item keys, display-name bounds,
  item-id collisions against the Sons item catalog, base-item existence, tags,
  and declared asset-reference kinds
- explicit runtime registration status in every item-registration plan
- loader state block `openAcaiSonsItemRegistration`
- capability state block `capabilities.sonsItemRegistration`
- built-in `Sons Item Registration` status provider in the `Extensions` tab
- optional server-policy row `sons-item-registration-gate`

Declaration registration remains non-mutating. Explicit `ExecuteItemRegistration`
publishes authored native items or supported simple base clones to the native
database, with ID checks and publication readback. This does not author layouts,
register arbitrary network prefabs or establish multiplayer acceptance. See
[native runtime actions](SOTF_RUNTIME_ACTIONS.md) for execution and asset ownership.

### Items with their own model

`ExecuteItemRegistration(CustomItemDefinition, ItemModel)` authors a counted item
from a mod's model rather than from game data:

- `ItemModel.Model` (and optionally `PickupModel`) return a template the mod keeps
  alive, such as a prefab from its asset bundle. The SDK instantiates inert copies:
  scripts, rigidbodies, joints and colliders are stripped and hit boxes are fitted
  to the meshes.
- The item shows on the inventory's mod gear page (below), rotated by
  `MatRotation` and scaled by `MatScale`. The SDK picks its place from
  `MatCategory` and the model's footprint as it lies; `MatOffset` moves the model
  from that place and is usually just a lift. Unset, the category is the weapon
  parts for items that attach or combine and Other for the rest. The SDK rebuilds
  the layout for each inventory lifetime and follows stock.
- A spawnable definition gets a world pickup built from a plain native pickup,
  with the mod model in place of the donor's renderers. For networked ids the
  pickup keeps the donor's Bolt entity under prefab id `33000 + itemId - 1000`,
  registered on every peer that registers the item, so hosted pickups reach
  clients that run the same mod. Client-local ids get local pickups.
- `ItemModel.MatAction` sets the left click on the mat. `Drop` (the default,
  spawnable items only) closes the inventory and drops one in front of the
  player, through the native drop or, when that refuses, a removal and spawn;
  the game only drops held items, and modeled items cannot be held. `Attach`
  attaches one to a carried weapon through `WeaponAttachments`. Combine is
  hidden until modeled items can take part in recipes. Host and singleplayer only.

### The mod gear page

The game's roll bag has a place for each of its own items and no room left, so
mod items lie on a page of their own: the same bag with the game's items put
away. The game's inventory mode key (X, shown as Toggle Mode) steps through the
pages:

1. the game's items;
2. mod gear, while the player carries at least one item that lies there;
3. the game's building materials, while creative mode is on.

Creative mode is a game setting that can change during play, for example when
an admin switches it, so the third page comes and goes with it; the game reads
it each time the inventory opens. The inventory opens on the first page, or on
the building page if the player closed it there and creative mode is still on.
The game itself remembers that page whatever the setting and only lets a player
leave it while creative mode is on, so the loader forgets the page when the
inventory opens with creative mode off.
The crafting mat keeps its contents between the first two pages, which is how a
mod item combines with one of the game's. Entering or leaving the building page
clears it, as the game always did.

Places on the page follow `MatCategory` (devices left of the crafting mat,
weapon parts right of it, tools above it, consumables and the rest further
out), then item id. Items go into the bag's own pockets, the fabric between its
zip strips and dividers, packed in rows from each item's footprint and centred
in the pocket. In the long columns an item lies lengthwise, its right-hand end
away from the player, as the game lays its axes and spears; elsewhere it is
turned only when it is too wide to lie across. A tall modeled item is moved a
little towards the camera's resting point, so that it looks centred rather than
leaning onto the strip behind it. The turn is the page's alone: on the crafting
mat a combinable item lies as `MatRotation` sets it. Every registered item keeps
its place whether or not the player carries one, so a set of mods always lays
out the same way. A category that runs out of pockets spills onto the bag's
flaps.

Modeled items get their place from `ItemModel`. A mod that builds its own layout
calls `Inventory.TryCreateGearLayout(definition, category, footprint, ...)`
instead of `TryCreateLayout` with an anchor: the SDK owns the layout root's
position and turn, and the mod lifts and turns its visuals on the row.
`TryCreateLayout` still places a group beside one of the game's on the first
page.

The page adds no bindings of its own. It steps on the game's inventory mode
action and shows the game's prompt for it, and items take hover, clicks and the
crafting mat through the game's inventory actions, so a controller and a
player's rebinds work as they do for the game's items. A controller's cursor is
drawn to a snap anchor on the item under it; the SDK puts one at the middle of
each gear place. The cursor takes it while it is over the part of the item that
covers that middle, which for an item built from several colliders is not all
of it.

The game greys out items that do not fit the recipe on the crafting mat with a
shader that draws the base map alone. A colour kept in `_BaseColor` over a pale
map would grey to white, so the SDK gives the greyed copy of such a material
the colour as its map. This covers every layout made through the SDK.

### Mod content agreement

Processes that reserve networked item ids (`ItemIds.TryReserve`, Networked band)
check at join that client and server declare the same reservations. The check
rides on client enrollment (capability `1 << 61`): the server sends a SHA-256
digest of its sorted reservations with a short summary, and the client answers
with its own.

- A server with content refuses clients whose content differs. By default it
  also refuses clients that never take part, such as vanilla clients, by
  holding their packets until the check passes. The disconnect notice names
  the content the server needs.
- `allowVanillaClients=true` in `Modstoke/UserData/openacai-loader.settings.cfg`
  lets clients that don't take part join; Modstoke clients with content are
  still checked and refused when they differ.
- Clients with content log the server's summary next to their own when they
  differ. Processes without reservations are unaffected.

### Weapon attachments

`WeaponAttachments.TryRegister(WeaponAttachmentSpec)` turns a registered item into
a native weapon mod for one weapon item, on a slot (`OnTopRail` by default):

- The mod hash is the first four bytes (little-endian) of
  SHA-256(`modstoke.weaponmod/1\n` + mod id + `\n` + key). A collision is refused.
  Every peer, including dedicated servers, must register the same specs, since
  held-weapon replication and saves carry only the hash.
- `Model` returns a template; the game instantiates it on every weapon that
  carries the mod, at `Offset`/`Rotation` from the slot locator. `Hides` names
  weapon parts to hide while attached, such as the rifle's built-in red dot
  (`RifleScopeModel`).
- `OnAttached`/`OnDetached` run for local and remote weapon objects, and
  `OnDetached` also runs when the carrying weapon object is destroyed.
- `TryAttach(itemId)` moves one item from the inventory onto the held, or else
  first carried, weapon and returns any attachment it replaces. `TryDetach`
  returns it. Both are host and singleplayer only and refuse under strict
  inventory. A held weapon shows the change at once; other players see it
  when it is next equipped.
- The definition must be a `CustomItem` without hotkeys or asset references. The
  item type defaults to `CraftingMaterial, Droppable`; equipment, weapon and
  ammunition types are refused until held models are supported.

### Crafting recipes, loot and 3D prints

These put registered mod items into the game's own ways of getting things. Each
one changes a list that every peer must hold identically, so mods register at
start in every process, clients and dedicated servers alike, and the Loader
applies the additions once a world is loading, in a fixed order.

- `CraftingRecipeRegistration.TryRegister(CraftRecipeSpec)` adds a crafting mat
  recipe that combines ingredients (1 to 8 kinds, counts 1 to 99) into 1 to 4 of
  a result the mod reserved. It copies one of the game's craft-new-item recipes
  for its sounds and completes at once. `AnimationFromItemId` borrows the craft
  animation of the recipe that makes that item instead; the animation moves that
  recipe's own parts, so use it only for the same ingredients. Strict inventory
  checks a craft against the server's list by position and content, so every
  crafting recipe database in the process gets the same additions after the
  game's own: weapon attachment recipes first, then mod recipes sorted by mod id
  and key. Dedicated servers get them too.
- The mat makes the first recipe whose ingredient kinds are exactly the ones
  lying on it, with at least each count, and only while every result fits the
  inventory: a player who already carries the most of an item they may can't
  make another, and the recipe isn't listed under the cog. A mod recipe with the
  same kinds as an earlier one never wins, and the Loader logs a warning for it.
- Combinable modeled items lie on the crafting mat at the game's weapon mod
  spots, the next spot for each item registered, so the parts of a recipe lie
  apart.
- `LootTables.TryAdd(modId, table, itemId, weight, out problem, maxCount)` adds
  an item to one of the game's container loot tables, such as `AmmoCase` (the
  colored weapon cases in helicopter crash sites and caves) or
  `BasicStorageCrateItems` (storage crates, the game's tech loot). The game's own
  entries weigh 1 to 300; `maxCount` caps how many one container may roll.
  Containers already opened in a save keep what they rolled.
- `PrinterBlueprints.TryRegister(modId, key, itemId, name, resin, seconds, out
  problem)` adds a 3D printer blueprint that prints one item from 1 to 2000
  resin. Its id comes from the mod id and key, so a save printing one finds it
  again; the laptop shows the item's localized name.
- All three need the item to have a world pickup, which modeled items registered
  with `canBeSpawned` get.
- `content-catalog-next-launch.flag` in `Modstoke/UserData` makes the Loader write
  `content-catalog-<time>.json` 20 seconds into the next world: every loot table
  with its weighted items and the containers that use it, and every printer
  blueprint.

## Current Sons Inventory SDK Slice

`Modstoke.Sons.Inventory` exposes the OpenACAI-owned inventory
readiness, live-query and action-planning service:

- service key: `openacai.sons.inventory`
- `State`, `Ready`, and `Refresh()`
- read-only LocalPlayer/world/inventory readiness state
- item-catalog backing status for future inventory-aware helpers
- `TryGetItemCount(int, out int, out string, bool includeEquipped = true,
  bool checkNextSlot = false)` forwards PlayerInventory.AmountOf options
- `TryHasItem(int, out bool, out string)` forwards the game's Owns predicate
- `TryIsItemEquipped(int, out bool, out string)` forwards TryGetEquipped without
  returning or retaining the native item instance
- inventory action planning helpers:
  `PlanAddItem`, `PlanRemoveItem`, `PlanEquipItem`, `PlanSpawnItem`,
  `PlanCraftResult`, `PlanRegisterItem`, `PlanReplaceInventoryModel`, and
  generic `PlanAction`
- explicit runtime mutation status in every action plan
- action plan counters and last-plan reason/status in runtime state
- loader state block `openAcaiSonsInventory`
- capability state block `capabilities.sonsInventory`
- built-in `Sons Inventory` status provider in the `Extensions` tab
- optional server-policy rows `sons-inventory-readiness` and
  `sons-inventory-action-gate`

Plans validate intent without mutating inventory. Separate `TryAddItem`,
`TryRemoveItem`, `TryEquipItem`, `TrySpawnItem` and `TryReplaceInventoryModel`
APIs perform explicit native actions with fresh context checks and honest
partial-result reporting. At the title screen inventory readiness remains
`PENDING`. See [native runtime actions](SOTF_RUNTIME_ACTIONS.md); a successful
plan is neither authorization nor evidence that an action ran.

Live queries require a positive item ID, the initialized Unity lifecycle thread,
and a currently in-world LocalPlayer with a live inventory. They reacquire that
inventory on every call rather than trusting the readiness snapshot or caching
a native object. False means unavailable/failed and includes a status message;
true means the query completed, even when count=0 or the returned predicate is
false. Positive IDs are forwarded without a catalog-membership check. Counts,
Owns and equipped results remain distinct native contracts, not inferred from
one another or from private storage fields. Reads do not publish status, scan
the full inventory, log, or enable mutation. Separate calls are not a combined
atomic snapshot. Bindings are statically verified; actual game results and
native query behavior still require in-world acceptance.

### Inventory Notifications

`InventoryEvents.Observed` exposes native pickup, crafting,
consumption and armor-equipped notifications without a SonsSdk or RedLoader API
dependency. The loader registers this source independently of legacy SDK setup.
Subscribers receive an immutable `InventoryActivity` containing:

- `Kind`: one of `PickedUp`, `Crafted`, `Consumed` or `ArmorEquipped`.
- `ItemId`: the reported ID for pickup, crafting and consumption; null for armor.
- `ReportedCount`: the raw pickup payload count; null for the other kinds.
- `ObservedAt`: the local wall-clock time when the payload was copied.

IDs and counts are not catalog-validated or inferred from inventory changes.
These are process-local event observations, not confirmed inventory deltas,
unique-item identities, remote-player attribution or server-authoritative action
receipts. There is no cancellation, mutation, network forwarding or replay.
In particular, a dedicated server must not use this stream as a complete record
of vanilla clients' inventory activity. Client-local notifications require code
running on that client.

Delivery is synchronous on the native publisher's thread; the SDK does not move
it to the game thread. Each subscriber is isolated from exceptions in earlier
OpenACAI subscribers. Retaining the snapshot retains no native item or delegate
payload. Existing game-thread requirements still apply to other SDK operations
called by a subscriber.

`RegisteredKinds` is a flags value describing registration calls that returned
successfully. Registration is attempted once per process and independently for
each kind; partial failure does not disable successful kinds or retry uncertain
registrations. Native delegates remain rooted for the process lifetime. Normal
native scene clearing preserves the core registry used here, but the SDK does
not repair another mod replacing that registry or removing its callbacks.

Legacy event subscriptions, original payloads and SDK setup timing are unchanged.
The OpenACAI stream does not invoke the legacy events, and no relative ordering
between the two streams is promised. Missing or incompatible item payloads are
not published as valid item observations. Gameplay delivery, repeat-session
behavior and third-party compatibility remain unverified in-game.

## Current Sons Crafting SDK Slice

`Modstoke.Sons.Crafting` exposes the OpenACAI-owned crafting
planning and recipe metadata service:

- service key: `openacai.sons.crafting`
- `State`, `Ready`, `Refresh()`, `PlanningEnabled`,
  `RuntimeExecutionEnabled`, and `SupportedPlanKinds`
- crafting plan helpers: `PlanCraftResult`, `PlanRecipe`,
  `PlanIngredientCheck`, and generic `Plan`
- `TryFindRecipesForResult(int itemId,
  out IReadOnlyList<RecipeSnapshot> recipes, out string status)`
- result item and ingredient validation through `Items`
- inventory execution gating through `Inventory`
- explicit runtime execution status in every crafting plan
- plan counters and last-plan reason/status in runtime state
- loader state block `openAcaiSonsCrafting`
- capability state block `capabilities.sonsCrafting`
- built-in `Sons Crafting` status provider in the `Extensions` tab
- optional server-policy row `sons-crafting-plan-gate`

Crafting plans remain non-mutating. `TryBeginCurrentCraft` executes the actual
native mat combination path and returns an observer that distinguishes acceptance
from completion. It does not execute arbitrary result-ID plans, manufacture
ingredients or bypass their native lifecycle. `TryStageIngredient`,
`TryReturnIngredient` and `TryCollectCraftedResult` use the current native layouts
and report exact custody observations instead of assuming a callback succeeded. See
[native runtime actions](SOTF_RUNTIME_ACTIONS.md).

Recipe reads are separate from those caller-supplied plans. The numeric argument
is a positive **result item ID**, not the plan's string recipe label or a native
recipe ID. The initialized Unity lifecycle thread and an in-world local player
are required. Each call reacquires the current crafting system/database and
scans its finite recipe list by Count/indexer; no cached native objects or
automatic per-frame scans. It returns all live recipe entries whose declared
result list contains that item, including matches beyond the first result slot.
True with an empty list means no declared match in the scanned list; false
returns an empty list and failure status, never a partial success.

The managed read-only snapshots contain the exposed name/type/localization
metadata, force-new-instance/container-result flags, declared result item IDs,
and ingredient item IDs/raw counts/reusable flags. Recipe and entry order,
duplicates and raw counts are preserved; names are not persistent identities.
Null/dead recipes and null declared-result lists are skipped. A matching recipe
without an ingredient list fails the query. Reads are not an atomic database
snapshot and do not establish inventory availability, consumption, result
quantities, or actual output, especially for container/generated results.
They do not call Initialize, RefreshAvailableRecipes, result generation,
ingredient consumption or crafting execution. Native getter behavior and the
resulting snapshots still require in-game acceptance.

## Current Sons Networking SDK Slice

`Modstoke.Sons.Networking` exposes the OpenACAI-owned networking
planning surface:

- service key: `openacai.sons.networking`
- `State`, `Ready`, `Refresh()`, `PlanningEnabled`, and
  `RuntimeTransportEnabled`
- reflection-only Bolt/session readiness probes without adding direct Bolt or
  UdpKit compile dependencies
- OpenACAI-owned message channel registration through
  `TryRegisterMessageChannel`
- message intent validation through `PlanMessage`
- supported targets: `only-self`, `only-server`, `all-clients`, `everyone`,
  `others`, and `connection`
- payload-size, target, channel ownership, active-network, and server-authority
  gates
- loader state block `openAcaiSonsNetworking`
- capability state block `capabilities.sonsNetworking`
- built-in `Sons Networking` status provider in the `Extensions` tab
- optional server-policy row `sons-networking-plan-gate`

This slice intentionally validates and reports multiplayer intent without
installing packet/chat hooks or sending traffic yet. Runtime transport remains
disabled until the Bolt packet path is verified in game.

That limitation describes the public planning API. The legacy compatibility
layer has owned packet and native chat implementations, but those are not yet
the public server-first messaging API described above and do not establish
unmodified-client acceptance on their own.

## Current Sons Building SDK Slice

`Modstoke.Sons.Building` exposes the OpenACAI-owned building and
blueprint planning surface:

- service key: `openacai.sons.building`
- `State`, `Ready`, `Refresh()`, `PlanningEnabled`,
  `RuntimePlacementEnabled`, `RuntimeBookIntegrationEnabled`, and
  `SupportedPlanKinds`
- OpenACAI-owned blueprint roots under `Modstoke/UserData/Blueprints/Mods`
- legacy `Mods/Blueprints` discovery for compatibility visibility
- mod-owned blueprint directory helpers:
  `GetModBlueprintDirectory`, `EnsureModBlueprintDirectory`,
  `GetModBlueprintFilePath`, and `ListModBlueprintFiles`
- custom blueprint registration through `TryRegisterBlueprint`
- building intent validation through `PlanBlueprintItem`,
  `PlanCustomBlueprint`, `PlanBlueprintFile`, and generic `Plan`
- loader state block `openAcaiSonsBuilding`
- capability state block `capabilities.sonsBuilding`
- built-in `Sons Building` status provider in the `Extensions` tab
- optional server-policy row `sons-building-plan-gate`

Building plans remain non-mutating. Explicit APIs now register prepared runtime
blueprints and book pages, enter/confirm/cancel native interactive placement,
and submit native ingredient/cancellation actions on the currently selected node.
They preserve native placement and multiplayer authority, not instant completion.
File plans still require an importer or authored prefab. See
[native runtime actions](SOTF_RUNTIME_ACTIONS.md) for the supported contract.

## Current Sons Save SDK Slice

`Modstoke.Sons.SaveLifecycle` and
`Modstoke.Sons.SaveRegistry` expose the first OpenACAI-owned save
lifecycle and custom-save surfaces:

- service keys: `openacai.sons.saveLifecycle` and
  `openacai.sons.saveRegistry`
- loader state blocks `openAcaiSonsSaveLifecycle` and
  `openAcaiSonsSaveRegistry`
- save/load hooks for `SaveGameManager.Load` and `SaveGameManager.Save` through
  the OpenACAI Harmony safety gate
- legacy SonsSdk save-event relay for load/save targets whose SDK hook setup has
  not been attempted; applying, failed or returned registrations suppress it
- per-mod sidecar JSON registration through `ISaveable` and
  `RegisterJson<T>`
- explicit serializer storage intent through
  `SaveStorageMode.SidecarJson`,
  `EmbeddedPreferred`, and `EmbeddedRequired`
- `RegisterEmbeddedJson<T>` for mods that want embedded save-file storage once
  the Sons save embed hook is verified
- sidecar fallback counters for embedded-preferred saveables and skip counters
  for embedded-required saveables while embedded storage is not installed
- built-in `Sons Save Lifecycle` and `Sons Save Registry` status providers in
  the `Extensions` tab
- optional server-policy rows `sons-save-lifecycle`, `sons-save-registry`, and
  `sons-save-serializer-routing`

The current before/after phases describe Harmony hooks around the declared
SaveGameManager entrypoints, not verified serialization or durable IO completion.
The installed Save wrapper returns void; Load returns bool, but its result is not
used to gate the after-load notification. Legacy AfterSaveLoading now runs as a
postfix rather than a prefix. The SDK's public patch methods remain the same and
forward their callback logic to Core on the normal path.

Fallback routing uses separate SDK load/save installation outcomes, not the flag
requesting SDK initialization. A failed attempt has uncertain native state and
does not trigger a second publisher; a target not yet attempted remains eligible.
`LegacySonsSdkRelayEnabled` means either target is eligible, and
`LegacySonsSdkRelayReason` reports both target states. A returned registration is
not native execution proof. These setup records do not track external patching
that bypasses `SavingCallbackPatches.Patch` or guarantee routing across arbitrary
patch mutations during a save/load call.

The registry deliberately stores custom mod data under Modstoke UserData
sidecar paths first. Embedded save-file intent is now declared and reported, but
the runtime will not pretend to write into Sons save files until a specific game
save hook is verified.

## Current Server Policy Slice

`Modstoke.Loader.ServerPolicy` exposes the public protected-runtime
policy registry:

- service key: `openacai.loader.serverPolicy`
- `IServerPolicyProvider` for public policy providers
- `ServerPolicyProviderSnapshot` for registered provider metadata
- `ServerPolicySnapshot` for provider-owned policy status
- `ServerPolicyRequirementSnapshot` for individual checks
- `TryRegisterProvider`, `RegisterOrUpdateProvider`, `TryUnregisterProvider`,
  `ProvidersSnapshot`, `PoliciesSnapshot`, and `TryGetPolicy`

The built-in `OpenACAI Runtime Baseline` provider publishes the first public
protected-server readiness policy. It checks loader readiness, public SDK
services, compatibility shim state, compatibility dependency readiness, file
integrity, library separation, private payload boundary, runtime attestation,
the optional Sons runtime state, environment, save-data, asset path, item
catalog, item-registration, inventory readiness, crafting/building/networking
plan gates, save lifecycle, and save registry signals, and the explicit
RedLoader hosting gate.
The `Protected Runtime` provider renders `Server Policy` first so these
disclosed readiness signals are visible before lower-level diagnostics.
It also renders a `Timing` section for process entry delay, loader startup,
state-write, title button attach, and recent event telemetry.

## Current Compatibility Dependency Slice

`Modstoke.Loader.CompatibilityDependencies` exposes the curated
RedLoader/SonsSdk dependency baseline:

- service key: `openacai.loader.compatibilityDependencies`
- `Snapshot`, `Ready`, `Entries`, `Warnings`, and `Refresh()` helpers
- packaged/runtime/bootstrap-mirror readiness for `Tomlet`,
  `SemanticVersioning`, `Alt.Json`, and `GLTF`
- top-level `dependencyCount`, `readyDependencyCount`,
  `packagedDependencyCount`, `warningCount`, and `bindingConflictCount`
  summary fields for manager, server-policy, and diagnostics consumers
- binding-conflict diagnostics when an older assembly was already loaded before
  the OpenACAI packaged baseline could be used

The built-in server policy requires the packaged dependency baseline to be
ready. Runtime attestation includes dependency counts, ready/packaged counts,
warning counts, and binding-conflict counts so protected servers can distinguish
package readiness from transitional bootstrap binding drift.

The current package also aligns `SemanticVersioning.dll` in the transitional
`BepInEx/core` bridge with OpenACAI's curated `3.0.0.0` baseline. This keeps
older BepInEx references compatible while preventing the early bootstrap from
preloading `2.0.2.0` and surfacing a false dependency binding conflict.

## Current File Integrity Slice

`Modstoke.Loader.FileIntegrity` exposes the installed package file
manifest as an SDK-visible runtime service:

- service key: `openacai.loader.fileIntegrity`
- `Snapshot`, `Ready`, `Entries`, `IssueEntries`, `TryFindEntry()`, and
  `Refresh()`
- primary `Modstoke/Runtime/openacai-loader-files.json` manifest verification
- bootstrap compatibility mirror verification under
  `BepInEx/plugins/OpenACAILoader/openacai-loader-files.json`
- installed file existence, size, and SHA-256 checks
- critical-file counts for bootstrap, runtime, hook, and OpenACAI compatibility
  files

This lets protected servers, companion tools, and mods consume disclosed loader
file health through OpenACAI APIs instead of scraping manager files or treating
dependency libraries as mods. The public `Protected Runtime` page renders these
signals in a dedicated `File Integrity` section, and the built-in server policy
requires the `file-integrity` row to be ready.

## Current Runtime Attestation Slice

`Modstoke.Loader.RuntimeAttestation` exposes a compact public runtime
evidence snapshot for protected servers and companion tools:

- service key: `openacai.loader.runtimeAttestation`
- `Snapshot`, `Ready`, and `Refresh()`
- unsigned evidence digest/token built from loader version, .NET runtime,
  manifest hashes, verified file counts, compatibility dependency readiness,
  mod/library separation counts, hosted count, and disclosed compatibility shim
  states
- no absolute local paths and no private admin or anti-cheat implementation
  details

This gives protected-server policy providers one OpenACAI-owned object to check
without binding to BepInEx/RedLoader internals or scraping the Mods menu. The
public `Protected Runtime` page renders these signals in a dedicated
`Runtime Attestation` section, and the built-in server policy requires the
`runtime-attestation` row to be ready before reporting zero required failures.

## Admin Tool Position

Modstoke Admin should adapt to this SDK, not the other way
around.

Recommended split:

- Admin chat: a private `IMenuProvider` tab or panel named something
  like `Server` or `Admin`, shown only when the private admin module is present
  and authorized.
- Host controls: private command providers that use public command/action
  plumbing.
- Server health and moderation signals: public-style status providers with
  private data redacted when the user is not authorized.
- Anti-cheat/protected-server checks: a disclosed protected-server runtime
  layer exposed as capability and policy state.

This may require changing the current admin panel code, which is acceptable
because OpenACAI controls it. The loader should not be bent around the old admin
panel window if a more general SDK provider model serves future modders better.

## Public Mods Menu Scope

The public Mods menu should remain useful without the admin panel installed:

- show RedLoader-style and BepInEx-style mod inventory
- show Modstoke-native packages from `Modstoke/Mods`
- keep RedLoader-style and BepInEx-style entries labeled as compatibility
  inventory, not as the runtime identity
- show mod authors, versions, sources, warnings, and missing files
- edit detected configs through friendly and raw text modes
- expose credits for upstream/runtime/mod authors
- show loader compatibility and runtime status
- provide future mod-contributed tabs/actions through the SDK
- avoid admin-only labels or behavior unless the admin provider is active

Admin and security entries can appear as additional sections only when relevant.

## Security Boundary

OpenACAI can support protected servers, but the public loader should not become
a hidden anti-cheat payload.

Use explicit server policy, signatures, file hashes, mod allow-lists, runtime
capabilities, and disclosed attestation status. Keep private detection logic and
private admin source outside public packages. Public APIs can expose stable
security-relevant signals without leaking private detection internals.

## Runtime activation observations

`LoadedMods.TryCapture(out snapshot)` reads the owned hosts on the
startup/Unity thread after Core startup completes. It copies IDs, versions and
activation states without rescanning manifests or invoking mod callbacks. Entries
contain no assembly paths, plugin objects or exception details.

The host families provide different evidence:

| Family | Observation |
|---|---|
| Native Modstoke | Captured startup manifest identity and version; active only after startup callbacks completed, while execution is enabled and the mod has neither failed nor stopped. |
| Retained RedLoader | Current registration and current manifest identity/version. Registration does not prove that initialization callbacks succeeded. Dynamic registrations outside the retained host list are not included. |
| BepInEx | Copied identity/version from a successful `BasePlugin.Load`, plus whether the exact plugin and metadata remain registered. This is historical load evidence, not ongoing lifecycle or integrity proof. |

BepInEx observations include normal discovery, dynamic discovery and the direct
low-level load API. Direct calls are labeled, use the metadata's normalized
semantic version, and need not be registered. Weak references avoid extending
the lifetime of directly loaded plugins. Repeated successful activations remain
distinct; copied records are retained up to 1,024 observations per process.

The public snapshot contains at most 512 entries. Check `Truncated` and
`HasUnreadableEntries`; a successful read is not a claim of complete coverage.
Oversized or malformed identity text is not silently shortened into a new ID.
Duplicate IDs are marked within each loader family, using ordinal comparison for
BepInEx and ordinal-ignore-case comparison for native and RedLoader entries.
Libraries, arbitrary injected code and assemblies loaded outside these hosts are
outside this report's coverage.

```csharp
if (LoadedMods.TryCapture(out var snapshot) == LoadedModsReadStatus.Success)
{
    var result = snapshot.CheckNativeRequirement("example.community-map", "1.2.0");
    // Present the result as a local observation, then capture again when needed.
}
```

`CheckNativeRequirement` compares exact version strings against active native
entries only. It distinguishes missing, inactive, ambiguous and mismatched
entries. Incomplete coverage, unavailable native execution and stopped runtimes
cannot return `Matched`. Legacy registrations or library files cannot satisfy a
native-mod requirement. This comparison uses the native host's ID matching rules;
it does not yet consume a Manager requirements document or enforce server admission.

The existing native `Loaded` and `LoadedCount` fields retain their historical
meaning. Additive `Name`, `Version`, `Active`, `ActiveCount` and `LoadCompleted`
fields expose current activation evidence without changing that compatibility
contract. Later callback failure and shutdown publish inactive entries.

Capture again after lifecycle changes. A snapshot becomes stale as the runtime
changes and must not be cached as authorization. Loader state files include the
latest capture status and snapshot when available; the first startup write can
report `Unavailable` until Core startup completes. No network report, client
attestation, enrollment selection or gameplay gate is enabled by these APIs.

## Required native mods at admission

`ModRequirements.TryParse` reads the portable schema-1 requirements
document exported by Modstoke Manager. It accepts 1–128 native package IDs with
exact version strings. Unknown or duplicate fields, invalid Unicode, duplicate
IDs and oversized documents are rejected. The input and canonical transport form
are each limited to 64 KiB.

The SDK follows native host ID comparison and also rejects invariant-lowercase
collisions. Rust and .NET Unicode casing differ for some non-ASCII IDs, so a
Manager preparation list must still pass SDK validation. Manager additionally
checks the size of pretty-printed export; the SDK checks compact transport JSON.

Before Bolt starts, a server integration can parse its operator-selected JSON and
call `ModAdmission.SelectForNextStart(true, requirements)`. A joining
client integration calls `SelectForNextStart(true)` without server requirements.
Both calls must succeed on the startup/game thread. A server started without a
requirements document rejects this mode. This API selects gated enrollment;
selecting ordinary enrollment later clears the mod-admission selection.

The feature is off by default. Initialization alone does not select it. The SDK
does not read an arbitrary requirements file or enable a server policy
automatically. Modstoke Client is a separate optional native package under
`src/Modstoke.Client`; it is not bundled into the Loader runtime. Manager server
launch wiring and a Client connection screen remain separate work.

`TryReadSelection(out snapshot)` reads the local selected mode, gameplay-gate
selection, transport state, immutable server requirements and whether the native
network startup boundary permits a change. It runs on the game thread and does
not modify selection. `CanChangeForNextStart` is not a promise that hook setup
will succeed. A failed read provides no snapshot.

The snapshot's `Revision` changes on each successful admission selection and
when ordinary enrollment clears it. Session reset preserves it. Integrations
can record the revision after selecting, then check it before clearing their
own mode; it is an observation, not an ownership lock. Disabled startup must not
clear another integration's selection. A joining-client integration should
preserve an existing server requirements selection. Configured settings and
observed selection must be reported separately, especially after a live reload
that requires a new session or application restart.

Enrollment capability bit 63 is reserved for this protocol. The existing
56-byte enrollment protocol and channel identity are unchanged. Only explicitly
selected peers that negotiate the capability can exchange admission documents.
The server sends its requirements; the client returns the native activation
snapshot described above. The server compares every requirement, preserving
missing, inactive, ambiguous, unavailable and version-mismatch results.

Admission transfers use the existing reliable stream channel, with a separate
512-frame send and receive allowance per exact peer. Ordinary enrollment keeps
its 32-send/64-receive lifetime limits. Each document is limited to 64 KiB and
uses 208-byte chunks, four per acknowledged window. At maximum size, each
direction needs at most 316 data frames, 79 window acknowledgments and a final
decision. Global admission data sending is bounded to 32 frames per update;
existing transport queue limits remain in force. No allowance refills or resets
within a connection. A transfer expires after 120 seconds.

Frames bind to the enrollment nonce and exact current transport/session handle.
Repeated matching chunks cannot change received data; conflicting duplicates,
invalid windows, malformed documents and stale connections reject admission.
The client recaptures its native report before accepting the server's decision
and rejects if local activation state changed during the exchange. The gate
holds the covered ordinary client gameplay packet paths until admission, then
requests disconnection on rejection. Server-to-client initialization and
replication remain unchanged; this is not a private-world-data boundary.

A joining client whose host sends no enrollment frame within the 15-second
negotiation window is released instead: its enrollment state becomes
`HostNotParticipating`, ordinary gameplay resumes, and features that require an
enrolled host (strict combat, migration, SDK datagrams) stay off. Vanilla hosts
and Modstoke hosts that did not select enrollment behave this way; a host that
enrolls without the admission capability still disconnects a client that
selected admission. If the host's
first frame arrives later, enrollment negotiates normally with a fresh window,
unless the client already sent gameplay while released; then enrollment ends
with `HostAnsweredLate` and the client disconnects so the player can rejoin
cleanly. A host that participates is never released.

`TryRead(peer, out snapshot)` and `TryRead(session, out snapshot)` expose the
local decision, per-requirement results and reported native entries. Reports
contain no local file paths or executable objects. They cover native mods only;
legacy registrations cannot satisfy native requirements. An oversized or
incomplete report cannot establish a match. Snapshots describe admission time,
not continuous monitoring, and must not be cached as gameplay authorization.

`TryReadRecent(out observations)` copies up to 32 recent admission attempts on
the game thread, newest attempt first. Updating an older attempt does not move
it ahead of a newer one. Records remain in memory after disconnect, network
reset or selection changes and disappear on eviction or process exit. A read
does not access native connection objects or change the admission decision.
A released client is recorded as `Admitted` with reason `HostNotParticipating`
and no requirements. That record is withdrawn if the host takes part later;
the real attempt is then recorded once admission starts.

Each immutable observation contains a process-local peer GUID, whether the
local side was joining a server, the last observed admission state and reason,
requirements name and results, observation time, and a `ConnectionEnded` flag.
It contains no account identity, server address, native handle or full mod
report. `ConnectionEnded` records local cleanup; false does not prove the
connection is alive. A closed pending attempt has no observed admission
decision. Enrollment failures before this protocol starts produce no record.
Retained acceptance is historical evidence and cannot authorize a later
connection. Modstoke Client uses these records to keep compatibility results
readable after a disconnect.

Client reports remain untrusted claims. A dishonest client can fabricate its
report; enrollment and a matching mod list do not establish integrity or grant
Tribe, administration or combat authority. Queuing a decision does not prove
the remote interface displayed it before disconnection. The protocol and gate
still require observed listen-host and dedicated-server multiplayer acceptance,
including native connection timeouts, reliable-channel behavior and reconnects.

## Next Implementation Targets

1. Done: add a small UI/menu provider registry to the public SDK.
2. Done: expose built-in Mods list/config/credits/protected-runtime metadata as
   built-in providers and make built-in tab labels/tooltips consume it.
3. Done: add an `Extensions` tab that lists built-in extension providers and
   external providers registered through `MenuRegistry`.
4. Done: add structured detail panels for external menu providers through
   `IMenuContentProvider`.
5. Done: add renderer-side editing for structured config providers through
   `IConfigProvider`.
6. Done: add command invocation for safe provider actions.
7. Done: add richer inventory SDK helpers and compact catalog state.
8. Done: add a generic server/protected-runtime policy provider registry.
9. Done: add a public runtime-attestation evidence surface for protected-server
   consumers.
10. Done: add first read-only Sons item metadata catalog helpers.
11. Done: add OpenACAI-owned item search/query/filter helpers over the catalog.
12. Done: add first read-only Sons inventory readiness gate.
13. Done: add first OpenACAI-owned Sons asset/audio path helpers.
14. Done: add first OpenACAI-owned gated keybind registration helpers.
15. Done: add OpenACAI-owned save lifecycle and save registry status providers.
16. Done: add OpenACAI-owned environment and save-data status providers.
17. Done: add a dedicated Runtime Libraries status provider and policy row.
18. Done: add OpenACAI-owned Sons runtime-state status provider and policy row.
19. Done: add custom save serializer storage-intent routing and policy status.
20. Done: add OpenACAI-owned asset/audio discovery and AssetBundle load/unload
   helper status.
21. Done: add OpenACAI-owned inventory action planning/gate status.
22. Done: add OpenACAI-owned crafting planning/gate status.
23. Adapt the private admin panel to register its chat and controls through the
   provider registry.
24. Added the [World Conditions example](../examples/WorldConditions/README.md):
   a native mod with a typed settings page and read-only refresh action, using
   only the public Core assembly. In-game loading and UI acceptance remain open.
25. Done: extend item helpers into OpenACAI-owned inventory, crafting, and safe
    item registration planning APIs after catalog validation.
26. Done: add an OpenACAI-owned native mod host validation gate that keeps
    libraries out of native mod candidates.
27. Done: add metadata-only native host assembly and entrypoint validation
    without executing Modstoke-native mod assemblies.
28. Done: add metadata-only native host assembly reference/dependency
    validation without executing Modstoke-native mod assemblies.
29. Done: add metadata-only native host execution-plan validation without
    executing Modstoke-native mod assemblies.
30. Done: add OpenACAI-owned native lifecycle hook registration planning without
    executing Modstoke-native mod assemblies.
31. Done: add disabled-by-default native lifecycle invocation-gate dry-run
    counters without executing Modstoke-native mod assemblies.
32. Done: add OpenACAI-owned native lifecycle dispatch planning with runtime
    dispatch disabled and no Modstoke-native mod method invocation.
33. Done: add OpenACAI-owned native lifecycle invocation adapter diagnostics
    with stable binding tokens and no Modstoke-native mod method invocation.
34. Done: add OpenACAI-owned native lifecycle invocation token resolver
    diagnostics with non-executing token lookup and no mod method invocation.
35. Done: add non-executing native lifecycle invocation token lookup audit
    counters and SDK-visible lookup result snapshots.
36. Done: add a metadata-only native lifecycle invocation replay journal that
    records ordered replay intents and dry-run replay plans without method
    invocation.
37. Done: add metadata-only native lifecycle invocation scheduling with
    deterministic hook ordering and per-hook replay batches.
38. Done: add metadata-only native lifecycle invocation execution planning with
    zero method invocation while runtime invocation remains disabled.
