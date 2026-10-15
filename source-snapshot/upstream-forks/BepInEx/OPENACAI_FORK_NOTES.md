# OpenACAI BepInEx Fork Notes

Developer: Alex Cooper

This fork is transitional source for OpenACAI Endnight Loader's Sons Of The Forest
runtime work. BepInEx remains credited upstream code under its existing license;
OpenACAI changes are intended to unblock the `.NET 10` LTS and `.NET 11`
preview runtime lines while the long-term loader is rewritten into
OpenACAI-owned modules.

## Current Port Focus

- Build the IL2CPP runtime path for the OpenACAI `.NET 11` preview branch.
- Preserve BepInEx plugin compatibility while OpenACAI rewrites the runtime,
  patch host, plugin inventory, resolver, interop repair, and state reporting
  into OpenACAI namespaces.
- Keep RedLoader/SonsSdk support as compatibility behavior in OpenACAI Endnight Loader,
  not as an external sidecar framework.

## Harmony And MonoMod Baseline

The current tested dependency set is:

- HarmonyX `2.16.1`
- MonoMod.Core `1.3.5-openacai-net11.3`
- MonoMod.RuntimeDetour `25.3.5-openacai-net11.3`
- MonoMod.Utils `25.0.13-openacai-net11.3`

MonoMod 25 currently gates official runtime support at `.NET 10`. The
OpenACAI `.NET 11` branch intentionally sets
`MonoMod_ReallySkipCheckTargetRuntime=true` for `net11.0` only, so runtime
detour behavior must be treated as experimental until validated in game.

## Version Branding Policy

Keep the BepInEx assembly/runtime version at `6.0.0` while the compatibility
surface remains active. BepInEx plugins compare against the core assembly
version, so changing that assembly version to the public OpenACAI release line
would make otherwise-compatible plugins look mismatched. The public loader
version is exposed through `OpenACAIBranding` from `OPENACAI_LOADER_VERSION` or
`BepInEx\plugins\OpenACAILoader\openacai-loader.manifest.json`; missing manifest
data now displays as `OpenACAI Endnight Loader (development)` rather than reusing the
BepInEx runtime version.

## API Replacements Used

- Old `MonoMod.Utils.PlatformHelper` usage is bridged through an OpenACAI
  compatibility type backed by MonoMod 25 `PlatformDetection` and
  `DynDll.DllExtension`.
- Old `DynDllImport`/`ResolveDynDllImports` patterns are replaced by explicit
  `DynDll.TryOpenLibrary`, `DynDll.GetExport`, and
  `Marshal.GetDelegateForFunctionPointer<T>()`.
- Old `AsDelegate<T>()` calls are replaced by
  `Marshal.GetDelegateForFunctionPointer<T>()`.
- Old `ReflectionHelper.IsCore` and `ReflectionHelper.IsMono` checks are
  replaced with target-framework checks or `PlatformDetection.Runtime`.
- RuntimeDetour 25's `IDetour` shape is adapted in the IL2CPP native-detour
  wrapper so OpenACAI can preserve the BepInEx helper methods while compiling
  against the modern dependency surface.

## Target Framework Policy

For the active Sons IL2CPP path, Core, Preloader.Core, and Unity.Common target
`netstandard2.1;net11.0`. OpenACAI-owned runtime implementation code should use
modern `.NET` targets. `net35` and `netstandard2.0` remain only where legacy
Mono/reference compatibility still needs them during the transition.
