# Mod Development Workflow

Start with a small, typed SDK operation and an explicit user action. Keep game
reads separate from presentation, and investigate native code only when the
existing API does not cover the feature. The [game routines guide](SDK_GAME_ROUTINES.md)
contains API examples and detailed result contracts; this page describes how
to put them to work.

## Start From The Example

Use the [World Conditions example](../examples/WorldConditions/README.md) as the
starting point for a native mod. Its [manifest](../examples/WorldConditions/openacai.mod.json)
names the entrypoint and shutdown callback;
[Mod.cs](../examples/WorldConditions/Mod.cs) registers and removes providers;
[WorldConditionsProvider.cs](../examples/WorldConditions/WorldConditionsProvider.cs)
captures clock and weather on Refresh without background polling.

Give your mod its own assembly, manifest identity and provider IDs. Reference the
exact `Modstoke.Loader.dll` release it will run with, using the
example project's `LoaderAssemblyPath` property. The project targets `net11.0` and
sets `Private="false"` on that reference. Do not ship another copy of Core or its
runtime dependencies, and do not assume a new source API exists in an older
installed loader.

Run the offline commands below from the repository root in PowerShell 7.2 or
later. Set `$coreDll` to that Core DLL, `$gameRoot` to your game directory, and
`$researchRoot` to a local output directory outside the repository. Exports
refuse to overwrite existing files; choose a new output name for each capture.

## Choose One Typed Operation

Find the closest entrypoint in the [SDK reference](sdk.md), then read
its declaration and implementation under `src/Modstoke.Loader`.
Check the argument limits, failure statuses, copied fields and required context
before adding a callback.

To inspect what your chosen Core DLL actually declares, use
[Export-ModstokeSdkApi.ps1](../tools/Export-ModstokeSdkApi.ps1):

```powershell
./tools/Export-ModstokeSdkApi.ps1 -CoreDllPath $coreDll -OutputPath "$researchRoot/sdk-api.json"
```

This reads metadata without loading Core or its dependencies. The JSON inventories
declared public types, methods, properties and fields in `Modstoke.Sons`, with
the DLL hash and module identity. It does not expand inherited members or event
declarations. Signatures use metadata notation, not copy-ready C#, and do not
reconstruct nullable annotations. Check unavailable signatures and the source
contract; a declaration is not runtime readiness or proof of native behavior.

For an item-count panel, for example:

1. Start with the example's command/provider structure and supply only the item
   IDs your feature needs to `Inventory.TryCaptureItems`.
2. On Refresh, preserve the overall result and each entry's `ReadSucceeded` and
   `Status`. An unavailable count is not zero; a successful batch can still
   contain unavailable entries.
3. Build the display from the managed results. Do not enumerate the whole scene
   or add reflection polling to fill gaps in the response.

Choose local reads for the current process and server-world APIs for the current
server context. Replicated player fields are observations, not authenticated
permissions or a complete inventory. For mutations, enforce your mod's caller
permissions as well as the operation's native authority and readiness checks.

`Inventory.TryDropStoredItem` transfers one exact, uniquely stored instance from
the offline player or host into an ordinary native pickup. It requires qualified
inventory hooks, idle equipment and a supported item/module profile. It is not a
remote-player command or held-item throw.

For plain count-based storage, use `Inventory.TryDropCountedItem(itemId, ...)`.
It transfers one item, including the last item in a stack, while retaining the
native storage bucket and removal callbacks. The item must not be held, have
per-instance module features, or use a specialized pickup. The pickup must yield
exactly one item and have no weapon-module or deferred-renderable components.
Module-storage containers and unique items use their own APIs.

Check `ItemDropResult` even when the call returns false. Debit and spawn have
separate attempted/observed fields; failure can occur after either step. The
routes preserve native removal notifications and quick-select cleanup, block
the item-ID fallback, and check the output's exact instance. They do not roll
back, retry, confirm remote delivery or save the world. `AfterCount` is unavailable
when the last native phase throws or its inventory identity cannot be revalidated.

