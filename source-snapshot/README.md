# Modstoke Loader

**Source snapshot status (15 October 2026):** This reviewed tree includes Loader source, build scripts, curated BepInEx and RedLoader fork source, and attribution files. It is a partial source review snapshot, not an installable release. The frozen preview ZIP was built with separately hashed local fork outputs and cannot be reproduced byte for byte from this curated tree. Six console encoding files with unresolved third-party permission are excluded; see [source review](SOURCE_REVIEW.md).

Modstoke Loader is an independent mod runtime for **Sons of the Forest**, developed by Alex Cooper and published by OpenACAI. It starts through a Rust `winhttp.dll` host, loads a managed bootstrap and preloader, and includes compatibility paths for BepInEx and RedLoader/SonsSdk mods. It is not affiliated with or endorsed by Endnight Games.

The prepared local source checkout is on the experimental `openacai-net11-preview` line. Its manifest declares `0.4.0-net11-preview` and pins .NET 11 RC2. That value describes development source, **not** a public release. A separate .NET 10 line is kept on `openacai-net10-port`.

## Repository and download status

The source checkout and frozen preview package metadata still refer to `desertofunknown/openacai-loader`. Manager update links and the existing `OpenACAILoader.zip` filename depend on that identity. The new public repository is not yet a replacement download source, and no public Loader ZIP is verified here.

Maintainers with a prepared checkout can use `build-openacai-loader.ps1` to make a test ZIP. It needs local fork outputs, generated game interop assemblies, and a prepared game installation. This curated source snapshot alone cannot produce the frozen ZIP. See [building](docs/BUILDING.md) and [troubleshooting](docs/TROUBLESHOOTING.md) for the local workflow.

## Planned package layout

- `winhttp.dll`, `.doorstop_version`, and `doorstop_config.ini` start the native host.
- `dotnet/` contains the matching hostfxr and CoreCLR runtime.
- `OpenACAI/Runtime/` contains the bootstrap, preloader, Loader Core (including the Modstoke SDK API), manifest, and runtime state files.
- `BepInEx/plugins/Modstoke.ClientCompanion/` contains the client-only companion; host Admin is a separate project.
- `OpenACAI/Runtime/Compatibility/` contains transitional RedLoader/SonsSdk support assemblies.
- `OpenACAI/Mods/` is the Modstoke native mod location; `Mods/` and `BepInEx/plugins/` remain compatibility locations.
- `OpenACAI/UserData/` holds Loader and mod data. The package also includes upstream license and notice files.

The local build script creates this layout and `openacai-loader.manifest.json` records it. The build recipe excludes the host Admin mod, generated interop caches, and local logs. Source included here for the client companion is MIT licensed in [its scoped license](src/Modstoke.ClientCompanion/LICENSE), Copyright (c) 2026 Alex Cooper.

## Developing mods

The **Modstoke SDK** API is currently part of `OpenACAI.Endnight.Loader.Core.dll`, under the `OpenACAI.Loader` and `OpenACAI.Sons` namespaces. It is not a separate SDK binary or NuGet package. Legacy `SonsSdk.dll` is bundled for compatibility and is a different API.

The [World Conditions example in the SDK repository](https://github.com/desertofunknown/modstoke-sdk/tree/main/examples/WorldConditions) shows a native mod manifest, a static entry point, a menu provider, and a setting. It builds against a matching Loader Core assembly, but its in-game loading and UI behavior still need validation. The local source checkout also has a broader API guide.

## Help and project history

OpenACAI-authored Loader code is GPL-3.0-or-later, apart from the scoped MIT client companion. Compatibility work preserves the terms and credit of [BepInEx](https://github.com/BepInEx/BepInEx) and [RedLoader/SonsSdk](https://github.com/ToniMacaroni/RedLoader). The latter's root LGPL-2.1 license conflicts with its AGPL-2.0 package metadata, as explained in [third-party notices](THIRD_PARTY_NOTICES.md). The source snapshot also includes [upstream lineage](UPSTREAMS.md) and [license texts](LICENSES/). Complete notices and matching corresponding source are still being reviewed for binary redistribution.
