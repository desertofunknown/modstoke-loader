# Named world-state access

Modstoke SDK by OpenACAI exposes the server boundary for native
`ChangeIdObjectState` requests. The game uses this route for some facility doors
and for other named world state. It is an admission mechanism for integrations
that define their own permissions.

## Requests and identity

Subscribe to `ServerNamedStates.Changing` and remove the subscription
when the integration stops. Each `NamedStateRequest` contains the raw
`RequestedIndex`, `RequestedValue`, `RoutingTargets`, a copied `RegisteredName`,
`Origin`, `ReceivedAt` and an exact `SourceSession` for remote ingress.
`ServerLocalDispatch` has no authenticated player identity. Do not treat a null
session as the listen host.

Call `Reject()` synchronously on the callback thread to skip the event before
native dispatch forwards or applies it. Rejection cannot be reversed or made
after the callback returns. A handler exception or missing required context
denies a subscribed event. Reentrant dispatch is also denied. Keep callbacks
bounded and do not call native state setters from them.

The SDK resolves the name from the native registry and checks the mapping again
after handlers return. The event's index and value remain client-supplied data.
Ordinary client publication uses route `6`; a policy must also consider routing
when authorizing a request. Other route values are not evidence of authority.

## Registry reads

`TryReadRegistry(out snapshot, out status)` reads the current registry only on
the owning game thread while the server and hook are available. A successful
`NamedStateRegistrySnapshot` contains immutable `Entries` with copied
`Index` and `Name` values and an `ObservedAt` timestamp. Reads are bounded to
4,096 entries and 256 plain-text UTF-16 characters per name. Unsupported or
ambiguous registry data is unavailable rather than partially reported.

Store exact names with ordinal comparison in world-bound policies. A numeric
index can change between game builds and must not become a persistent facility
identifier. Names can also disappear after an update; retain the policy and
report the unavailable binding instead of silently attaching it to another key.
Registry membership does not classify the live component or validate a proposed
state value.

## Native coverage

The inspected server receiver applies the requested value to named game state
and the replicated server-info array. The dispatch hook runs before that path
and before forwarding. Native security doors can already have changed locally
before submitting the event; one terminal coroutine also inlines the state
setter. Rejecting the server event does not undo the sender's local state,
animation or movement.

Direct host changes, local script or debug calls, save restoration and native
replication application are outside this request boundary. It does not replace
keycard checks, validate inventory, prevent movement through geometry or
establish private per-player door state. It does not expose an operation
completion receipt or modify the game's save data directly.

Without subscribers, native dispatch is unchanged. Client and offline dispatch
are outside this server hook. `State.Status == Installed` reports hook
registration, not observed multiplayer enforcement. Listen-host and dedicated
operation, scene loading, late joins and rejection behavior still require game
acceptance with the matching build.
