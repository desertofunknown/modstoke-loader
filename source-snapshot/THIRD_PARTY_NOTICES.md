# Third-Party Notices

OpenACAI Endnight Loader is developed by Alex Cooper.

This project is built around public mod-loader components and preserves their license boundaries. Attribution is part of the source, package, and manager-facing documentation so downstream users can tell what came from OpenACAI and what came from upstream projects:

- **BepInEx** by the BepInEx team: LGPL-2.1. Source: `https://github.com/BepInEx/BepInEx`
- **Modstoke Unity metadata compatibility** replaces `BepInEx.Unity.Common.dll` with source in `src/Modstoke.Compatibility.BepInEx.Unity.Common`. It retains the BepInEx API and assembly identity, the BepInEx team's copyright and LGPL-2.1-only license. The manager-file offsets follow BepInEx's lookup, adapted from [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL). Copyright (C) 2026 Alex Cooper applies to the replacement implementation.
- **Modstoke preloader compatibility** replaces `BepInEx.Preloader.Core.dll` with source in `src/Modstoke.Compatibility.BepInEx.Preloader.Core`, retaining the BepInEx contracts, copyright and LGPL-2.1-only license. Copyright (C) 2026 Alex Cooper applies to the replacement implementation. Patch execution is provided by the GPL-3.0-or-later Modstoke preloader, not the original BepInEx patching engine.
- **Dobby** BepInEx fork by the BepInEx team, based on Dobby by jmpews and contributors: Apache-2.0. Source: `https://github.com/BepInEx/Dobby`
- **RedLoader/SonsSdk** by Toni Macaroni: LGPL-2.1 according to the upstream repository license file. Source: `https://github.com/ToniMacaroni/RedLoader`
- **Tomlet** by Sam Byass: MIT. Source: `https://github.com/SamboyCoding/Tomlet`
- **SemanticVersioning** by Adam Reeve: MIT. Source: `https://github.com/adamreeve/semver.net`
- **RedLoader/SonsSdk compatibility helpers** (`Alt.Json.dll`, `GLTF.dll`) are packaged from the local RedLoader/SonsSdk compatibility build while OpenACAI absorbs those SDK features into OpenACAI-owned services. RedLoader credits the GLTF helper as a modified UnityGLTF-derived component.
- **RedManager** by Toni Macaroni: Apache-2.0. Source: `https://github.com/ToniMacaroni/RedManager`
- **.NET runtime** by Microsoft and .NET contributors: the exact .NET runtime bundled with this package is distributed with its acquired binary terms in `dotnet/LICENSE.txt` and the applicable collected notices in `dotnet/ThirdPartyNotices.txt`. Source: `https://github.com/dotnet/runtime`.

The bundled Modstoke Client Companion is a client-only plugin. Its audit protocol,
inventory reader, and request flow were adapted from the owner's private OpenACAI
Admin source. The owner authorized public distribution and licensed the adapted
client companion code under MIT. `src/Modstoke.ClientCompanion/LICENSE` carries
the scoped notice, Copyright (c) 2026 Alex Cooper. The grant does not extend to
the separate host Admin project; its DLL is not part of this package.

`LICENSES/THIRD_PARTY/` contains component-specific notices for identified
dependencies and incorporated Mono and UnityGLTF source. BepInEx
`BepInEx.Core/Console/Unix/TtyHandler.cs` abridges Mono's `TermInfoReader.cs`;
`MIT-Mono-TermInfoReader.txt` preserves the Novell/Mono notice. RedLoader's
`GLTF` source credits modified UnityGLTF; `MIT-UnityGLTF.txt` preserves its
notice. RedLoader's tracked `LICENSE` is LGPL-2.1, while its
`Directory.Build.props` states `PackageLicenseExpression=AGPL-2.0`. We retain
both facts without resolving the conflict or asserting one license for its
binary outputs.

Five BepInEx console source files retain `Copyright (c) Usagirei 2015 - 2015`
headers and originate in the public UnityInjector `ConsoleUtil` tree. Three of
those files, and their three RedLoader copies, are excluded from this source
snapshot as described below. UnityInjector's
archived root license is MIT, Copyright (c) 2024 Reisen Usagi; that exact text
is `LICENSES/THIRD_PARTY/MIT-UnityInjector-2024.txt`. BepInEx's historical
MIT notice from when it imported these files is also retained. The three
BepInEx `ConsoleEncoding` files and their three RedLoader copies credit an
asm256 gist and Jon Skeet's EBCDIC page.
Jon Skeet's page permits source and binary use and modification, including
commercial use, while asking that his work not be passed off as someone else's
and restricting sale of just that code without asking him. The gist's code-wide license is not clearly stated; its credit and URL
remain in the local original fork. These six files are excluded from this
public source snapshot while the gist grant remains unclear.

OpenACAI-authored bridge and packaging source is licensed as `GPL-3.0-or-later`.

Redistributed binaries must continue to include the applicable upstream license texts in `LICENSES/` and provide source access for LGPL-covered components.
