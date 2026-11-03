# Server ballistic data

Modstoke SDK, by OpenACAI

`ServerWorld.TryReadBallisticWeapon` copies the server's current
ballistic settings and a loaded ranged-weapon prefab's ammunition profile.
Call it on the server game thread after a world has become available. It works
without selecting a local player and does not require Modstoke Client.

The read uses the existing bullet handler, item cache, held prefab and ammunition
properties. Missing data stays unavailable: it does not initialize the database,
load assets, instantiate a weapon or substitute default constants. Dedicated
server availability of these objects still needs in-game qualification.

## Snapshot contents

- World frame, scaled frame duration and native maximum simulation step.
- Drag and gravity switches, wind, air density and current physics gravity.
- Weapon and ammunition IDs, launch speed, lifetime and initial ray distance.
- Collision mask and resolved trigger policy. `UseGlobal` is resolved from the
  current physics setting into the SDK's explicit `Ignore` or `Collide` value.
- Mass, diameter, drag coefficients and native impact-related flags.
- Ammunition muzzle damage, weapon ammunition multiplier and their product.

`RawDamage` is the initial damage input. Damage nodes, armor, receiver behavior
and other game rules can change the resulting health loss. The snapshot does
not prove that a player owns the weapon, has ammunition, aimed legally or hit
anything. It describes the loaded template, not an equipped player's live gun
or its attachments. Weapon-specific modifiers need their own authority path.

## Availability and lifetime

Only `true` with `Success` supplies a snapshot. Other statuses identify context,
missing settings/assets, invalid profiles, exhaustion or native read failures.
Weapon and frame reads share at most 32 attempts per world frame. A prefab must contain one
matching ranged-weapon component among at most 16 returned components; ambiguous
or larger results are unavailable. That component limit bounds inspection after
the native hierarchy query, not the native query's allocation or traversal.
Reentrant reads are rejected. Native failures do not provide replacement values.

The weapon's configured ammunition must resolve to the same loaded properties
as its item-cache entry. IDs, source references and world/frame lifetime are
checked during capture. Nonfinite or unusable numeric profiles are rejected.

Snapshots contain copied values and retain no weapon, projectile or settings
objects. `IsCurrentBallisticSnapshot` verifies the same world and Unity frame on
the server game thread. It does not freeze the source objects or guarantee that
another mod has not subsequently changed a value. Read again when current data
is needed; retaining a snapshot for a shot does not make its captured frame
duration a clock for later frames.

## Weapon timing

`TryReadWeaponTiming(weaponItemId, out snapshot, out status)` copies fire and
magazine settings from the same loaded held-prefab template: the controller's
fire delay and aim, two-hand and charged flags, the ammunition item ID, internal
storage and `MagazineCapacity`, and the `ReloadsSingleRound` and
`CanInterruptReload` flags. It shares the 32-read budget, the reentrancy guard
and `BallisticReadStatus`, and does not need the bullet handler. The controller
must be the one linked to the matched weapon, or the prefab's only controller
when the template leaves that link unset; anything else is `InvalidProfile`.

`FireDelaySeconds` is the minimum for the native fire accumulator, which is
checked once per frame. It is a lower bound, not the firing rate: the attack
animation cycle and controller subclasses also gate firing, and the seconds
unit is inferred. For charged weapons the charge time governs. No reload
duration is offered, because the game has no such field; reloads follow
animation and state. Attachments and runtime ammunition switching are not
reflected, and the weapon ID usually comes from client-replicated state.

## Advancing an existing projectile

`TryReadBallisticFrame` reads the current scaled frame duration, gravity, wind,
drag, material switch and trigger settings without looking up weapon assets.
A server can read one frame and share it across its active projectiles. Their
launch profiles retain the weapon and ammunition values captured for each shot.
The existing weapon snapshot properties remain available; `Environment` also
exposes the frame that was captured with that profile.

`IsCurrentBallisticFrame(frame)` checks the running world, game thread and Unity
frame. The overload taking a launch profile additionally requires that profile
to belong to the same world, while allowing its launch frame to be older.
Neither overload freezes physics or native settings. Missing frames and exhausted
collision budgets must be handled explicitly; they cannot be counted as clear
flight through unqueried space. A zero scaled duration means no flight time has
elapsed, even when wall-clock time has advanced.

## Materials and exits

`TryReadBallisticSurface(hit, launch, ...)` reads a same-frame hit's native
ballistic material. It uses the hit transform's own `BallisticObject`, falling
back to the loaded default material when that object or its material is missing.
For compound colliders, the hit transform belongs to the attached rigidbody.
It does not search parents for a different ballistic object.

The returned surface is an opaque handle. `TryReadBallisticMaterial` can read its
retained material on later frames in the same world. Changed numeric values are
copied; the handle does not select a replacement material when an object's field
changes. Destroyed objects, changed collision identities and invalid values are
reported explicitly. Finite native values are preserved without range clamps.
Profiles using `IgnoreInstigator` are currently unsupported: accurate passthrough
selection needs the projectile's actual weapon-root identity.

`TryTraceBallisticExit(currentPosition, nextPosition, surfaces, mask, triggers, ...)`
casts backward from the next position, considering at most 16 retained surfaces.
It selects the farthest strictly positive reverse hit whose native transform
matches a retained surface. The returned index identifies that surface. Distances
and query endpoints in the returned hit describe the **reverse** query.

Exit reads use a separate reusable 32-hit buffer. A full buffer returns `false`
with `BufferFull`, even if a matching hit was found: the query may have omitted
a farther exit. Only an unsaturated completed query can return `true` with `Miss`.
Material and exit reads share the collision API's 256-attempt frame budget.
They do not invoke impact callbacks, create effects or apply damage.

`TryMatchBallisticSurfacePlayer(surface, binding, ...)` matches a retained entry
surface to an exact current player binding, including on a later frame. It
revalidates the original collider, material, native attachment and canonical
player/session. It consumes one collision read and does not relax the same-frame
rule of `TryMatchHitPlayer`. For an exit, match the current exit hit separately;
the retained entry does not establish which player a new hit belongs to.

Keep the target's life captured before the entry query. A later successful native
attachment match does not authorize damage to a new life. These SDK reads do not
know a server mod's life generations or deduplicate its impact effects.

These contracts have been compiled against the installed game wrappers. Native
multiplayer behavior, dedicated-server material availability and compound-collider
traversal still need gameplay acceptance.

## Integration limits

These reads and the [collision API](SERVER_COLLISION_READS.md) are mechanisms for
server gameplay. The SDK does not run a projectile simulation, apply damage, select
permitted targets, enforce ammunition ownership or suppress native shots.
Penetration, ricochet, lifetime accounting and receiver-specific effects require
explicit integration before a server can offer authoritative combat.
