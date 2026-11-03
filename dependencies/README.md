# Build dependencies

The Loader builds against the pinned files in `inventory.json`. Each entry records
its SHA256, original input, component origin, assembly identity where applicable,
category and package destinations. Empty `packagePaths` means build-only. The
inventory is copied into the package and its hash is recorded in the file manifests.

The default cache is `.tools/modstoke-dependencies`, excluded from Git. Restore it
from a preserved dependency bundle with the same `managed`, `build-reference`,
`native`, `config` and `unity-libs` layout:

```powershell
./dependencies/Restore-ModstokeDependencies.ps1 -SourceRoot 'D:/BuildInputs/modstoke-dependencies' -IncludeAllNativeRuntimeAssets
```

The helper verifies every selected source file before copying and verifies the
result. Alternatively, pass `-ModstokeDependencyRoot` to `build-openacai-loader.ps1`
to consume a bundle in place. Missing or changed files stop the build with the
expected cache path. Builds never search original BepInEx or RedLoader outputs,
installed support libraries, or a user's NuGet cache for these inputs. Normal
pinned NuGet `PackageReference` restoration is unchanged.

Use `-OutputDirectory 'dist/my-build'` to keep a package separate from the default
`dist` output, for example while inspecting an earlier ZIP. Package filenames and
download addresses remain unchanged.

The initial cache preserves the existing package's runtime support and Unity
2022.2.16 base-library files. The inventory's baseline identifies that package.
Red compatibility references and optional Capstone native assets were transferred
once from the named historical inputs recorded in `acquiredFrom`. Those fields
are provenance, not build search paths. Retain this cache or an equivalent verified
bundle; this repository does not yet publish a dependency-bundle download.

`-IncludeAllNativeRuntimeAssets` includes the eight inventoried Capstone assets
for other platforms, in addition to the normal Windows x64 Capstone and Dobby
payload. It requires those optional cache files. Without the switch, they are
neither required nor copied. Dobby remains pinned to 1.0.5; ad hoc Dobby URL or
version overrides are rejected. `-RebuildExternalRuntime` now applies only to an
explicit `-UseDoorstopForkNativeHost` build.

`GameInteropRoot` is separate from the dependency cache. It must contain the
generated wrappers for the target game, normally `BepInEx/interop` under
`GameRoot`. The Core and Sons compatibility projects reference those wrappers
with `Private=false`; packaging never copies them. Unity base libraries are
generation inputs and cannot replace game wrappers. The Red compatibility API
also retains two historical build references, `Il2Cppmscorlib.dll` and
`UnityEngine.CoreModule-nstrip.dll`, in `build-reference/red`.

For direct MSBuild use, pass `-p:ModstokeDependencyRoot=...` and
`-p:GameInteropRoot=...`. `BepInExCoreRoot` and `RedCompatibilityLibraryRoot` remain
explicit reference overrides; their defaults resolve only inside the dependency
cache. The package script verifies the inventory before building. Red and Sons
facades retain their net6.0 Debug configuration and original assembly identities;
the host remains .NET 11 RC3. Their historical Interop 1.4.6 and MonoMod.Utils
25.0.12 build references remain separate from packaged Interop 1.5.2 and
MonoMod.Utils 25.0.13.

The two historical Red reference DLLs incorrectly request `mscorlib, Version=6.0.0.0`;
the net6 framework facade is `mscorlib, Version=4.0.0.0`. PowerShell 7.2+ helper
`Prepare-RedFrameworkReferences.ps1` verifies the inventoried originals and makes
compile-only copies under `build-reference/red/net6-framework`. Each copy changes
only the major-version byte in that AssemblyRef row. No API, IL, assembly identity,
original input or runtime payload is changed. `provenance.json` records both hashes
and the changed offset. These derived references are never packaged or installed.
Restore and package builds prepare them automatically. Before direct MSBuild use,
run the helper once (with `-RedCompatibilityLibraryRoot` for an alternate cache).
`RedFrameworkReferenceRoot` can explicitly select an equivalent prepared directory.

Compatibility projects retain XML documentation output but do not require a comment
on every inherited public member (`CS1591`). Malformed documentation and other compiler
warnings remain enabled. The deprecated world-update callback has a local `CS0618`
exception only in its compatibility adapter; mod consumers still see its deprecation.

`BepInEx.cfg` contains reviewed defaults for new installations. The builder does
not read a user's configuration or patchers. Configuration remains excluded from
the managed-file manifests; installers must preserve any existing configuration.
The retained `BepInEx.Unity.IL2CPP.dll.config` only contains the historical
Mono.Cecil binding redirect and remains an explicitly hashed runtime sidecar.

Third-party provenance and licenses remain in `THIRD_PARTY_NOTICES.md`, `NOTICE`
and `LICENSES/`, which are copied unchanged into the package. Retaining an input
in this cache does not change its license or grant redistribution rights for
game-generated wrappers. Updating a dependency requires reviewing its origin,
license, references and package destinations, then replacing its hash explicitly.
