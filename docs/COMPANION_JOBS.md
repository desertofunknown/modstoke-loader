# Companion job queues

`Modstoke.Sons.CompanionJobs` provides transient server queues for
Kelvin's enabled native **Get** orders. The game still performs order admission,
action selection, rest, interruption and resource handling. A receipt describes
observed order state; it never proves successful work or resource delivery.

This is a separate API from the existing [remote request protection and activity
observations](sdk.md#remote-companion-requests). Those APIs and their
contracts remain unchanged.

## Opening and using a queue

Use the API on the game thread while Bolt is running as the server and
`State.Status` is `Installed`. Installation requires safe native patch support and
the companion observation hooks. Hook registration alone does not establish
working dispatch or multiplayer acceptance.

| API | Behavior |
| --- | --- |
| `TryListCompanions` | Lists active, live, uninjured, server-owned Kelvin actors by network ID; dormant actors are not included. |
| `TryGetOrders` | Copies enabled native Get options with `Index`, `Name` and `RestartsAfterRest`. It does not run native eligibility predicates. |
| `TryOpen` | Binds a queue to a current `PlayerHandle`, world and companion lifetime. It refuses an existing native order, override or manual interaction. |
| `TryEnqueue` | Adds an order index, drop mode and authorization callback; returns a job GUID. |
| `TryRead` | Copies receipts, revalidating the current context. A closed handle retains its final copied receipts. |
| `CancelPending` | Cancels only an entry still marked `Queued`. |
| `Close` | Retires the queue and cancels waiting entries. Submitted native work continues independently. |

Drop modes are `DropHere` (0), `GiveToMe` (1), `FollowMe` (2), `FillHolder` (3)
and `FillLogSled` (4). `FollowMe` here is a Get-order destination mode, not a
general Follow-order queue. Select indices from the current options; native order
data is checked again before submission.

Keep the queue handle itself. Its managed object reference identifies the queue;
`NetworkId` is an observed companion ID, not a durable queue identity. Job GUIDs
identify entries only within this transient history. Do not save or reconstruct
handles from IDs. The SDK accepts current remote player bindings as
well as local host bindings; it has no intrinsic local-player requirement.

Authorization callbacks run synchronously on the game thread at enqueue, during
lifetime maintenance and before submission. Keep them bounded and free of native
actions. They must check the caller's current policy, not merely return a stored
admission decision. Denial or an exception at enqueue rejects that entry. During
maintenance or dispatch, failed authorization retires the queue without
cancelling submitted native work.

There is one queue owner per companion and at most four open SDK queues per
server. Each queue allows eight waiting entries and retains at most 32 receipts;
old terminal receipts are removed to make space. Waiting entries expire after
one hour measured from enqueue. This expiry does not time out submitted or
resting work. Queues and receipts are not persisted to world saves.

## Native scheduling and receipts

Submission waits for the companion's native idle boundary inside its normal
actor tick, after an inactive `UpdateActiveAction` result. Nested ticks, action
execution and action-selection calls are excluded. The scheduler requires
no active order, override, action, thought, group or sequence, and inactive order
completion stimuli. It does not change that native return value or suppress the
game's subsequent action selection and interruption. It submits through native
`ServerGiveOrders` and leaves eligibility and execution to the game.

| Receipt status | Meaning |
| --- | --- |
| `Queued` | Waiting; no native request has been submitted. |
| `Submitted` | A native request was submitted; execution and delivery are not confirmed. |
| `Resting` | Native rest/restart retained the same tracked order. |
| `NativeEnded` | Completion returned with restart disabled and no remaining active order. Success and delivery remain unknown. |
| `Refused` | Native refusal returned; delivery is not established. |
| `Rejected` | The selected native order data changed before submission. |
| `Cancelled` | A waiting entry was removed before submission. |
| `ContextLost` | Queue ownership or its bound context ended; submitted work was not cancelled. |
| `Uncertain` | Submission failed or returned without the expected order; effects are unknown and the queue retires. |
| `Expired` | A waiting entry reached its one-hour limit without submission. |

Native rest/restart may retain a job indefinitely. A successor waits while that
job remains active, and can expire while waiting. A completion observation with
`RestartAfterRest == false` is only `NativeEnded` when the after-state has no
active order. Missing or unexpected after-state retires the queue instead of
assuming success. No receipt automatically advances objectives, grants economy
rewards or confirms resource delivery.

Removing or closing a queue never stops a running or resting native order.
Closed handles keep copied receipts, but cannot submit more work. An explicit
close marks waiting entries `Cancelled` and other unfinished entries
`ContextLost`; already terminal receipts retain their status.

## Lifetime boundaries

A queue retains the exact player binding, current world context, companion
entity attachment, Robby component and actor. It retires on stale player/world
context, disconnect attempts, authority loss, network restart/shutdown, actor or
entity disable/destruction, actor activation/death, companion destruction or
injury-state changes. Manual order-pad interaction, native/manual replacement
orders and unexpected tracked-order changes also retire it. Retirement releases
the queue's ownership; it does not undo game effects or resume the queue later.

## Acceptance status

These contracts describe the implementation source. Build verification is tracked
with the consuming change; this guide does not claim a completed build or runtime
qualification. In-game acceptance remains pending, including
native dispatch, rest/restart, retirement and receipt display. Dedicated-server
and remote-player dispatch also require runtime qualification. Admin currently
exposes only a listen-host command, not a dedicated-server command.
