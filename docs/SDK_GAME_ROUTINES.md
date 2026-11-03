# Game Routines For Mod Developers

Use the typed `Modstoke.Sons` APIs to read game systems and request supported
operations without retaining raw IL2CPP objects. Start with a read-only snapshot;
use a mutation API only when your feature actually needs to change the game.

Reference the `Modstoke.Loader.dll` version your mod will run with.
The current project targets `net11.0`. New source APIs may not exist in an older
installed loader. Do not bundle a second Core assembly or its runtime dependencies.
The [World Conditions example](../examples/WorldConditions/README.md) provides a
native-mod manifest, project reference, menu action and shutdown cleanup.
The [development workflow](SDK_DEVELOPMENT_WORKFLOW.md) covers API discovery,
metadata-only SDK inventories and hash-matched offline native disassembly.

The C# blocks below are complete helper classes for that kind of mod project;
they are not entrypoints and do not register callbacks or start polling. These
examples describe source contracts, not verified in-game or multiplayer results.

## Find A Typed Operation

| Feature | Entry point |
| --- | --- |
| Inventory counts, ownership and equipment | `Inventory.TryCaptureItems` |
| Recipe quantities and ingredient shortages | `Crafting.TryGetRecipeRequirements` |
| Item equipment slots and hand requirements | `ItemSnapshot.Equipment` |
| Seasons and weather transitions | `EnvironmentReads.TryReadSeason`, `ServerWeather.TryReadTransition` |
| Authorized clock adjustments | `ServerTime.CheckReadiness`, `AdjustTimeOfDay` |
| Player and vehicle observations | `ServerWorld.TryReadPlayerMovement`, `TryReadGolfCartDynamics` |
| Request-scoped storage contents | `StorageRequest.InspectTargetSlot` |
| Blueprint requirements and selected construction geometry | `ServerWorld.TryReadBlueprintRequirements`, `TryResolveFreeFormTargetElement` |
| NPC registry and exact network IDs | `ServerWorld.TryReadActiveNpcsInRadius`, `TryReadEntities` |
| Companion queue preconditions | `CompanionJobs.TryReadPreconditions` |
| Loaded collision and heightmap geometry | `ServerWorld.TryOverlapCapsule`, `TryReadTerrainHeights` |
| Owned audio parameters | `AudioBanks.TryGetEventMetadata`, `TryGetPlaybackMetadata` |
| Loaded bundle paths | `Assets.TryInspectLoadedAssetBundle` |
| Managed coroutine outcomes | `Coroutines.TryStartObserved` |
| Existing game managers | `ManagerAccess.TryGetManager<T>` |
| Current and next equipment slots | `Inventory.TryCaptureEquipmentSlots` |
| Stow the current local slot's item | `Inventory.TryStowEquipmentSlot` |
| Live crafting-mat ingredients | `Crafting.TryReadCraftingMat` |
| Separate craft collection and module-storage submission | `ModuleStorageContainer.CollectAndDepositWithReceipt` |
| Multi-point lock input and before/after observations | `Building.LockCurrentPlacementPoint` with `PlacementPointReceipt` |
| Paged recipes by ingredient or result | `Crafting.TryQueryRecipesForIngredient`, `TryQueryRecipesForResult` |
| Weapon/ammunition definition relationships | `Items.CaptureAmmunition` |
| Stored ambient-temperature fields | `EnvironmentReads.TryReadTemperature` |
| Local cached survival stats | `LocalPlayerReads.TryReadVitals` |
| Vanilla walk, run, swim and crouch settings as the local rig woke | `LocalPlayerReads.TryReadMovementDefaults` |
| Replicated animation, clothing and armour | `ServerWorld.TryReadPlayerAnimation` |
| Physical golf-cart connection slots | `ServerWorld.TryReadGolfCartOccupants` |
| Request-scoped storage contents | `StorageRequest.InspectTargetContents` |
| Direct construction links and direction lists | `ServerWorld.TryReadFreeFormStructureLinks` |
| Exact NPC state, action and target observations | `ServerWorld.TryReadNpcState` |
| Companion running-order and target details | `CompanionJobs.TryReadCurrentOrder` |
| Heightmap normals and slopes | `ServerWorld.TryReadTerrainSurface`, `TryReadTerrainSurfaces` |
| Bounded sphere sweeps | `ServerWorld.TrySweepSphere` |
| Capsule and oriented-box sweeps | `ServerWorld.TrySweepCapsule`, `TrySweepBox` |
| Oriented-box overlap snapshots | `ServerWorld.TryOverlapBox` |
| Owned audio playback phase and timeline | `AudioBanks.TryGetPlaybackState` |
| Exact field and property declarations | `FieldInspection.TryInspectField`, `TryInspectProperty` |

