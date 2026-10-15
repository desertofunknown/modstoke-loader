// SPDX-License-Identifier: GPL-3.0-or-later
// Config contract adapted from BepInEx's AssemblyPatcher.cs, copyright BepInEx contributors.
// Copyright (C) 2026 Alex Cooper

using BepInEx.Configuration;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class AssemblyPatchingOptions
    {
        private readonly ConfigEntry<bool> _dumpAssemblies;
        private readonly ConfigEntry<bool> _loadDumpedAssemblies;
        private readonly ConfigEntry<bool> _breakBeforeLoad;

        internal AssemblyPatchingOptions()
        {
            var config = ConfigFile.CoreConfig;
            _dumpAssemblies = config.Bind("Preloader", "DumpAssemblies", false,
                "Save patched assemblies into BepInEx/DumpedAssemblies for inspection and debugging.");
            _loadDumpedAssemblies = config.Bind("Preloader", "LoadDumpedAssemblies", false,
                "Load patched assemblies from BepInEx/DumpedAssemblies instead of memory for debugging. Also enables DumpAssemblies.");
            _breakBeforeLoad = config.Bind("Preloader", "BreakBeforeLoadAssemblies", false,
                "Call Debugger.Break before loading patched assemblies, allowing a debugger to install breakpoints.");
        }

        internal bool DumpAssemblies => _dumpAssemblies.Value || _loadDumpedAssemblies.Value;
        internal bool LoadDumpedAssemblies => _loadDumpedAssemblies.Value;
        internal bool BreakBeforeLoad => _breakBeforeLoad.Value;
    }
}