## Capture Once, Keep The Meaning

Run live game reads on the initialized game thread through a supported callback,
such as the example's menu command. A `WrongThread` result rejects the call; it
does not schedule it. `Task.Run`, timers and continuations after `await` must not
assume they are on the game thread.

Keep status and observation time with each copied snapshot. Preserve unknown
enum values, nullable fields, truncation and incomplete coverage. Failure does
not mean an empty world, and omission from a bounded result does not prove
absence. If you keep a previous successful result, label it as stale after a
failed refresh. `ObservedAt` is not a replication freshness guarantee.

Managed snapshots are distinct from borrowed Unity/IL2CPP objects. For example,
`ManagerAccess.TryGetManager<T>` returns a borrowed component, not a
detached snapshot. Use it immediately on the game thread and reacquire it after
world changes. Keep exact SDK session/binding handles where an API requires
them; do not substitute an ID or reuse a native wrapper across object replacement.
Unsubscribe events and release owned registrations and observers at shutdown.

## Inspect Declarations Before Native Code

Use `RoutineInspection.TryInspect` for a specific managed declaration:
declaring type, name, staticness, return type and ordered parameter types. It
does not select an approximate overload or search base classes. Read `BodyKind`
and the inspection status; a unique declaration may still be an unsupported
unstripping stub or have no available managed body.

Inspection does not invoke the declaration, initialize its wrapper type or read
the native method pointer. The reported managed token and module identity belong
to the inspected managed assembly, not the original IL2CPP metadata. Wrapper
signatures and fields describe the access surface, not the game's native
implementation.

### Startup And Cache Costs

A current interop cache still passes content and metadata validation before use.
Changing generator inputs or the Preloader build can require regeneration; disabling
updates is not a performance fix. A missing cache skips the initial comparison hash,
but generation still hashes inputs before and after conversion and validates output
before promotion.

The preloader state records cache-validation, input-hashing, conversion and
output-validation milliseconds under `generation`. Its elapsed total also includes
library preparation, recovery and promotion; these fields are not game load times.
Assembly-preload timing is logged separately. Compare cached starts separately from
generation runs before attributing a delay to either phase.

Metadata validation uses at most four independent readers and keeps original
exception types for cache recovery. Generation member indexes preserve overload
order; shared metadata rewriting remains serial. These changes do not skip hashes,
native qualification or required startup hooks.

On Windows 11 24H2 or later, the owned console requests a non-activating window.
Older systems retain console allocation with conditional focus restoration. New
owned consoles disable Quick Edit selection mode; inherited consoles are unchanged.
Automatic failure paths honor their compatibility layer's console-enabled setting,
while disk logging and explicit console creation remain available.

### Unity Restoration Limits

The preloader's restoration report separates existing wrappers, translated
managed bodies, native-call bridges and unsupported methods. A translated body
can still depend on an unavailable restored method; `managed-body-partial`
records that dependency without deleting working conditional paths. Unresolved
dependency references and native-call availability still need investigation.
The report records generation and restoration completion separately. If generation
fails before restoration starts, zero considered methods is not a coverage result;
startup may still use the previously validated cache. The console summary states
these outcomes explicitly.

Restoration preserves typed vector operations, constructed generic member
bindings, literal fields and bounded integral/enum initializers from embedded
data. Missing object-root managed helpers can retain real CLR fields,
constructors and static initialization when their source bodies are available.
They do not acquire a fabricated IL2CPP identity. The report's `representation`
distinguishes managed-only storage from native wrappers and value layouts;
native calls reject managed-only storage.

Object-root generic helpers and interface-free generic value helpers preserve
their CLR parameters, constraints, fields and initialization. Calls must prove
the rewritten constraints; native wrappers are not CLR interface contracts.
Managed helper factories use CLR construction. Native generic method caches
carry both declaring-type and method arguments and check native class/method
availability. Boxing and unboxing distinguish value copies from reference casts,
with runtime native-layout checks rather than inferred identity.
Generic vector allocation selects native value or reference storage from the
closed element class. Generic and wrapped-value stores use native `SetValue`,
including its GC barrier, after type and bounds checks. Floating-point vector
accesses also check native-sized indices before narrowing. Generic defaults
preserve ordinary CLR defaults and allocate a zeroed native value when a native
struct is represented by a wrapper; they do not call its constructor.