These are on-demand operations, not background monitors. Observe the individual
API's thread, authority, lifetime and partial-result contract. Manager access
returns a borrowed native component; use it immediately and reacquire it after
world changes. Coroutine completion covers managed iterators, not arbitrary
native yields or external Unity stop calls.

Stow returns copied before/after slot observations. A true result means an empty
slot was observed, not that animation or multiplayer replication finished. Inspect
`InvocationAttempted` before handling failure; never automatically retry an attempted
call. Items that require dropping use a separate workflow.

Craft collection and module deposit are separate operations. The combined receipt
retains collection evidence even when deposit is refused or unconfirmed. A submitted
deposit can remain pending; inspect its operation for later custody. Do not retry,
refund or replace an attempted transfer automatically.

The placement receipt observes one native lock-input call. Counts include the live
cursor, and a missing count is not zero. Post-call counts require unchanged world,
system, recipe and operation observations. Neither a returned input nor a changed
count proves that the handler accepted a point or built a segment.

Box overlaps use positive half-extents and normalize a finite, nonzero orientation.
`BufferFull` returns a possibly truncated snapshot, not clearance. The snapshot has
no mutation authority and cannot prove that placement is safe.

The equipment capture preserves the native lock predicate as `NativeSlotLockResult`.
Some game builds implement `IsSlotLocked` as a constant-false shared stub; the
result does not establish slot lock state or equip readiness. Current and next
item IDs remain separate observations. Likewise, a method named as a getter can
initialize caches or create native objects. Inspect its body and callees before
treating an unfamiliar game routine as a side-effect-free read.

`TryReadCraftingMat` copies at most 256 existing ingredient rows without refreshing
recipes or invoking crafting callbacks. Preserve its raw counts, duplicates and
`Truncated` flag; mat contents alone do not establish craftability. Paged recipe
queries copy complete metadata for confirmed matches and report unreadable or
over-limit recipes separately. Page indices are temporary list positions, not
stable recipe identities.

Sphere sweeps share the existing collision budget and return unordered hits.
`false` with `BufferFull` includes a possibly truncated snapshot; other failures
do not mean a miss. Hit distance is sphere-center travel, not origin-to-contact
distance. Terrain normals cover loaded, translation-only heightmaps, not caves,
meshes, walkability or collision coverage. These helpers do not change physics
settings or schedule background queries.

Construction link inspection copies raw slots and evaluates the structure's native
`Supporting` and `SupportedBy` getters. Those getters dispatch virtually: inspection
of the base implementation does not establish side-effect-free behavior for every
derived type or class initializer. The SDK invokes no graph setters, but this read
is not a blanket purity guarantee for native code or other mods' patches.

## Choose The Right Context

- Local reads, such as `EnvironmentReads` and
  `Inventory`, observe this process. A dedicated server does not have
  a local player's inventory. Environment availability is checked separately.
- `ServerWorld` reads the active server world. Its player reads take
  an exact `ServerSession`; replicated fields are observations, not
  independently authoritative health, inventory or permission records.
- Server operations such as `ServerTime.SetTimeOfDay` check native
  role and readiness. They do not decide whether your mod's caller is authorized.

Loader startup readiness is not game-system readiness. Check the particular
operation's return value and status even when a capability or `HooksInstalled`
flag is true. Do not interpret a missing API, disabled path or unavailable
dependency as permission to invoke a similarly named native method by reflection.

## Read On The Game Thread

Call live game reads from your mod's supported Unity-thread callback, such as a
menu command. These methods do not marshal work to the game thread. `WrongThread`
means the call was rejected, not queued. A timer, `Task.Run`, or continuation after
`await` must not assume it is on that thread.

Capture once for a user action, keep the managed values, and render from those
values. The World Conditions example follows this pattern. Its refresh action
reads clock and weather independently and retains each result's own status.
If you retain an older successful result after a failure, label it as stale with
its observation time; do not present it as the latest state.

### Observe A Season

```csharp
using Modstoke.Sons;

public static class SeasonReadExample
{
    public static string Read()
    {
        if (!EnvironmentReads.TryReadSeason(
                out var season, out var status))
            return $"Season unavailable: {status}";

        string active = season.ActiveSeason?.ToString()
            ?? $"Unknown ({season.RawActiveSeason})";
        return $"Season: {active}; locked: {season.IsSeasonLocked}; "
            + $"observed: {season.ObservedAt:O}";
    }
}
```

