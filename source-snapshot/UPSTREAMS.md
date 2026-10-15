# Upstream Lineage

Modstoke Loader by OpenACAI is not presented as a clean-room loader. It is an OpenACAI-maintained compatibility distribution that continues work from established open-source loader projects.

## Primary Upstream Projects

- **BepInEx** by the BepInEx team: `https://github.com/BepInEx/BepInEx`
  - Provides the Unity IL2CPP/Doorstop loader foundation that OpenACAI uses as the single bootstrap path.
  - License: LGPL-2.1.

- **Dobby** BepInEx fork: `https://github.com/BepInEx/Dobby`
  - Provides the win-x64 native detour backend staged as `BepInEx/core/dobby.dll`
    while OpenACAI replaces the transitional IL2CPP bootstrap path.
  - License: Apache-2.0.

- **RedLoader/SonsSdk** by Toni Macaroni: `https://github.com/ToniMacaroni/RedLoader`
  - Provides the Sons Of The Forest-specific RedLoader and SonsSdk compatibility surface that OpenACAI hosts beside BepInEx.
  - License: LGPL-2.1 according to the upstream repository license file.

- **RedManager** by Toni Macaroni: `https://github.com/ToniMacaroni/RedManager`
  - Provides manager/package flow reference material used by the paired Modstoke Manager project.
  - License: Apache-2.0.

## OpenACAI Changes

OpenACAI-authored work includes the BepInEx-first RedLoader compatibility bridge, packaging recipe, loader state/capability reporting, and Sons Of The Forest-focused compatibility hardening.

The client-only Modstoke Client Companion is included in this source snapshot.
It adapts its inventory audit protocol, reader, and responder flow from the
owner's private OpenACAI Admin source. The adapted client component is licensed
under MIT with Copyright (c) 2026 Alex Cooper; its scoped license is at
`src/Modstoke.ClientCompanion/LICENSE`. The host Admin mod is separate and is not
included here. RedLoader's LGPL-2.1 root license and AGPL-2.0 project metadata
conflict remains documented in `THIRD_PARTY_NOTICES.md`.