Canonical `ldftn`/`ldvirtftn` delegate factories create a CLR `Action`/`Func`
before converting through the pinned native delegate bridge. Closed callback
signatures, native return/parameter identities and supported value layouts are
checked. The native target retains its CLR callback; equivalent live callbacks
reuse identity for removal. The descriptor supports up to 16 parameters only
where the native delegate definition exists. These factories still reject
escaped pointers, byref signatures, unproven virtual bindings and missing
native/AOT types.

The converted-delegate bridge has native exception propagation for explicit
target families on the pinned Windows x64 player: `void()`, `void(string)`,
`bool(object)`, parameterless scalar returns,
`TResult(T)` with primitive value arguments and returns, and
`void(T)`, `void(T1, T2)`/`bool(T1, T2)` and
`void(T1, T2, T3)`/`bool(T1, T2, T3)` with primitive value arguments.
Scalar returns cover Boolean, signed/unsigned 8-, 16-, 32- and 64-bit integers,
Char, IntPtr/UIntPtr, Single and Double. The same types are supported as the value
arguments, including mixed integer/floating-point pairs and triples; by-reference,
optional and direction-attributed arguments are excluded from that path. Native type
identity must match even when types share a calling convention. Char arguments
and returns use an explicit 16-bit representation. Generated conversion helpers
may impose additional signature restrictions.

CLR enums backed by signed or unsigned 8-, 16-, 32- or 64-bit integers use these
same scalar families, including mixed reference signatures and single writable
ref/out arguments. The exact enum class remains in the signature; its native
enum flag, underlying type and payload size must match. Values are not filtered
against declared constants, so flags and unnamed values retain their bits.
These rules do not create missing native/AOT enum types.

String and native reference-wrapper arguments also support void and Boolean
returns: a single argument, a pair with any listed scalar or reference, or three
arguments combining those types in any order. Native reflection passes references
directly and reads scalar arguments from typed storage. Optional,
direction-attributed, byref and native-value arguments are excluded from this route.
SDK command callbacks
still catch exceptions, report the first failure once and return false; native
propagation does not bypass that boundary. These selector extensions are compiled,
but native runtime acceptance remains pending.

The separate reference-result route takes one String or native reference-wrapper
argument and returns String or a native reference wrapper. Arguments retain exact
native identity; reference return covariance must be valid in the native type
hierarchy. Non-null results are rooted before leaving managed code and released
when the native callback bridge finishes; subsequent ownership follows the native
caller's normal rules.
Null remains a valid result and needs no return handle. Native value payloads,
pointer returns and escaping byrefs are not part of this route.

One writable `ref`/`out` argument is also supported with a void or Boolean return
for those primitives, integer-backed enums, String and closed native
reference-wrapper types. Type and ref/out direction must match exactly;
native-value proxies, nonprimitive structs, optional parameters and `in` contracts
are outside that route. Use a named CLR delegate with the required ref/out
signature for explicit conversion.
Primitive references alias caller storage; converted references retain the
existing rooted writeback and GC-barrier behavior. Writes made before a callback
throws are not rolled back. Character references use UTF-16 storage, and null
storage is rejected before invoking the managed callback. An out enum is not
zeroed on callback failure; assignments completed before a throw remain visible.
This is call-scoped reference support, not permission to retain addresses after return.
Direct calls and native reflection invokers raise captured failures only after
managed callbacks have returned; matching managed/native components are required.
Other signatures retain their contained fallback. This does not establish
universal exception equivalence, escaping references, aggregate returns or
exception propagation for every injected method and hook.
Reference parameter contravariance and return covariance are accepted only when
the emitted CLR inheritance chain proves the binding. A static callback may bind
one leading reference argument, including a proven reference null. Runtime
signature adapters retain the original multicast delegate and removal identity;
value conversions, native interface adaptation and arbitrary generic variance
are not implied.