`PreviousSeason` is a stored field, not a transition history.
`IsSeasonSwitchInstant` is a policy flag, not transition progress or completion.
Neither a season nor weather snapshot proves visible effects or agreement with
another player. `TryReadClock(out clock, out status)` similarly distinguishes
`GameTime` from `EffectiveTime` when the native override is active.

### Capture Requested Inventory Items

```csharp
using System;
using System.Collections.Generic;
using Modstoke.Sons;

public static class InventoryReadExample
{
    public static string Read(IReadOnlyList<int> itemIds)
    {
        if (!Inventory.TryCaptureItems(
                itemIds, out var items, out var status,
                includeEquipped: true, checkNextSlot: false))
            return $"Inventory unavailable: {status}";

        if (items.Count == 0)
            return "No item IDs requested.";

        var lines = new List<string>();
        foreach (var item in items)
        {
            lines.Add(item.ReadSucceeded
                ? $"Item {item.ItemId}: count {item.Count}; "
                    + $"owned {item.OwnsItem}; equipped {item.Equipped}"
                : $"Item {item.ItemId}: unavailable ({item.Status})");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
```

Supply the positive game item IDs your feature needs. `MaxCaptureItems` is 256,
including duplicates; larger requests fail without truncation. Input order and
duplicates are preserved, with each distinct positive ID read once. This is not
an enumeration of the entire inventory or an atomic transaction.

`false` means the whole capture failed. `true` means results are available, but
each entry still needs `ReadSucceeded`: invalid IDs and failed native reads have
null values, not zero counts or `false` predicates. Count options affect only the
count query; ownership and equipment are independent native predicates. For a
single count, `TryGetItemCount(itemId, out count, out status)` remains available;
its default `count` on failure does not mean the player owns zero items.

## Keep Identity And Lifetime Separate

Obtain remote session handles from `ServerSessions.Sessions` or its
`Opened` event. The list and `IsCurrent` describe last-observed membership, not
real-time liveness. Events are queued, so a handle can already be closed when its
callback runs. Pass the original handle to the SDK, which revalidates it; do not
substitute a connection number, Steam ID or network entity ID for its lifetime.

```csharp
using Modstoke.Sons;

public static class PlayerReadExample
{
    public static string Read(ServerSession session)
    {
        if (!ServerWorld.TryReadPlayerState(
                session, out var player, out var status))
            return $"Player state unavailable: {status}";

        string life = player.LifeState?.ToString()
            ?? $"Unknown ({player.RawDyingState})";
        return $"Entity {player.NetworkId}: replicated life {life}; "
            + $"right-hand item {player.RightHandHeldItemId}; "
            + $"observed: {player.ObservedAt:O}";
    }
}
```

`SessionId` is a correlation value, not an authentication token.
`RegisteredSteamId` is game registration metadata, not permission. The held-item
fields above are replicated presentation data, not the player's full inventory.

For work spanning callbacks on one player attachment, capture with
`TryCapturePlayerBinding(session, out binding, out status)` and subsequently use
`TryReadPlayerBinding(binding, out snapshot, out status)` on
`ServerWorld`. A replaced attachment retires the binding; a later
attachment is a new target even if an ID is reused. A binding is not a
health/respawn-generation token. The separate `TryCaptureLocalPlayerBinding`
path is for the actual listen-host player, not a guessed remote session.

Do not keep raw Unity/IL2CPP wrappers across scene or player replacement. A
managed reference can outlive its native object. `GameRuntime.IsInWorld` is not
an object-lifetime token. Remove your event subscriptions and registrations when
your mod stops, and dispose SDK observer handles you own when no longer needed.

## Preserve Unknown And Partial Results

| Signal | What the mod should preserve |
| --- | --- |
| A `Try...` call returns `false` | Failure or unavailability, not an empty inventory, clear weather or absent entity. |
| A successful read returns zero or `false` | A valid observation for that query, distinct from failure. |
| A nullable enum is `null` | An unrecognized value; display or retain the accompanying raw value instead of choosing a default. |
| A collection reports truncation, skipped entries or an incomplete scan | Partial coverage; an omitted entry is not proof of absence. Check that API's coverage fields. |
| `ObservedAt` | Read completion time, not simulation time, replication freshness or a last-change timestamp. |
| `HooksInstalled` or a readiness flag | Availability evidence for that subsystem, not a guarantee that the next operation can succeed. |

