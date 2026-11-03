# Contributing

Modstoke targets Sons of the Forest's shipped IL2CPP player. Check the matching
game types and existing SDK before introducing a new service or abstraction.
Generated interop wrappers describe callable signatures, not the original game logic.

## Code

- Keep game rules in Sons-specific services. Keep BepInEx and RedLoader API
  compatibility in their adapters; preserve exported names and assembly identities.
- Follow the surrounding module's naming and namespace layout. Use four spaces,
  ordinary C# idioms and short names with an established meaning, such as `id` or
  `dt`. Do not shorten names at the expense of clarity or reformat unrelated files.
- Construct objects where their lifetime is owned. Pass dependencies when a caller
  supplies them; do not add a container or interface for a single implementation.
- Reuse identical validation and conversion rules. Keep differing packet formats,
  truncation rules and native contracts separate, even when the code looks similar.
- Comments should explain constraints or decisions: Unity thread affinity, native
  ownership, save compatibility and authority checks. Skip descriptions of obvious
  assignments or wrappers. Keep API summaries short; document non-obvious caller
  obligations in remarks.

## Source Layout

Put code with the subsystem that owns it. Loader Core separates hosting,
discovery, compatibility and Unity runtime support from game code in `Sons/`.
Use [the Core guide](src/Modstoke.Loader/README.md) to find the
right folder before adding a file; avoid a new catch-all helpers directory.

Use `Modstoke` for projects, assemblies and namespaces, not as a prefix on every
class, struct or method. Names inside the namespace should describe their role. Use
`Type.Area.cs` for partials, group related parts together, and preserve meaningful
suffixes such as `Protocol`, `Hooks` and `Persistence`. Other modules keep their
local naming conventions.

Names describe the work, not how the file was produced. Avoid repeating the brand,
folder, or implementation label in every type and member. Keep `Native` where it
distinguishes unmanaged storage, calling conventions or ownership from a managed
counterpart. Do not add it merely because game code calls IL2CPP. Likewise, keep
`Client` and `Server` when authority matters. Prefer a specific operation over a
chain of generic words such as manager, adapter and handler.

For renames, follow symbol references and also check reflection strings, Unity
callbacks, serialized names, service keys and native exports. A cleaner source
name must not silently change a persisted or external contract. Keep matching
filenames and partial names together; do not shorten names into unfamiliar slang.

Core uses explicit `Compile` entries. Keep existing entries in order when moving
files; partial field initializers can depend on that order. Update source links
in current documentation when paths change.

## Errors And Diagnostics

Expected input rejection belongs in a result or status, not an exception. Catch
exceptions at boundaries where recovery is possible: files, native calls, plugin
callbacks and lifecycle cleanup. Let ordinary internal failures reach that boundary.
Keep cleanup isolation when one failure must not prevent releasing other resources.

Use the owning component's existing logger. Report a failure once with operation
context and the exception needed to diagnose it; show players a concise status.
Do not replace useful diagnostics with silent catches or log the same failure at
every layer. Preserve native lifetime and multiplayer validation checks.

## Changes And Builds

Keep a change focused on one behavior or cleanup. Review its callers, ownership,
error paths and diff before compiling the affected project with the SDK pinned in
`global.json`. Use the matching game and dependency references; do not upgrade them
as part of a style edit. See [Project Structure](docs/PROJECT_STRUCTURE.md) and
[Source and Build](docs/NEXUS_SOURCE_AND_BUILD.md) for the repository layout and build inputs.

A successful build is not proof of gameplay or multiplayer behavior. State which
checks ran and what remains unverified. Keep local notes, private paths and
diagnostic artifacts out of source and release packages.
Preserve copyright headers, licenses and upstream attribution.
