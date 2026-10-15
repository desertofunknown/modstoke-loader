// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Unity player metadata initialization for the IL2CPP
// preloader path. This keeps BepInEx.Unity.Common.UnityInfo populated for
// compatibility consumers without calling its internal Initialize method.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIUnityInfoInitializer
    {
        private static readonly ManagerLookup[] ManagerVersionLookup =
        {
            new ManagerLookup("globalgamemanagers", 0x14, 0x30),
            new ManagerLookup("data.unity3d", 0x12),
            new ManagerLookup("mainData", 0x14)
        };

        internal static OpenACAIUnityInfoInitializationState Initialize(
            string gameRoot,
            Func<string, string, Type> loadRequiredType,
            Action<string> trace)
        {
            if (loadRequiredType == null)
            {
                throw new ArgumentNullException(nameof(loadRequiredType));
            }

            var state = new OpenACAIUnityInfoInitializationState
            {
                OpenAcaiOwned = true,
                BepInExUnityInfoInitializeUsed = false
            };

            var pathsType = loadRequiredType("BepInEx.Core", "BepInEx.Paths");
            state.ExecutablePath = GetStaticString(pathsType, "ExecutablePath");
            state.GameDataPath = GetStaticString(pathsType, "GameDataPath");
            state.ManagedPath = GetStaticString(pathsType, "ManagedPath");
            state.GameRootPath = GetStaticString(pathsType, "GameRootPath");

            if (string.IsNullOrWhiteSpace(state.ExecutablePath))
            {
                state.ExecutablePath = FindExecutablePath(gameRoot);
            }

            if (string.IsNullOrWhiteSpace(state.GameDataPath))
            {
                state.GameDataPath = FindGameDataPath(gameRoot, state.ExecutablePath);
            }

            state.ExecutablePath = Path.GetFullPath(state.ExecutablePath);
            state.GameDataPath = Path.GetFullPath(state.GameDataPath);
            state.ExecutableExists = File.Exists(state.ExecutablePath);
            state.GameDataPathExists = Directory.Exists(state.GameDataPath);

            if (!state.ExecutableExists)
            {
                throw new FileNotFoundException("OpenACAI could not resolve the Unity player executable.", state.ExecutablePath);
            }

            if (!state.GameDataPathExists)
            {
                throw new DirectoryNotFoundException("OpenACAI could not resolve the Unity game data path: " + state.GameDataPath);
            }

            var unityInfoType = loadRequiredType("BepInEx.Unity.Common", "BepInEx.Unity.Common.UnityInfo");
            state.UnityInfoType = unityInfoType.FullName ?? "";
            state.UnityInfoAlreadyInitialized = GetStaticBoolField(unityInfoType, "initialized");

            var versionProperty = unityInfoType.GetProperty("Version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (versionProperty == null)
            {
                throw new MissingMemberException(unityInfoType.FullName, "Version");
            }

            var version = DetermineVersion(state.GameDataPath, state.ExecutablePath, versionProperty.PropertyType, state);
            SetStaticProperty(unityInfoType, "PlayerPath", state.ExecutablePath);
            SetStaticProperty(unityInfoType, "GameDataPath", state.GameDataPath);
            SetStaticProperty(unityInfoType, "Version", version);
            SetStaticBoolField(unityInfoType, "initialized", true);

            state.UnityInfoPropertiesSet = true;
            state.UnityInfoInitializedFieldSet = GetStaticBoolField(unityInfoType, "initialized");
            state.UnityVersion = Convert.ToString(version) ?? "";
            state.Detail = "OpenACAI initialized UnityInfo compatibility metadata from " + state.UnityVersionSource + ".";
            trace?.Invoke("OpenACAI Unity info initialized: " + state.Detail);
            return state;
        }

        private static object DetermineVersion(
            string gameDataPath,
            string executablePath,
            Type unityVersionType,
            OpenACAIUnityInfoInitializationState state)
        {
            foreach (var lookup in ManagerVersionLookup)
            {
                var versionText = lookup.TryLookup(gameDataPath);
                if (string.IsNullOrWhiteSpace(versionText))
                {
                    continue;
                }

                var parsed = TryParseUnityVersion(unityVersionType, versionText);
                if (parsed != null)
                {
                    state.UnityVersionSource = lookup.FileName;
                    state.UnityVersionRaw = versionText;
                    return parsed;
                }
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(executablePath);
                foreach (var candidate in new[] { info.ProductVersion, info.FileVersion })
                {
                    var parsed = TryParseUnityVersion(unityVersionType, candidate);
                    if (parsed != null)
                    {
                        state.UnityVersionSource = "executable-version-info";
                        state.UnityVersionRaw = candidate ?? "";
                        return parsed;
                    }
                }
            }
            catch
            {
            }

            var managedPath = Path.Combine(gameDataPath, "Managed");
            if (File.Exists(Path.Combine(managedPath, "UnityEngine.CoreModule.dll")))
            {
                var parsed = TryCreateUnityVersion(unityVersionType, 2017, 0, 0);
                if (parsed != null)
                {
                    state.UnityVersionSource = "managed-coremodule-fallback";
                    state.UnityVersionRaw = "2017.0.0";
                    return parsed;
                }
            }

            state.UnityVersionSource = "default";
            state.UnityVersionRaw = "";
            return Activator.CreateInstance(unityVersionType);
        }

        private static object TryParseUnityVersion(Type unityVersionType, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                var parse = unityVersionType.GetMethod(
                    "Parse",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                if (parse != null)
                {
                    return parse.Invoke(null, new object[] { value.Trim() });
                }
            }
            catch
            {
            }

            return null;
        }

        private static object TryCreateUnityVersion(Type unityVersionType, int major, int minor, int build)
        {
            try
            {
                var ctor = unityVersionType.GetConstructor(new[] { typeof(ushort), typeof(ushort), typeof(ushort) });
                if (ctor != null)
                {
                    return ctor.Invoke(new object[] { (ushort)major, (ushort)minor, (ushort)build });
                }
            }
            catch
            {
            }

            return null;
        }

        private static string FindExecutablePath(string gameRoot)
        {
            var candidate = Path.Combine(gameRoot ?? "", "SonsOfTheForest.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(gameRoot))
            {
                foreach (var executable in Directory.GetFiles(gameRoot, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (!Path.GetFileName(executable).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                    {
                        return executable;
                    }
                }
            }

            return candidate;
        }

        private static string FindGameDataPath(string gameRoot, string executablePath)
        {
            var root = string.IsNullOrWhiteSpace(gameRoot)
                ? Path.GetDirectoryName(executablePath)
                : gameRoot;
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            var processData = Path.Combine(root ?? "", processName + "_Data");
            if (Directory.Exists(processData))
            {
                return processData;
            }

            return Path.Combine(root ?? "", "Data");
        }

        private static string GetStaticString(Type type, string propertyName)
        {
            try
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return Convert.ToString(property?.GetValue(null)) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static void SetStaticProperty(Type type, string propertyName, object value)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property == null)
            {
                throw new MissingMemberException(type.FullName, propertyName);
            }

            var setter = property.GetSetMethod(true);
            if (setter == null)
            {
                throw new MissingMethodException(type.FullName, "set_" + propertyName);
            }

            setter.Invoke(null, new[] { value });
        }

        private static bool GetStaticBoolField(Type type, string fieldName)
        {
            try
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                return field != null && Convert.ToBoolean(field.GetValue(null));
            }
            catch
            {
                return false;
            }
        }

        private static void SetStaticBoolField(Type type, string fieldName, bool value)
        {
            var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                throw new MissingFieldException(type.FullName, fieldName);
            }

            field.SetValue(null, value);
        }

        private sealed class ManagerLookup
        {
            private readonly int[] _offsets;

            internal ManagerLookup(string fileName, params int[] offsets)
            {
                FileName = fileName;
                _offsets = offsets ?? Array.Empty<int>();
            }

            internal string FileName { get; }

            internal string TryLookup(string gameDataPath)
            {
                try
                {
                    var path = Path.Combine(gameDataPath ?? "", FileName);
                    if (!File.Exists(path))
                    {
                        return "";
                    }

                    using (var stream = File.OpenRead(path))
                    {
                        foreach (var offset in _offsets)
                        {
                            var value = ReadNullTerminatedAscii(stream, offset);
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                return value;
                            }
                        }
                    }
                }
                catch
                {
                }

                return "";
            }

            private static string ReadNullTerminatedAscii(Stream stream, int offset)
            {
                if (stream == null || offset < 0 || offset >= stream.Length)
                {
                    return "";
                }

                stream.Position = offset;
                var builder = new StringBuilder();
                while (stream.Position < stream.Length)
                {
                    var value = stream.ReadByte();
                    if (value <= 0)
                    {
                        break;
                    }

                    builder.Append((char)value);
                    if (builder.Length > 64)
                    {
                        break;
                    }
                }

                return builder.ToString();
            }
        }
    }

    internal sealed class OpenACAIUnityInfoInitializationState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExUnityInfoInitializeUsed { get; set; }
        internal bool UnityInfoAlreadyInitialized { get; set; }
        internal bool UnityInfoPropertiesSet { get; set; }
        internal bool UnityInfoInitializedFieldSet { get; set; }
        internal bool ExecutableExists { get; set; }
        internal bool GameDataPathExists { get; set; }
        internal string ExecutablePath { get; set; } = "";
        internal string GameRootPath { get; set; } = "";
        internal string GameDataPath { get; set; } = "";
        internal string ManagedPath { get; set; } = "";
        internal string UnityInfoType { get; set; } = "";
        internal string UnityVersion { get; set; } = "";
        internal string UnityVersionRaw { get; set; } = "";
        internal string UnityVersionSource { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
