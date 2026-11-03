# Native NPC party pacing

`Modstoke.Sons.ServerNpcParties` lets server mods defer a whole
naturally queued native `SearchPartyEvent` before target selection and actor
creation. It is part of Modstoke SDK, by OpenACAI. The game retains control of
the event, its expiry, targets and actors. This API does not force movement,
spawn actors or change retreat behavior.

## Availability and scope

Check `State.Status` and `State.Detail` for hook availability. `Installed` means
the hooks registered; it does not establish successful spawning or multiplayer
qualification. The API is compiled but native multiplayer behavior remains
unqualified.

Both dedicated and listen servers require Bolt to be running as the server, a
current world context, and active native world-event and simulation managers.
Callbacks run on the server game thread. Native party representation does not
require the Modstoke Client.

Pacing applies only within the matching `VailWorldEvents.Update` scope, when the
SDK can identify exactly one matching native queue entry. Both the scheduling
call and that entry must have an actual null forced target. `RunEventNow` calls
are excluded, including calls made inside an update. This is not a general
interceptor for every search-party or actor operation.

The SDK scans queues of at most 256 entries and considers at most eight
candidates per frame. Oversized queues, ambiguous matches, unavailable context
and candidates beyond that frame budget fall through to native scheduling.
Subscribers therefore do not receive every native scheduling call.

## Considering and deferral

Subscribe to `Considering` with a bounded synchronous handler. Its
`NpcPartyRequest` contains `WorldId`, `WorldGeneration`,
`ScheduledDay`, `QueueAgeHours` and the UTC observation time `ObservedAt`.
`QueueAgeHours` uses the game's simulation clock, not elapsed wall time.

Call `request.TryDefer()` inside that callback on the same thread. A successful
call sets `IsDeferred` and stops delivery to later `Considering` subscribers.
The SDK revalidates the context before skipping native execution. The request
closes when the callback sequence ends; storing it or using an asynchronous
continuation does not extend the opportunity to defer.
Repeated calls return the same request result without rescanning the queue.
`IsDeferred` records an accepted request, not proof that the final context
recheck succeeded or that native execution was skipped.

A deferral skips only this scheduling attempt and leaves the native queue entry
and its original timestamp intact. It does not set a future execution time.
Use a bounded policy whose release deadline does not move forward on every
callback, and reset transient policy state when the world generation changes.

Deferrals are refused once the queue age reaches 1.25 game hours. Native expiry
occurs above 1.5 game hours. A clock jump can take an entry past expiry before
another scheduling attempt, even after the SDK has stopped deferring it. Pacing
does not guarantee a delay of a particular length, a retry or completion.

## Attempt observations

`Attempted` reports a supported native scheduling call that returned normally;
it is not emitted for an SDK-deferred call. Its `NpcPartyAttempt`
contains `WorldId`, `WorldGeneration`, `ObservedAt` and `Handled`.

| `Handled` | Meaning |
| --- | --- |
| `true` | Native scheduling handled the event. This includes cancellation when no target is available. |
| `false` | Native scheduling returned its retry result, leaving retry to the native queue and expiry rules. |

Neither result proves actors spawned or an encounter completed. Keep observation
handlers bounded and synchronous too.

## World lifetime

Treat `WorldId` and `WorldGeneration` together as the scope for transient policy
and observations. A saved world's identity can outlive a particular loaded
generation; a request from an earlier generation cannot control the current
world. These copied fields are context, not a durable party identifier or an
actor handle. Do not retain requests as scheduled commands. Unsubscribe handlers
when the owning mod stops.
