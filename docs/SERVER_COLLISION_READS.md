# Server collision reads

The Modstoke SDK, by OpenACAI, provides bounded reads of the server's default
physics scene through `ServerWorld`. They are available on the SDK
game thread while a server world record context is current. Neither a local
player nor Modstoke Client is required for the query itself.

## Segment queries

`TryTraceSegment` takes two finite, distinct `System.Numerics.Vector3` endpoints,
an explicit nonzero layer mask and an explicit `Ignore` or `Collide` trigger
policy. It performs a point raycast, with distance derived from the endpoints.
It does not use an infinite distance or the mutable global trigger default.

The return value describes completion of the query:

- `true` with `Success` returns an `CollisionHit`.
- `true` with `Miss` means that native raycast found no collider.
- `false` reports an unavailable, invalid, stale or failed read. These outcomes
  are not misses and must not allow a projectile to travel through unchecked space.

Queries, material reads, reverse exit queries and player-hit matching share a limit of 256 reads per game frame and
world context. Attempts that enter the native read consume the budget even if
they fail. `LimitReached` is explicit. A simulation must respect that limit;
it must not silently drop collision checks or turn budget pressure into player
punishment. This limit has not been qualified for 32-player combat.

When automatic physics synchronization is disabled, the first segment query
through this API synchronizes transforms once for that frame. Later queries
read the current physics scene; this is not a frozen snapshot, and transforms
changed later in the frame may not yet be synchronized. Run a coherent batch
after the server has established the transforms it intends to query.

## Player attachment matching

A hit contains copied endpoints, query settings, point, normal, distance, layer
and trigger state. Its optional `CandidateNetworkId` is a lookup hint. It does
not identify a player or authorize damage.

Internally, the hit retains its collider, native hit transform and the nearest observed Bolt entity
attachment, with ancestry traversal limited to 32 transforms. Traversal stops
at the nearest entity even when it is unattached; it never skips that entity
to attribute its collider to a parent. Unknown ancestry does not invalidate the
physical hit, but cannot establish a player match.

Call `TryMatchHitPlayer` with the hit and an already retained
`PlayerHandle`. Matching requires the same frame and world context,
a live unchanged collider and hit transform, unchanged entity ancestry and attachment, and the
binding's current canonical player/session identity. It rechecks native context
before returning. A hit on another entity does not retire a valid player binding.
A replacement or lost canonical player does retire that binding through the
existing player-binding rules.

Do not persist hit handles or use them on another thread. Copy any diagnostic
fields you need. For authoritative combat, retain the intended target's life
before querying and recheck that life at application; the SDK knows native
attachments, not a private combat ledger's life generations.

## Player impact receivers

`TryMatchHitPlayerReceiver` performs the attachment checks above and also follows
the native impact callback chain. It reads `IImpactReceiver` components on the
hit's effective transform, exactly where native projectile impacts look. It
does not search parents or children for a more convenient damage receiver.

The supported route has exactly one receiver: a `DamageNode` registered with a
`DamageController`, or the controller itself. Each callback stage must have one
entry with the expected declaring type, method and target. Local ownership must
match the current `LocalPlayer.Stats` and its controller. Remote ownership must
match `BoltPlayerSetup.SendPlayerHit`, that setup's controller and its already
cached exact player attachment. The SDK does not invoke a lazy entity resolver.

The route is read twice, retaining and comparing receiver, controller, owner and
delegate identities. It shares the collision read budget and same-frame rules.
Native component discovery and method metadata resolution can allocate; the SDK
does not bound that native work or execute the impact callbacks.

Missing routes return `ReceiverUnavailable`; unsupported components or handlers
return `ReceiverUnsupported`; additional receivers or callback entries return
`ReceiverAmbiguous`. A replaced otherwise-supported route returns
`ReceiverChanged`. These failures supply no player match. In particular,
`PhysicalImpactReceiver` is not ignored: its UnityEvent can invoke additional
configured behavior.

Success establishes a supported callback association, not the outcome of
calling it. Node validity, blocking, team rules, absorption, armor and final
health damage still need their own policy. It neither applies damage nor proves
that a client accepts server-owned health. The original `TryMatchHitPlayer`
retains its existing attachment-only behavior.

## Scope

These reads do not prove collision coverage, trustworthy replicated poses,
complete damage behavior, permitted aim, weapon range or attack permissions.
Disabled, missing or streamed-out colliders can affect results. A miss is an
observation of this native physics query, not proof that the world contains no
obstacle or player. Raycasts also retain native limitations such as origins
inside colliders; see [Unity's raycast notes](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Physics.Raycast.html).

The API does not move actors, apply damage or enable strict combat. Native
projectile stepping, penetration and ricochet handling, receiver-specific
damage, life ownership and bypass prevention remain separate work. Listen-host
and dedicated-server behavior still require multiplayer acceptance.
