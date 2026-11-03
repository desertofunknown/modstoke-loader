# Modstoke Harmony

A Windows x64, .NET 11 candidate that retains the HarmonyX managed API and uses a Rust planning core. It is based on HarmonyX 2.16.1, commit `02a98f540fa12ac4656f189a3cf67b0c96cc68f7`.

This is a mixed Rust/CLR implementation. The native core owns planning and selection algorithms; the managed assembly preserves the mod API and executes CLR operations. It is not yet a validated replacement for the installed loader. The managed boundary remains substantial, rather than a tiny facade or a complete Rust rewrite.

## Implementation

Rust sorts base `Patch` priority/index keys with the pinned .NET runtime's introsort behavior, then performs dependency traversal and cycle pruning. The managed adapter supplies method-equality groups and dependency-set order; patch subclasses retain the reflection-based managed comparator. Individual patch occurrences are retained, including duplicate methods and equal sorting keys. Rust also handles metadata precedence, patch filtering and owner aggregation, stable attribute partitioning, patch-class lifecycle planning, argument-name resolution, matcher traversal, opcode candidate and jump scans, and type-name rendering.

The native core produces prefix/postfix/finalizer programs, reverse/unpatch plans, invocation and delegate recipes, field-access and emitter-dispatch recipes, and IL decoding, branch and exception-handler tables. Cached recipes avoid native transitions for each emitted opcode. Stateful matcher and lifecycle operations resume between managed callbacks; the native core never invokes those callbacks. Generated patches and invocation delegates execute directly in the CLR without calling Rust on each game-method invocation.

Argument-array initialization and copyback, boxed-argument restoration, instance-argument loading, and switch-label fixups are planned in Rust. Cached transfer programs keep reflection reads at their emission points and avoid rebuilding a plan for each parameter list. Switch planning reuses disposable identity storage without changing live instruction arrays before replay.

UTF-16 literal escaping and linear string joining, type/member-name parsing, IL-error location parsing and offset ordering, instruction formatting, diagnostic indentation and handler layout, and hexadecimal log rows also run in Rust. Eligible exact-opcode patterns are matched in bounded batches; predicates and named captures keep their live callback path. Native planners borrow input buffers where possible, use caller-owned output storage, and avoid temporary allocations for fixed instruction recipes.

The managed assembly retains public `HarmonyLib` objects and attributes, mutable patch arrays, CLR reflection and equality, lazy callback discovery, live transpiler streams, IL emission, GC-tracked references, logging, and backend extension points. Reflection caches and object traversal retain their CLR locking and callback behavior. It still uses MonoMod and Il2CppInterop.HarmonySupport. Replacing these contracts with native addresses or eagerly evaluated snapshots would change existing mod behavior; this candidate neither replaces their detours nor sandboxes executable mods.

The unused internal BinaryFormatter serializer has been removed. Public patch fields, JSON attributes and the typed JSON converter remain. Assembly identity stays `0Harmony, Version=2.16.1.0, Culture=neutral, PublicKeyToken=null`; informational version identifies this implementation separately. Original type forwards remain enabled when targeting .NET 11.

The native boundary retains no caller buffers or CLR objects. It validates descriptor ranges, capacities and buffer overlap, contains Rust unwinding, and returns statuses through the managed exception path. Accessible pointer memory remains a caller responsibility. The DLL is loaded by absolute path beside `0Harmony.dll`, checked for ABI version 4 and the shared node size, and retained for process lifetime. There is no search-path fallback or silent fallback to the upstream planner.

## Build

Run `./build.ps1` from this directory using PowerShell 7. The script uses the suite's pinned .NET SDK, the existing maintained MonoMod/Cecil reference cache, Cargo and the MSVC-target LLVM linker. Rust has no third-party crates. An optional `-DependencyRoot` selects another matching managed reference directory; `-OutputDirectory` selects the artifact directory.

The default output is `.artifacts/candidate`, containing `0Harmony.dll`, `modstoke_harmony_core.dll`, documentation, notices and hashes. The script only compiles and stages. It does not install, change dependency inventory, run tests, or launch the game. Keep both DLLs together when preparing any future integration; changing only one is unsupported.

## Compatibility limits

Public source signatures and type forwards are retained, but binary identity and successful compilation alone do not establish behavioral compatibility. This candidate targets the current Windows x64 .NET 11 loader, not every framework/platform supported by upstream HarmonyX.

Patch sorting snapshots method equality before native traversal. Cycle diagnostics are emitted afterward, in the planned order, with each removed dependency applied before its log callback. A logging subscriber that mutates patch methods can still affect upstream traversal differently. Metadata precedence uses a cached Rust policy while CLR field reads and writes remain interleaved. Attribute grouping merges incomplete metadata before evaluating live CLR target equality. Concurrent mutation of exposed patch objects is not made thread-safe. Native load failure deliberately stops initialization before normal patch installation and can precede argument rejection in operations requiring the backend.

Some native operations serialize metadata and UTF-16 strings into temporary buffers. String joining preserves converter order and null/empty separator behavior, but buffers converted strings before composition; allocation or oversized-result failures can therefore occur later than in upstream's repeated concatenation. This port does not establish a speedup or lower allocation rate; the setup and query costs have not been benchmarked.

The IL-error parser uses a bounded linear scan rather than `Regex.Match`, so matching no longer produces regex-initialization or match-timeout failures. CLR trimming, Unicode digit classification and hexadecimal parsing remain in place. `FileLog.LogBytes` now terminates for `int.MaxValue` bytes instead of overflowing its loop counter. These edge-case differences are intentional.

Before deployment, the specific remaining checks are API/forwarded-type comparison against the official DLL; differential ordering with duplicate methods, duplicate indices and cycles; prefix/postfix/finalizer state and ref/out/result behavior; lazy transpilers and reverse patches; custom IL2CPP patchers; and patch/unpatch failure recovery. These checks have not been run. No performance, comprehensive security, live gameplay or universal mod-compatibility claim is made.

See `THIRD_PARTY_NOTICES.md` for retained source licenses.
