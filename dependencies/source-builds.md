# Maintained dependency builds

The retained dependency inventory remains the release authority. The source
builds below are staged candidates, not automatic replacements for that cache.

| Dependency | Maintained fork | Pinned source | License |
| --- | --- | --- | --- |
| Alt.Json | [openacai-newtonsoft-json](https://github.com/desertofunknown/openacai-newtonsoft-json) | Newtonsoft.Json 13.0.4, `4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a` | MIT |
| Dobby | [openacai-dobby](https://github.com/desertofunknown/openacai-dobby) | BepInEx v1.0.5, `888d971214900374edbca6206fad6ded8a2c1311` | Apache-2.0 |

Clone these into `Forks/ReleaseDependencies/Alt.Json` and
`Forks/ReleaseDependencies/Dobby` alongside this suite's parent directory.
Check out the listed revisions. Run `tools/Build-MaintainedDependencies.ps1`
with a new absolute `-OutputDirectory`. It requires the suite's pinned .NET
SDK and the MSVC CMake wrapper, or an explicit `-DotNet` host.

The recipe refuses dirty or unexpected source checkouts, exports the pinned
commits and builds outside them. Alt.Json uses the upstream .NET 6 feature
configuration with a mechanical `Newtonsoft.Json` to `Alt.Json` substitution
in the staged C# source. Its unsigned assembly version remains `1.0.0.0` for
compatibility; its file version identifies the actual 13.0.4 source. It is a
library, without the old binary's console entry point. The upstream MIT
copyright remains intact. Dobby uses BepInEx's Windows x64 release features.

Outputs include the DLLs, license notices and `build-provenance.json`.
The receipt records the recipe and generated source/project hashes. Managed
debug information embeds the rewritten sources instead of linking them to an
unrelated enclosing repository; the informational version uses the pinned source.
Native compiler versions and build timestamps can change hashes; this is not a claim
of bit-for-bit reproducibility. Existing Dobby compiler warnings are not hidden.

Before promoting a candidate, review its source and public API compatibility,
compile affected consumers, and verify the supported startup and hook lifecycle.
Keep the previous cache available for rollback. Update the inventory and signing
inputs deliberately only after acceptance. Do not claim a new hash proves a
false positive or use repackaging to bypass a release quarantine. Antivirus
vendor review and source provenance are separate checks.
