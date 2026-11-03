# Native dependency backends

These are candidate backends, not replacements for the retained dependency
inventory until integration is accepted. The source roots are
`Forks/ReleaseDependencies/Dobby` and `Forks/ReleaseDependencies/Alt.Json`,
alongside the suite's parent directory. Existing proprietary notices and
third-party licenses remain unchanged; native implementation does not change
ownership or redistribution terms.

## Rust detours

The new detour backend contract is Rust 2021:
`Dobby/native/modstoke-detours/Cargo.toml`, library name `dobby`, crate type
`cdylib`, Windows x64 only. It pins `iced-x86` 1.21.0 for instruction relocation
and `windows-sys` 0.61.2 for Windows APIs. The upstream Dobby C++ tree is retained
as reference only: this backend does not link it or use a C++ shim. Build with
Cargo, not CMake. The baseline C++ recipe in
`tools/Build-MaintainedDependencies.ps1` is separate and remains unchanged.

The Windows x64 Dobby-compatible C ABI preserves the old signatures and names:
`DobbyPrepare`, `DobbyCommit`, `DobbyDestroy`, `DobbyHook`, `DobbyBuildVersion`,
`dobby_enable_near_branch_trampoline` and
`dobby_disable_near_branch_trampoline`. Prepare/Hook take target and replacement
pointers plus an output trampoline pointer; Commit/Destroy take the target
pointer. These four operations return 0 on success and -1 on failure. Exports
use `extern "C"` with panic containment and `panic = "unwind"`; foreign memory
access uses checked current-process OS reads/writes, not arbitrary Rust references.

The lifecycle contract serializes registry operations, including Hook's
prepare/commit pair. Prepare relocates complete instructions with `iced-x86`,
seals trampoline memory RX and flushes its instruction cache before publication.
Commit requires unchanged original bytes. Failed or uncertain patch/protection
restoration retains recovery state; Destroy must not overwrite foreign changes
and removes an entry only after bytes and protections are restored. Published
trampolines remain allocated for process lifetime, even after Destroy.

Registry locking is not atomic code patching. No thread suspension is provided;
the caller must quiesce threads that could execute affected code. This is not
all-platform Dobby parity or a guarantee of concurrent patch safety.

Existing consumers resolve `dobby.dll`: the owned loader binds Prepare, Commit
and Destroy, and the RedLoader P/Invoke surface also declares Hook.

The owned BepInEx detour factory defaults to Dobby, falling back to Funchook
when Dobby cannot load. An explicit Dobby selection fails instead of falling
back. RedLoader compatibility binds its Dobby shim directly. BepInEx loading
first checks its assembly directory, then permits legacy native-library lookup;
RedLoader retains its own P/Invoke resolver. Do not apply the JSON backend's
adjacent-only loading rule or disable switch to detours.

## Alt.Json compatibility

Keep the exact unsigned managed identity:
`Alt.Json, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`.
The existing public CLR API and `Alt.Json` namespaces remain the compatibility
surface. Native JSON performs buffered syntax decoding, integer conversion,
binary-text conversion and escaping, not full native CLR serialization.
It is an internal implementation detail, not a new
serialization API or a replacement for reflection, contracts, converters or
`JToken`. It does not replace the game's Newtonsoft.Json wrapper or
`System.Text.Json` APIs.

The JSON integration contract uses
`Alt.Json/native/modstoke-json` to produce `modstoke_json_native.dll`:

- Cdecl ABI version 3, reported by `modstoke_json_abi_version`. The managed
  bridge rejects other versions and uses its managed implementation instead.
- `modstoke_json_first_escape_utf16` and `modstoke_json_escape_utf16` scan or
  emit escaped content without surrounding quotes. Modes are Default (0),
  EscapeNonAscii (1) and EscapeHtml (2); quotes are single or double quotes.
- `modstoke_json_scan_string_utf16` scans after the opening quote and returns
  the closing-quote offset; backslash, CR, LF, NUL or exhaustion requests fallback.
- `modstoke_json_scan_number_utf16` scans only the prefix characters
  `0-9 a-f A-F x X + - .`; managed code still validates and converts numbers.
- `modstoke_json_decode_string_utf16` decodes complete buffered strings,
  including escaped UTF-16 surrogate handling. Incomplete strings, raw source
  newlines and invalid escapes return to the managed reader without advancing it.
- `modstoke_json_parse_integer_utf16` classifies 32/64-bit decimal integers,
  preserving invalid-versus-overflow and leading-zero digit-count rules.
- `modstoke_json_scan_prefix_utf16` scans ASCII whitespace, comment spans or
  unquoted identifiers. Unicode classification, delimiters and line tracking
  stay with the reader.
- `modstoke_json_format_integer_utf16` writes decimal digits into an existing
  writer buffer; single-digit output and writer callbacks remain unchanged.
- `modstoke_json_encode_base64_utf16` and `modstoke_json_decode_base64_utf16`
  handle standard Base64. Unsupported input uses the managed conversion;
  Guid alternatives and JSON byte-array wrappers remain unchanged.