Field/vector addresses support immediate accessor lowering and bounded
method-local reference flows. Rooted location descriptors preserve owners and
indices through ref locals, duplication, branches and repeated scalar accesses;
blittable location-to-location copies and readonly reads retain those contracts.
Mapped non-generic native calls can consume mutable, reference-free instance-field
or array value locations. Rectangular elements retain a checked row-major index,
rank and original array owner. Nested value fields retain their root,
exact field chain and readonly ancestry, including initonly children. Readonly
locations support value loads, matching indirect loads, field reads and the
source side of value copies. They cannot become write destinations or native
receiver/ref arguments, including after local assignments or branch merges.
These descriptor forms require nongeneric,
reference-free matching native/CLR value layouts. Calls pin their real owners, preserve aliases and
mutations made before an exception, and release every pin in `finally`. They do
not export raw pointers or substitute copyback. Metadata lookup is cached by
exact generated type and field. Native calls through readonly/static locations,
mixed CLR/native byrefs, generic calls, reference-containing slots, atomics, mixed-location schemas
and external returned/stored references remain unsupported. Constrained calls support proven CLR
byrefs to native reference receivers, not mutable native value receivers.

Concrete native rectangular arrays of rank 1 through 32 use generated wrappers
with explicit element/rank identity, bounds, constructors and Get/Set.
Primitive, enum, native value, reference and string elements are supported,
including checked reference covariance and bounded rank-one nonvectors. Fully
closed native generic elements also have shape-specific wrappers. Their complete
element identities select a helper owner that already depends on the required
assemblies; corlib does not gain a reverse reference to game assemblies. Native
class availability and layout are checked when the closed wrapper is used.
Element access currently uses native index vectors and boxing; this is not a
fast per-cell processing API. Open generic-dependent elements, nullable values,
nested arrays and externally escaping element addresses remain unsupported. Existing
native invocation signatures for unsupported shapes stay opaque and emit a
diagnostic; they are not counted as indexed array support. Native boxed-value
interior references still require a separate owner and lifetime contract.

