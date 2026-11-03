# Third-Party Notices

Modstoke Loader by OpenACAI is developed by Alex Cooper.

This project is built around public mod-loader components and preserves their license boundaries. Attribution is part of the source, package, and manager-facing documentation so downstream users can tell what came from OpenACAI and what came from upstream projects:

- **BepInEx** by the BepInEx team: LGPL-2.1. Source: `https://github.com/BepInEx/BepInEx`
- **Modstoke Unity metadata compatibility** replaces `BepInEx.Unity.Common.dll` with source in `src/Modstoke.Compatibility.BepInEx.Unity.Common`. It retains the BepInEx API and assembly identity, the BepInEx team's copyright and LGPL-2.1-only license. The manager-file offsets follow BepInEx's lookup, adapted from [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL). Copyright (C) 2026 Alex Cooper applies to the replacement implementation.
- **Modstoke preloader compatibility** replaces `BepInEx.Preloader.Core.dll` with source in `src/Modstoke.Compatibility.BepInEx.Preloader.Core`, retaining the BepInEx contracts, copyright and LGPL-2.1-only license. Copyright (C) 2026 Alex Cooper applies to the replacement implementation. Patch execution is provided by the GPL-3.0-or-later Modstoke preloader, not the original BepInEx patching engine.
- **Modstoke IL2CPP compatibility** replaces `BepInEx.Unity.IL2CPP.dll` with source in `src/Modstoke.Compatibility.BepInEx.Unity.IL2CPP`. BepInEx's plugin, input, collection and logging contracts retain their identity, attribution and LGPL-2.1-only license. Copyright (C) 2026 Alex Cooper applies to the replacement implementation. Startup, interop orchestration, native hooks, log capture and component hosting are provided by the GPL-3.0-or-later Modstoke runtime.
- **Modstoke core compatibility** supplies `BepInEx.Core.dll` from `src/Modstoke.Compatibility.BepInEx.Core` under LGPL-2.1-only, retaining BepInEx's assembly identity, API, configuration, logging, metadata, console, path and utility implementations. Those support implementations retain the BepInEx team's copyright and their embedded source attributions, including Usagirei/UnityInjector console code and Mono-derived MIT-licensed terminal information code. Copyright (C) 2026 Alex Cooper applies to the Modstoke bindings. Plugin discovery, ordering, activation, caching and chainloader lifecycle execution are supplied by the GPL-3.0-or-later Modstoke preloader.
- **Dobby** BepInEx fork by the BepInEx team, based on Dobby by jmpews and contributors: Apache-2.0. Source: `https://github.com/BepInEx/Dobby`
- **RedLoader/SonsSdk** by Toni Macaroni: LGPL-2.1 according to the upstream repository license file. Source: `https://github.com/ToniMacaroni/RedLoader`
- **Modstoke RedLoader and SonsSdk compatibility** supplies `RedLoader.dll` and `SonsSdk.dll` from `src/Modstoke.Compatibility.RedLoader` and `src/Modstoke.Compatibility.SonsSdk`. Retained upstream contracts, native wrappers, shared state and support implementations keep their original attribution and LGPL-2.1-only license. Modstoke bindings are by Alex Cooper. Startup, mod execution, script loading and SDK service execution are provided by the GPL-3.0-or-later Modstoke runtime.
- **Tomlet** by Sam Byass: MIT. Source: `https://github.com/SamboyCoding/Tomlet`
- **SemanticVersioning** by Adam Reeve: MIT. Source: `https://github.com/adamreeve/semver.net`
- **RedLoader/SonsSdk compatibility helpers** (`Alt.Json.dll`, `GLTF.dll`) remain pinned library inputs from the local RedLoader compatibility cache. They are not rebuilt by the owned compatibility projects. RedLoader credits the GLTF helper as a modified UnityGLTF-derived component.
- **RedManager** by Toni Macaroni: Apache-2.0. Source: `https://github.com/ToniMacaroni/RedManager`

OpenACAI-authored bridge and packaging source is licensed as `GPL-3.0-or-later`.

Redistributed binaries must continue to include the applicable upstream license texts in `LICENSES/` and provide source access for LGPL-covered components.
