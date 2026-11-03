# Modstoke Loader .NET 10 Modernization Track

Developer: Alex Cooper

The `openacai-net10-port` branch is the active .NET 10 LTS track for Modstoke Loader. It is the line we are modernizing for public use; older .NET 6 compatibility builds should come from an archived legacy branch/package, not from this track.

The `.NET 11` work lives on `openacai-net11-preview` and is documented in
`NET11_PREVIEW_EXPERIMENT.md`. Keep this `.NET 10` track as the LTS baseline
until the preview runtime is validated repeatedly in-game.

Sons Of The Forest runtime facts, Unity constraints, and the hardening roadmap
are tracked in `SOTF_RUNTIME_COMPATIBILITY.md`.

UnityDoorstop hostfxr notes, upstream base facts, and the Doorstop/Mono/Harmony
source review are tracked in `DOORSTOP_RUNTIME_NOTES.md`.

## Why This Exists

Earlier OpenACAI Endnight Loader builds kept the in-game BepInEx IL2CPP bootstrap on .NET 6 because the first direct .NET 10 runtime swap crashed inside `coreclr.dll` before BepInEx managed startup. That failure pointed at the native runtime host path, not just the C# target framework.

The .NET 10 track modernizes the full stack:

- OpenACAI RedLoader/BepInEx compatibility bridge targets `net10.0`.
- BepInEx Unity IL2CPP is built from the OpenACAI experimental fork as `net10.0`.
- UnityDoorstop is built from an OpenACAI fork that can initialize CoreCLR through `hostfxr`.
- The OpenACAI UnityDoorstop fork validates configured `hostfxr.dll` and
  `coreclr.dll` architecture before loading native runtime components.
- Loader packages use a framework-style runtime layout with `dotnet/host/fxr/<version>/hostfxr.dll` and `dotnet/shared/Microsoft.NETCore.App/<version>/`.
- `BepInEx.Unity.IL2CPP.runtimeconfig.json` declares the runtime used by the native host.

## Current Status

Managed BepInEx IL2CPP and the OpenACAI compatibility bridge compile on .NET 10. The patched Doorstop fork also compiles on Windows x64.

The hostfxr path now reaches BepInEx startup in the local Sons Of The Forest install. Keep this branch as the active modernization line and validate OpenACAI-hosted compatibility shims, Harmony gates, native detours, and manager repair behavior under the hostfxr bootstrap before broad public release.

The BepInEx IL2CPP fork now writes an early native-hook diagnostic file at
`BepInEx/plugins/OpenACAILoader/openacai-il2cpp-hook-state.json`. Use this with
Doorstop/hostfxr logs when testing crashes that happen before the OpenACAI
bridge can write its normal loader state.

## Local Build

From the loader repo:

```powershell
.\build.ps1 -NoUpdatePrebuilt
```

The script writes the experimental zip to `dist/OpenACAILoader.zip` and leaves `prebuilt/` unchanged unless `-NoUpdatePrebuilt` is omitted.

To inspect a local game install before or after package changes:

```powershell
.\tools\Inspect-SotfRuntime.ps1
.\tools\Inspect-SotfRuntime.ps1 -Json
```

This branch intentionally refuses `.NET 6` and non-hostfxr package builds.

## Native Hosting Notes

Microsoft's current native hosting guidance uses `hostfxr_initialize_for_runtime_config`, then obtains `hdt_load_assembly_and_get_function_pointer` through `hostfxr_get_runtime_delegate`.

Doorstop's legacy IL2CPP path used `coreclr_initialize` and
`coreclr_create_delegate` directly from `coreclr.dll`. The OpenACAI Doorstop
fork tries the `hostfxr` path first when `hostfxr_path` and
`runtime_config_path` are configured. If that explicit hostfxr path fails, the
fork does not silently fall through to the legacy CoreCLR path.

Source references:

- https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting
- `DOORSTOP_RUNTIME_NOTES.md`