- `modstoke_json_parse_date_utf16` returns fresh ISO-parser components, preserving
  its legacy lexical rules. CLR construction, rollover, timezone handling and
  resulting exceptions remain managed.
- `modstoke_json_format_date_utf16` formats a Gregorian ISO body from .NET ticks,
  trimming trailing fraction zeros. Zone suffixes and custom formats stay managed.
- `modstoke_json_transform_name_utf16` applies ASCII camel/snake/kebab naming;
  non-ASCII input falls back to .NET casing and classification.
- `modstoke_json_scan_decimal_utf16` accumulates lexical decimal parts. The original
  CLR arithmetic tail still supplies scale, rounding, overflow and exceptions.
- `modstoke_json_parse_big_integer_utf16` converts decimal text to unsigned
  little-endian magnitude; `modstoke_json_format_big_integer_utf16` performs the
  reverse conversion. Managed code owns the BigInteger objects and stream writes.
  Native parsing handles the reader's existing 380-character bound; formatting
  handles magnitudes up to 256 bytes, with unrestricted managed fallback beyond
  that fast-path bound. This does not impose a new public value-size limit.

Inputs and outputs are UTF-16 code units with caller-owned pinned buffers;
there is no UTF-8 conversion, normalization, retained pointer, callback or I/O.
Statuses are 0 success, 1 unsupported/incomplete, 2 invalid argument,
3 insufficient capacity and 4 caught panic. Short output buffers report the
required length without partial writes. Size, alignment and nonoverlap checks
do not establish arbitrary pointer validity.

`MODSTOKE_NATIVE_JSON` enables the managed binding; other builds use
false-returning stubs. The binding must load only the DLL adjacent to the
loaded Alt.Json assembly, only on Windows x64 and only with the matching ABI,
without PATH search. Missing or incompatible native code uses the managed path.
Set `AppContext.SetSwitch("Modstoke.NativeJson.Disable", true)` before JSON use
to disable this backend.

Reader integration operates only on the current buffer. State, refill, CLR date
construction, decimal arithmetic, floating-point conversion and errors remain
managed. Async readers make synchronous
native calls only over buffered data, preserving cancellation and refill
scheduling. Native bulk string escaping is limited to exact `StringWriter` instances,
not subclasses; other writers may share the first-escape scan without changing
I/O orchestration. User writer/callback exceptions must not become fallback.

## Candidate builds and boundary

After fork writers finish, run from the suite root with Rust's Windows MSVC
target, LLVM linker and the pinned .NET SDK available:

```powershell
& '.\tools\Build-NativeDependencyCandidate.ps1' `
  -OutputDirectory (Join-Path $HOME 'Documents\ModstokeNativeCandidate')
```

Use a new output directory outside the game installation and forks, with an
existing parent. The script captures allowlisted source and builds both native
libraries plus the identity-preserving managed Alt.Json assembly with its
native binding enabled. Cargo packages and .NET references must already be
cached; restores are offline. Cargo runs from each captured crate directory
so its local linker configuration applies. Nothing is installed or promoted
to the dependency inventory. Package
`modstoke_json_native.dll` beside the Alt.Json assembly actually loaded.

Candidate provenance must retain both Cargo lockfiles and their hashes, source
snapshot and recipe hashes, compiler/toolchain versions, target/build options
and output DLL hashes. Record `iced-x86` and `windows-sys` versions, source
origins and source checksums from the lockfile, and retain their license texts
and those of transitive dependencies. Record local Rust source hashes too;
an upstream C++ revision alone does not identify this implementation. The
script writes these receipts separately under `provenance/`. Candidates remain staged, not deployed;
no installed files, retained cache or release inventory are changed by staging.

The ABI3 October 22, 2026 candidate compiled both Rust libraries and both
managed targets without warnings or errors, using the pinned .NET 11 RC2 SDK.
The managed targets are .NET 11 for the runtime and .NET Standard 2.1 for
compatible library consumers, with the same managed assembly identity.
The Standard target uses its built-in nullable annotations and a compatible
faulted ValueTask construction for asynchronous disposal.
Static PE inspection confirmed the seventeen ABI3 JSON export names in the x64
library. Source, toolchain, output hashes and license receipts were captured
separately. The native DLLs still depend on the Windows MSVC runtime.
Integrated consumer and runtime acceptance remain pending. No test results,
performance gain, antivirus clearance or proven in-game behavior are claimed.

## Remaining boundaries

The native additions do not move CLR reflection, converters, DOM identity,
notifications or lazy query execution into Rust. BSON framing and JSONPath
parsing also remain managed; a bulk native representation would require its own
stream, descriptor and error-timing contracts. The dormant custom double parser
is not a replacement for the active .NET floating-point parser. No equivalent
rounding or runtime performance claim is made for such a substitution.

## Harmony boundary

Harmony/HarmonyX and MonoMod remain separately owned work. Their CLR integration
works with methods, metadata and IL, including managed `Reflection.Emit`
paths such as `DynamicMethod` and `MethodBuilder`. A native detour can redirect
a machine-code entry point; it cannot supply Harmony's managed patch ordering,
prefix/postfix/transpiler composition or emitted method bodies. Neither Rust
backend substitutes for that engine or changes its backend selection.
