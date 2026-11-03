# Third-party notices

The managed compatibility source is derived from [HarmonyX](https://github.com/BepInEx/HarmonyX), tag v2.16.1, commit `02a98f540fa12ac4656f189a3cf67b0c96cc68f7`.

- HarmonyX: copyright (c) 2020 BepInEx, MIT license in `LICENSE`.
- Harmony: copyright (c) 2017 Andreas Pardeike, MIT license in `LICENSE.Harmony`.
- JetBrains annotations: copyright (c) 2016 JetBrains; the complete MIT notice remains in `Managed/Tools/JetBrains.Annotations.cs` and `LICENSE.JetBrains`.
- `Managed/Tools/TypeNameHelper.cs` and its adapted `Native/src/typename.rs` formatter: copyright (c) .NET Foundation, Apache License 2.0. The original attribution to Ben.Demystifier remains. The license text is in `Managed/License.txt` in the source tree and `LICENSE.Apache-2.0` in candidate output.
- `Native/src/prioritysort.rs`: introsort adapted from the .NET runtime's `ArraySortHelper<T>` at tag `v11.0.0-rc.2.26504.105`, copyright (c) .NET Foundation and Contributors, MIT license in `LICENSE.NET`.
- New Modstoke code: copyright (c) 2026 Alex Cooper, MIT license in `LICENSE.Modstoke`. Native algorithms adapted from HarmonyX retain the HarmonyX/Harmony notices above; the type-name formatter retains its separate Apache license.

Changes from upstream include the Rust planning adapter, native implementation, removal of the unused internal BinaryFormatter path, .NET 11 project targeting, native initialization diagnostics and preservation of type forwards on that target. The retained managed API, reflection utilities and emission machinery remain derived work, not a clean-room replacement.

MonoMod, Mono.Cecil and Il2CppInterop remain external dependencies supplied by the loader. Their existing licenses and release notices must accompany any loader distribution containing them.
