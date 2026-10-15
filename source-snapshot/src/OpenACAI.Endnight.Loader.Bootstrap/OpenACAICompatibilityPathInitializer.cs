// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
// Preserves the BepInEx Paths layout while Bootstrap owns its initialization.

using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAICompatibilityPathInitializer
    {
        internal static void Initialize(
            Type pathsType,
            string executablePath,
            string bepInExRootPath,
            string managedPath,
            string[] dllSearchDirectories)
        {
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            var gameRootPath = Path.GetDirectoryName(executablePath);
            if (OperatingSystem.IsMacOS())
            {
                for (var level = 1; level < 4; level++)
                    gameRootPath = Path.GetDirectoryName(gameRootPath);
            }

            var gameDataPath = Path.Combine(gameRootPath, processName + "_Data");
            if (!Directory.Exists(gameDataPath))
                gameDataPath = Path.Combine(gameRootPath, "Data");

            if (!Directory.Exists(gameDataPath))
                throw new DirectoryNotFoundException("Failed to extract valid GameDataPath from executablePath: " + executablePath);

            managedPath ??= Path.Combine(gameDataPath, "Managed");
            bepInExRootPath ??= Path.Combine(gameRootPath, "BepInEx");
            var configPath = Path.Combine(bepInExRootPath, "config");
            var assemblyDirectory = Path.Combine(bepInExRootPath, "core");
            var assemblyPath = Path.Combine(assemblyDirectory, pathsType.Assembly.GetName().Name + ".dll");
            // Keep the compatibility API's ordinal, case-sensitive search-path deduplication.
            var searchPaths = (dllSearchDirectories ?? Array.Empty<string>())
                .Concat(new[] { managedPath })
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var values = new (string Name, object Value)[]
            {
                ("ExecutablePath", executablePath),
                ("ProcessName", processName),
                ("GameRootPath", gameRootPath),
                ("GameDataPath", gameDataPath),
                ("ManagedPath", managedPath),
                ("BepInExRootPath", bepInExRootPath),
                ("ConfigPath", configPath),
                ("BepInExConfigPath", Path.Combine(configPath, "BepInEx.cfg")),
                ("PluginPath", Path.Combine(bepInExRootPath, "plugins")),
                ("PatcherPluginPath", Path.Combine(bepInExRootPath, "patchers")),
                ("BepInExAssemblyDirectory", assemblyDirectory),
                ("BepInExAssemblyPath", assemblyPath),
                ("CachePath", Path.Combine(bepInExRootPath, "cache")),
                ("DllSearchPaths", searchPaths)
            };

            // Resolve the complete bridge before publishing any paths to compatibility consumers.
            var assignments = values.Select(value => (
                Setter: GetRequiredSetter(pathsType, value.Name, value.Value.GetType()),
                value.Value)).ToArray();
            foreach (var assignment in assignments)
                assignment.Setter.Invoke(null, new object[] { assignment.Value });
        }

        private static MethodInfo GetRequiredSetter(Type pathsType, string name, Type valueType)
        {
            var property = pathsType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property == null || property.PropertyType != valueType)
                throw new MissingMemberException(pathsType.FullName, name);

            var setter = property.GetSetMethod(true);
            if (setter == null || !setter.IsStatic)
                throw new MissingMethodException(pathsType.FullName, "set_" + name);

            return setter;
        }
    }
}