Newly generated calls retain the original GC-rooted native exception. Native
typed catches use IL2CPP assignability and expose a wrapper for the same object;
rethrow retains the carrier. Discard-only base catches preserve CLR catch-all
behavior. Typed/consuming native catches do not match CLR infrastructure
exceptions. Runtime `Il2CppException` carriers also participate when the loaded
runtime exposes its versioned retained-wrapper contract. The bridge binds that
nonvirtual accessor once; it does not inspect arbitrary exception subclasses or
reconstruct pointers from messages. Carriers without that contract remain
explicitly refused at handler entry. Source-authored filters, generic catch
types constrained to a native exception hierarchy, and compatible shared catches
have explicit lowering. Adjacent clauses can share a filter. Separated clauses
use private handler copies in the original search order, with internal branches
and switches remapped to each copy. Source predicates remain in CLR filters on
the first pass; handlers receive the same converted native wrapper after unwind.
Copying requires a proven common entry type and no fallthrough or external entry.
Complete nested catch, filter, finally and fault clauses are copied with their
private conversion locals and original clause order. Branches retain their
destinations; exclusive ends stop at the matching copy's boundary. Source and
lowered regions are checked against the
[CLI exception rules](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
(I.12.4.2.7-8). Crossing or enclosing regions, exception clauses inside filters,
nested nonadjacent shared families, mixed CLR/native shared entries and shared
filter bodies remain rejected. This path is compiled but still requires
generated-IL and native-runtime validation.

Generated caches call value, reference, delegate and exception helpers in the producing
Preloader assembly.
Deploy the matching Preloader and regenerated cache together, never a new cache
with an older Preloader. Missing native implementations, native identities,
unproven generic/virtual/delegate contracts and rejected method dependencies still prevent
complete Unity coverage. Regenerate into an isolated cache before judging
coverage changes. Compilation alone does not establish that generated IL can
be loaded or executed correctly.

## Inspect A Pinned Native Routine

First retain an index for the exact game binary and metadata pair.
[Export-SotfRoutineIndex.ps1](../tools/Export-SotfRoutineIndex.ps1) reads those
files offline. Limit the export to the type you are investigating:

```powershell
$indexPath = Join-Path $researchRoot 'item-instance-index.json'
./tools/Export-SotfRoutineIndex.ps1 -GameRoot $gameRoot -OutputPath $indexPath -TypeFullName Sons.Inventory.ItemInstanceManager
```

Keep the recorded SHA-256 hashes of both `GameAssembly.dll` and
`global-metadata.dat`, plus the Unity version, with your findings. When a study
must stay on that build, supply `-ExpectedGameAssemblySha256` and
`-ExpectedMetadataSha256` using the retained hashes. A filename or game version
label alone is not a binary pin.

Search that saved index with [Find-SotfRoutine.ps1](../tools/Find-SotfRoutine.ps1):

```powershell
$found = ./tools/Find-SotfRoutine.ps1 -IndexPath $indexPath `
  -TypeFullName Sons.Inventory.ItemInstanceManager -MethodName Create
$found.methods | Format-List assembly, type, name, signature
```

Filters are literal, case-sensitive and combined. Narrow with `-AssemblyName`
and `-Signature`; the latter matches the full stored text, including parameter
names. Do not take the first name match as an overload resolution. Check
`sourceTruncated` and `queryTruncated`; absence applies only to the exported
filters and records. The finder reports stored hashes but does not check your
current installation.

The native index identifies original IL2CPP declarations by assembly and token;
do not join it to wrapper metadata by token alone. Match the declaring type and
full signature, including overload details. Image virtual addresses, RVAs and
file offsets are not interchangeable or live process pointers. Shared entries,
unmapped methods and short entry-prefix hashes are evidence with limits, not
proof of a unique, callable or hook-safe native body.

Use [Export-SotfNativeRoutine.ps1](../tools/Export-SotfNativeRoutine.ps1) for a
bounded disassembly. With `$routine` set to the exact selected record from
`$found.methods`:

```powershell
./tools/Export-SotfNativeRoutine.ps1 -GameRoot $gameRoot -IndexPath $indexPath `
  -AssemblyName $routine.assembly -TypeFullName $routine.type `
  -MethodName $routine.name -Signature $routine.signature `
  -OutputPath "$researchRoot/selected-native.json" -MaxBytes 512
```

The exporter requires an untruncated index and one exact mapped declaration. It
verifies the current `GameAssembly.dll` hash and executable PE mapping, then
decodes an on-disk byte window using `BepInEx/core/Iced.dll`, without loading game
code. It records the index's metadata hash but does **not** reverify the current
metadata file. Keep that distinction when using older research.

`MaxBytes` is a byte budget, not a method size. The window may omit branches or
include padding, data or other routines. Inspect the output's window limits,
decoding stop reason and shared-entry information; even a fully decoded window
does not prove complete method coverage. Establish semantics from the relevant
body and callers before proposing a typed extension.

After an update, use [Compare-SotfRoutineIndexes.ps1](../tools/Compare-SotfRoutineIndexes.ps1)
as described in the routines guide, then revisit the relevant native body and
callers. An unchanged entry prefix does not establish unchanged behavior.

## Build And Hand Off

Self-review the chosen API's failure paths, bounds, thread requirements and
object lifetime, plus your registration cleanup. Build the affected mod against
its intended Core reference using the example's build instructions. Keep
compilation separate from loading, visual behavior and multiplayer acceptance;
none of those is established by a successful build or an offline routine export.

Follow the example's package layout and record the loader version your mod
requires. Keep raw game binaries, local research exports and machine-specific
paths out of the mod package. For deeper API work, record what is directly
observed, what is inferred, and what remains unknown before adding native calls.
