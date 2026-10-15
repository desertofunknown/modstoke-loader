# Modstoke Loader

**Repository status (15 October 2026):** This new public repository contains documentation while its source import is being prepared. Loader source, build scripts, and license files have not been imported, and no installable release has been published here. A clone at this stage does not provide a working build or download. The development details below describe a separate, prepared local source checkout.

Modstoke Loader is an independent mod runtime for **Sons of the Forest**, developed by Alex Cooper and published by OpenACAI. It starts through a Rust `winhttp.dll` host, loads a managed bootstrap and preloader, and includes compatibility paths for BepInEx and RedLoader/SonsSdk mods. It is not affiliated with or endorsed by Endnight Games.

The prepared local source checkout is on the experimental `openacai-net11-preview` line. Its manifest declares `0.4.0-net11-preview` and pins .NET 11 RC2. That value describes development source, **not** a public release. A separate .NET 10 line is kept on `openacai-net10-port`.

## Repository and download status

The local source checkout and package metadata still refer to `desertofunknown/openacai-loader`. Manager update links and the existing `OpenACAILoader.zip` filename depend on that identity. This documentation repository is not yet a replacement download source, and no public Loader ZIP is verified here.

Maintainers with the prepared local source checkout can use its `build-openacai-loader.ps1` script to make a test ZIP. It needs local fork outputs, generated game interop assemblies, and a prepared game installation. A fresh clone of this repository cannot produce the ZIP. See [building](docs/BUILDING.md) and [troubleshooting](docs/TROUBLESHOOTING.md) for the current local workflow.

## Planned package layout

- `winhttp.dll`, `.doorstop_version`, and `doorstop_config.ini` start the native host.
- `dotnet/` contains the matching hostfxr and CoreCLR runtime.
- `OpenACAI/Runtime/` contains the bootstrap, preloader, Loader Core, manifest, and runtime state files.
- `OpenACAI/Runtime/Compatibility/` contains transitional RedLoader/SonsSdk support assemblies.
- `OpenACAI/Mods/` is the Modstoke native mod location; `Mods/` and `BepInEx/plugins/` remain compatibility locations.
- `OpenACAI/UserData/` holds Loader and mod data. The package also includes upstream license and notice files.

In the local source checkout, `build-openacai-loader.ps1` creates this layout and `openacai-loader.manifest.json` records it. Neither file is in this public repository yet. The build recipe excludes private Admin modules, generated interop caches, and local logs.

## Developing mods

The **Modstoke SDK** API is currently part of `OpenACAI.Endnight.Loader.Core.dll`, under the `OpenACAI.Loader` and `OpenACAI.Sons` namespaces. It is not a separate SDK binary or NuGet package. Legacy `SonsSdk.dll` is bundled for compatibility and is a different API.

The [World Conditions example in the SDK repository](https://github.com/desertofunknown/modstoke-sdk/tree/main/examples/WorldConditions) shows a native mod manifest, a static entry point, a menu provider, and a setting. It builds against a matching Loader Core assembly, but its in-game loading and UI behavior still need validation. The local source checkout also has a broader API guide.

## Help and project history

OpenACAI-authored Loader code in the local source checkout is GPL-3.0-or-later. Compatibility work preserves the terms and credit of [BepInEx](https://github.com/BepInEx/BepInEx) and [RedLoader/SonsSdk](https://github.com/ToniMacaroni/RedLoader), both LGPL-2.1; the package also uses components such as [Dobby](https://github.com/BepInEx/Dobby). The source checkout's `UPSTREAMS.md`, `THIRD_PARTY_NOTICES.md`, and `LICENSES/` contain the full notices. Those files must accompany any source import or binary redistribution; they have not been imported into this documentation repository.

