# Source snapshot review

This is a curated source snapshot for review. It is not an installable release.

The frozen local preview ZIP (`C36CFEBA834D659A3A4E235834AC403141D02A96714806F9162BC6CB587D0018`)
used explicitly hashed local BepInEx and RedLoader build outputs. The curated
fork source here has not produced those exact binaries. Some RedLoader build
dependencies, including game-generated wrappers and a separate Il2CppInterop
submodule, are intentionally absent. Treat this tree as review material, not
complete corresponding source for that ZIP or a working fresh-clone build.

The Modstoke Client Companion has a scoped MIT license at
`src/Modstoke.ClientCompanion/LICENSE`, Copyright (c) 2026 Alex Cooper. It is
separate from the host Admin mod. The Loader's other source keeps its existing
license claims and the upstream fork licenses and notices are preserved.

RedLoader's root `LICENSE` says LGPL-2.1 while its `Directory.Build.props`
declares `AGPL-2.0` as package metadata. That conflict is disclosed, not
resolved. `LICENSES/THIRD_PARTY/` includes identified component notices,
including UnityGLTF, Mono `TermInfoReader`, and the original UnityInjector MIT
license. The UnityInjector console files retain Usagirei's original 2015
headers. Six `ConsoleEncoding*.cs` files, three each from BepInEx and
RedLoader, are deliberately excluded from this public source snapshot. They
also credit an asm256 gist and Jon Skeet's EBCDIC page, and the gist's
code-wide grant remains unclear. They remain only in local review material.

The staged RedLoader `Directory.Build.props` replaces a local absolute game
path with an overridable `$(SOTF_GAME_ROOT)` property. A disabled comment with
a developer-local asset-map path was removed from staged `SonsSdk/GameResources.cs`.
These edits do not exist in the original local fork or frozen preview ZIP build
input.