Not every API uses the same result shape. Some return a Boolean plus diagnostic
text, others use typed statuses or per-entry results. Handle the declared result
and any coverage fields together. Do not parse human-readable status text as a
stable error code, or convert unknown enum values into a success case.

## Request Changes Explicitly

Authorize the initiating action in your server-side policy before making a
world-changing call. UI visibility and confirmation dialogs are not authority
checks. A server role check inside the SDK does not authenticate a remote caller.

For example, an authorized Unity-thread command can call
`ServerTime.SetTimeOfDay(12, 0, 0)`. It preserves the current day and
normal clock speed, respects native locks and transitions, and requires the
active native server replication path. It changes the clock, not simulated hours
of gameplay.

`ServerTime.CheckReadiness()` can inform an explicit control's state.
`Ready` reserves nothing; the mutation checks again and its result must be handled
independently. `ServerTime.AdjustTimeOfDay(900)` requests a 15-minute
adjustment, wrapping within the same day rather than advancing the day count.
The supported offset is -86399 through 86399 seconds; zero returns `NoChange`
after readiness checks without calling the native setter.

`Applied` means the local native setter returned true, not that every receiver
ran or every client synchronized. `ApplyUncertain` means execution began and an
exception may have left some state changed: do not blindly retry or assume
rollback. `Locked`, `Busy`, `NotServer` and `ReplicationUnavailable` are reasons
to defer or decline the operation, not to clear locks or bypass the native path.
Other request APIs define their own acceptance and completion statuses; a
successful read is never a mutation preflight for a different API.

See the [SDK reference](sdk.md) for the broader API surface and the
[public platform contracts](mod-platform.md) for server sessions,
policy callbacks and request-specific delivery limits.

## Inspect A Routine Before Extending It

`RoutineInspection.TryInspect` resolves an exact managed declaration
without invoking it, running its wrapper initializer or reading native memory.
It distinguishes generated method mappings, known failed-unstripping stubs,
other managed bodies and unavailable bodies. It does not search base classes,
coerce parameters or choose a similarly named overload.

```csharp
using System;
using Modstoke.Sons;
using TheForest.Items.Inventory;

public static class RoutineReadExample
{
    public static string InspectOwns()
    {
        if (!RoutineInspection.TryInspect(typeof(PlayerInventory),
                "Owns", false, typeof(bool), new[] { typeof(int) },
                out var declaration, out var status))
            return $"Declaration unavailable: {status}";
        return $"Body: {declaration.BodyKind}; module: {declaration.ModuleVersionId}";
    }
}
```

A successful inspection is not permission or proof that the native routine
exists. IL2CPP can supply a nonzero placeholder for a missing method, and a
generated mapping is not a unique or safe detour target. The reported managed
metadata token is not the original native token. Native extensions still need
current-build body evidence, exact identity qualification, receiver lifetime
checks and the game's authority rules. Prefer the typed SDK operation when one
already covers the feature.

For offline investigation, `tools/Export-SotfRoutineIndex.ps1` reads the installed
`GameAssembly.dll` and IL2CPP metadata without launching the game. For example:

```powershell
./tools/Export-SotfRoutineIndex.ps1 -OutputPath ./artifacts/sons-routines.json
./tools/Export-SotfRoutineIndex.ps1 -OutputPath ./artifacts/inventory-routines.json -TypeFullName Sons.Inventory.ItemInstanceManager
```

The JSON records the SHA-256 of both input files, Unity version, original IL2CPP
method token, declaring assembly and type, signature, analysis-image virtual
address, RVA and raw file offset when mapped. The virtual address is not a
live process pointer. `entryPrefixSha256` covers at most the first 32 raw
bytes at that entry, not the whole method. The address may be shared by other
declarations; `entryDeclarationCount` reports the number of definitions at that
entry found by LibCpp2IL across all loaded metadata. A count of one is not a
hook-safety verdict. Unmapped methods remain in the index as unmapped; a record does
not prove a callable method, safe hook, gameplay behavior or authority. Pass
`-ExpectedGameAssemblySha256` and `-ExpectedMetadataSha256` when comparing
against a pinned build. The tool refuses to replace an existing output file.

After a game update, export a new index with the same filters and run
`tools/Compare-SotfRoutineIndexes.ps1` with `-BeforePath <old.json>`,
`-AfterPath <new.json>` and `-OutputPath <changes.json>`. It reports added or
removed qualified signatures, token changes, mapping changes, relocated entries
and changed entry prefixes or alias counts.
An unchanged prefix is not proof that the rest of a native body or its behavior
is unchanged; review the current body before carrying forward a native hook.
