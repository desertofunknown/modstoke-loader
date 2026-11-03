# Bolt event policy

`ServerEvents` provides an optional server-side decision point at
`Bolt.EventDispatcher.Dispatch`, before native routing, forwarding and local
listeners. It applies to events from vanilla and modded clients and to local
server events. No additional client component is needed for this hook.

Subscribe to `Receiving` during mod initialization and unsubscribe on unload.
With no subscribers, the hook leaves native behavior unchanged without reading
payloads or resolving sessions. Existing typed SDK policies remain available
and can still reject an event accepted by the common policy.

## Request context

Each `BoltEventRequest` contains:

- `EventName` and the current build's protocol `EventTypeId`.
- `Origin`, `SourceConnectionId` and an optional `SourceSession`.
- Routing targets, routed entity/connection IDs, sequence and reliability.
- An optional field `Schema` and callback-scoped `TryReadField` access.

The source is the immediate transport connection, not proof of original
authorship after forwarding. A missing session is unknown, not a local sender
or cheating evidence. Local/remote classification uses the actual connection,
not player IDs supplied in the payload. Numeric type IDs are build-specific.

`Reject()` rejects this dispatch only. Decisions expire when the synchronous
handler sequence ends. Exceptions from subscribed handlers also reject that
dispatch. The SDK revalidates routing and any resolved session after callbacks;
a disconnect, changed route or replaced connection cannot reuse the decision.
It owns storage cleanup only for its own rejection, including recursive calls
that reuse an event. Native exceptions are not suppressed.

This API is for trusted local mods. It does not sandbox another server plugin
that directly mutates game memory or replaces the dispatcher.

## Dispatch order

The common policy and most typed policies share one Harmony prefix on
`Dispatch` (`ServerDispatchGate`, priority `First + 100`, before
`sonssdk_harmony`). It reads each event's native class once and calls only the
policies whose event types match, in this fixed order:

1. `ServerEvents.Receiving`
2. `ServerChat.Receiving` and registered chat commands
3. `ServerLootSpawns.Receiving`
4. `ServerWorldLocators.Receiving`
5. `ServerConstruction.IngredientAdding`
6. `ServerConstruction.CompletionRequested`
7. `ServerVehicles` sled and golf-cart requests
8. The free-form construction authority guard
9. The player-spawn authority guard
10. `ServerPlayerHits.Receiving` and strict-combat hit ownership
11. `ServerPlayerRevives.Receiving`
12. `ServerInventory.Receiving`
13. `ServerSleep.Receiving`
14. Server notice frames

This is the order the separate prefixes ran in before. The first rejection
stops the remaining policies. Completed player event routing still runs before
the gate. `ServerPrinters`, `ServerNamedStates` and `ServerCompanions` keep
their own `Priority.Last` prefixes, so they still see an event only after other
mods' prefixes have accepted it.

When a typed policy rejects an event, the gate releases its storage before the
next policy runs. A common-policy rejection keeps the event until the gate's
finalizer, and a nested `Dispatch` of an event the common policy still holds is
rejected without releasing it again.

## Payload reads

`KnownEventSchemas` lists concrete event wrappers in the installed game event
assembly. The currently inspected native build has 203 event types. This is not
proof of 203 active gameplay handlers or an inventory of all dynamic factories.
Unknown custom classes can still reach the common policy with a null schema.
Typed getters require an exact native class match, not merely a matching name.

`TryReadField(name, out value, out status)` reads only the requested field.
Supported results are managed booleans, numbers, strings, numerics vectors and
quaternions, or raw entity network IDs. Entity fields use their generated raw-ID
accessor, not a lookup that could mistake an unresolved entity for ID zero.
Arrays, tokens and other native objects are explicitly unsupported. Strings
over 1024 characters return `TooLarge`, not a truncated value.

`CanRead` describes accessor support, not whether a native read has succeeded.
Always check the read result and status. Unknown fields, unsupported values,
expired callbacks, wrong-thread access and native failures are not zero-valued
evidence. Payloads remain untrusted claims even when read successfully.

Metadata and getter delegates are cached. Accessor compilation occurs only on
first use; normal reads do not use reflection invocation. Handler lists are
copied when subscriptions change, not for every dispatch. No automatic packet
logging, roster polling loop, new wire messages or punishment rules are added.

## Coverage limits

These limits describe the event gate specifically. Separate packet, stream,
state, command and entity APIs are documented in [Bolt networking](BOLT_NETWORKING.md).

| Surface | Common event gate |
| --- | --- |
| Native Bolt events reaching `Dispatch` | Optional pre-routing rejection, including events without a typed SDK policy |
| Client prediction or earlier mutation | Cannot undo it |
| Packet decoding and the receive filter | Earlier than this hook; not covered |
| Entity/state replication and command channels | Separate paths; not covered |
| Blob/construction transfers | Separate transport; use the existing scoped APIs |
| Direct game calls, host updates and save restoration | Not covered merely because an equivalent event exists |
| Client-owned health, ammunition and inventory | Reading claims does not transfer authority to the server |
| Client integrity or managed-client enrollment | Neither proves the client is honest |

The hook is qualified for the recognized native client build only. An unknown
build, including an unqualified dedicated-server binary, does not activate it.
`State` reports registration separately from unsupported network surfaces.
Successful registration or compilation is not multiplayer acceptance.

Prefer existing typed APIs when they provide the necessary fields and native
context. Use the common gate for additional events such as raft control,
zipline actions, cooking, trap refills and electrical switches. A policy must
establish the operation's native meaning and server-observed permissions before
rejecting legitimate requests. Do not infer possession from a claimed item
count, or apply a numerical threshold without the relevant game rules.

The broader SDK can observe replicated state, command processing and control
changes, but observation does not establish server authority over their values.
Full anti-cheat coverage still requires operation-specific rules and authority
over client-local combat. This API does not enable strict combat or change its readiness.
