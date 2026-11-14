# Mod dependencies

Modstoke Loader can require another managed mod to finish
startup before starting your mod. Add `modstokeDependencies` to your Modstoke
manifest when your callbacks depend on services provided by another mod:

```json
{
  "id": "example.settlement",
  "name": "Settlement",
  "version": "1.0.0",
  "assembly": "Settlement.dll",
  "entryType": "Settlement.Mod",
  "entryMethod": "Initialize",
  "modstokeDependencies": {
    "schemaVersion": 1,
    "required": [
      { "id": "example.community", "version": "2.1.0" }
    ]
  }
}
```

The required mod must be enabled, have an executable managed entrypoint and pass
the Loader's hosting checks. A plain version must match exactly, including case;
`2.1` and `2.1.0` are different versions. A version written as `">=2.1.0"` is a
minimum: 2.1.0 and every later version meet it, compared as major.minor.patch
with SemVer prerelease order, and a minimum without a prerelease also accepts
that version's prereleases (`2.1.0-dev`), as `minLoaderVersion` does. Prefer a
minimum unless your mod reaches into the other mod's internals. IDs match
without regard to case. No optional dependencies or automatic downloads are
implied.
Library-only packages and RedLoader/BepInEx plugins cannot satisfy this Modstoke
lifecycle contract. Their assembly references and compatibility dependency
handling remain separate.

Missing dependencies, duplicate enabled identities or assemblies, mismatched
versions and dependency cycles prevent the affected mods from starting.
Dependents of a blocked mod are blocked too; unrelated mods can still start.
Startup and ordinary lifecycle callbacks run in dependency order. Alphabetical
candidate order breaks ties. Shutdown runs in reverse order so a dependent gets
its cleanup callbacks before its prerequisites.

If a prerequisite fails during startup, the dependent's entrypoint is not run.
If it fails during a later Loader-dispatched callback, dependent mods stop
receiving ordinary runtime callbacks before their next callback is invoked, and
their declared shutdown and dispose callbacks run at that point instead of at
game exit, so they can release what they hold while the game is still running.
This does not unload assemblies or undo external event handlers, threads or game
mutations; mods remain responsible for cleaning up their own resources.

A mod that references another mod's assembly without declaring it in
`modstokeDependencies` still loads, but the Loader logs a warning: without the
declaration the two may start in either order.

The declaration accepts at most 64 required mods. IDs are limited to 96
characters and version strings to 256, with no surrounding whitespace or control
characters. Duplicate requirements, self-dependencies, unknown declaration fields
and unsupported schema versions are rejected. Manifests must be JSON objects
within 512 KiB and 16 levels of nesting, with unique property names ignoring case.
UTF-8 and the previously supported BOM-marked encodings are accepted. Identity
or version changes after discovery, and byte changes after preflight, require a
restart after package changes are finished.

`ModCandidateSnapshot.Dependencies`, `DependencyIssue` and
`HasDependencyDeclaration` expose the parsed declaration. Final startup and
callback results are in `ModRuntime.State`; a valid declaration
alone does not prove that its dependencies started or remain active.

Existing manifests without `modstokeDependencies` keep their existing ordering
and dependency-free behavior. Generic `dependencies` and `requires` fields are
not reinterpreted. Existing runtime IDs are unchanged; an ID that the older
runtime truncates cannot satisfy a new dependency requirement.

This contract starts with SDK package 0103. Older Modstoke builds ignore the new
field, so authors must require a supporting Loader when distributing packages
that rely on it. The field does not itself enforce a minimum Loader version.
Minimum versions (`>=`) need a Loader built on or after 2026-11-14; an older
one reads them as exact versions that never match.
Manager installation planning does not yet consume this declaration. Exact
versions are compatibility requirements, not proof of publisher identity, client
integrity or trusted gameplay.
