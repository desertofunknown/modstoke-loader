// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons save-data isolation service.

using System;
using System.IO;
using System.Text;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsSaveData
    {
        public const string ServiceKey = "openacai.sons.saveData";

        internal const string OpenACAISavesDirectoryName = "Saves";
        internal const string OpenACAIModSaveDirectoryName = "Mods";
        private const string UnknownModId = "unknown-mod";

        private static readonly object SyncRoot = new object();
        private static OpenACAISonsSaveDataSnapshot _state = OpenACAISonsSaveDataSnapshot.CreateUnknown();

        public static OpenACAISonsSaveDataSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static OpenACAISonsModSaveDataPath GetModPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.OpenAcaiModSaveDataDirectory, normalizedId);
            return new OpenACAISonsModSaveDataPath(
                modId,
                normalizedId,
                directory,
                Directory.Exists(directory));
        }

        public static string GetModDirectory(string modId)
        {
            return GetModPath(modId).Directory;
        }

        public static string EnsureModDirectory(string modId)
        {
            var path = GetModPath(modId);
            if (!string.IsNullOrWhiteSpace(path.Directory))
            {
                Directory.CreateDirectory(path.Directory);
                Refresh();
            }

            return path.Directory;
        }

        public static string GetModFilePath(string modId, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative file path is required.", nameof(relativePath));
            }

            var modDirectory = GetModDirectory(modId);
            if (string.IsNullOrWhiteSpace(modDirectory))
            {
                throw new InvalidOperationException("OpenACAI mod save-data directory is not available.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod save-data file paths must be relative.", nameof(relativePath));
            }

            var fullPath = Path.GetFullPath(Path.Combine(modDirectory, relativePath));
            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(modDirectory));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Mod save-data file paths must stay under the mod save-data directory.", nameof(relativePath));
            }

            return fullPath;
        }

        internal static void Refresh()
        {
            var snapshot = OpenACAISonsSaveDataSnapshot.Create();
            lock (SyncRoot)
            {
                _state = snapshot;
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        internal static void RegisterMenuProvider()
        {
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK save data provider could not be registered.");
            }
        }

        internal static string NormalizeModId(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return UnknownModId;
            }

            var trimmed = modId.Trim();
            var builder = new StringBuilder(trimmed.Length);
            var lastWasSeparator = false;
            for (var index = 0; index < trimmed.Length; index++)
            {
                var current = trimmed[index];
                var safe = IsSafeIdCharacter(current);
                if (safe)
                {
                    builder.Append(current);
                    lastWasSeparator = false;
                    continue;
                }

                if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }
            }

            var normalized = builder.ToString().Trim('.', '_', '-');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = UnknownModId;
            }

            return normalized.Length <= 96
                ? normalized
                : normalized.Substring(0, 96).Trim('.', '_', '-');
        }

        private static bool IsSafeIdCharacter(char value)
        {
            return (value >= 'a' && value <= 'z')
                || (value >= 'A' && value <= 'Z')
                || (value >= '0' && value <= '9')
                || value == '.'
                || value == '_'
                || value == '-';
        }

        private static string CombineUnderRoot(string root, string child)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, child ?? UnknownModId));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved save-data path escaped the OpenACAI save-data root.");
            }

            return fullPath;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            return path.EndsWith(separator, StringComparison.Ordinal)
                ? path
                : path + separator;
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            Refresh();
            var snapshot = State;
            return new OpenACAIMenuContentSnapshot(
                "Sons Save Data",
                "OpenACAI-owned save-data isolation roots for mod-owned files.",
                new[]
                {
                    CreateStorageSection(snapshot),
                    CreateSdkSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateStorageSection(OpenACAISonsSaveDataSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Save Storage",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, "READY", "Public OpenACAI save-data isolation service."),
                    Row("Isolation", snapshot.IsolationModel, "READY", "Mod-owned data is rooted under OpenACAI UserData."),
                    PathRow("Game Saves", snapshot.GameSaveDataDirectory, false),
                    PathRow("Persistent Data", snapshot.PersistentDataDirectory, false),
                    PathRow("OpenACAI Saves", snapshot.OpenAcaiSaveDataDirectory, snapshot.OpenAcaiSaveDataDirectoryExists),
                    PathRow("Mod Saves", snapshot.OpenAcaiModSaveDataDirectory, snapshot.OpenAcaiModSaveDataDirectoryExists),
                    PathRow("Legacy UserData", snapshot.LegacyUserDataDirectory, false)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateSdkSection(OpenACAISonsSaveDataSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Mod Pattern", ShortPath(snapshot.DefaultModDirectoryPattern), string.IsNullOrWhiteSpace(snapshot.DefaultModDirectoryPattern) ? "PENDING" : "INFO", snapshot.DefaultModDirectoryPattern),
                    Row("Normalize Mod ID", "Enabled", "READY", "Mod save directories are normalized to safe file-system ids."),
                    Row("Relative Files", "Required", "READY", "Mod file helpers reject rooted paths and path traversal."),
                    Row("Unknown Mod", UnknownModId, "INFO", "Fallback id used when a caller omits a mod id.")
                });
        }

        private static OpenACAIMenuRowSnapshot PathRow(string label, string path, bool knownExists)
        {
            var exists = knownExists;
            if (!exists && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    exists = Directory.Exists(path);
                }
                catch
                {
                    exists = false;
                }
            }

            return Row(label, ShortPath(path), string.IsNullOrWhiteSpace(path) ? "PENDING" : exists ? "READY" : "CHECK", path);
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsSaveData;
            public string DisplayName => "Sons Save Data";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 719;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons save-data path isolation.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsSaveData.CreateContent();
            }
        }
    }

    public sealed class OpenACAISonsSaveDataSnapshot
    {
        private OpenACAISonsSaveDataSnapshot(
            string generatedUtc,
            string serviceKey,
            string isolationModel,
            string gameSaveDataDirectory,
            string persistentDataDirectory,
            string openAcaiSaveDataDirectory,
            string openAcaiModSaveDataDirectory,
            string legacyUserDataDirectory,
            bool openAcaiSaveDataDirectoryExists,
            bool openAcaiModSaveDataDirectoryExists,
            string defaultModDirectoryPattern)
        {
            GeneratedUtc = generatedUtc;
            ServiceKey = serviceKey;
            IsolationModel = isolationModel;
            GameSaveDataDirectory = gameSaveDataDirectory;
            PersistentDataDirectory = persistentDataDirectory;
            OpenAcaiSaveDataDirectory = openAcaiSaveDataDirectory;
            OpenAcaiModSaveDataDirectory = openAcaiModSaveDataDirectory;
            LegacyUserDataDirectory = legacyUserDataDirectory;
            OpenAcaiSaveDataDirectoryExists = openAcaiSaveDataDirectoryExists;
            OpenAcaiModSaveDataDirectoryExists = openAcaiModSaveDataDirectoryExists;
            DefaultModDirectoryPattern = defaultModDirectoryPattern;
        }

        public string GeneratedUtc { get; }
        public string ServiceKey { get; }
        public string IsolationModel { get; }
        public string GameSaveDataDirectory { get; }
        public string PersistentDataDirectory { get; }
        public string OpenAcaiSaveDataDirectory { get; }
        public string OpenAcaiModSaveDataDirectory { get; }
        public string LegacyUserDataDirectory { get; }
        public bool OpenAcaiSaveDataDirectoryExists { get; }
        public bool OpenAcaiModSaveDataDirectoryExists { get; }
        public string DefaultModDirectoryPattern { get; }

        internal static OpenACAISonsSaveDataSnapshot Create()
        {
            var environment = OpenACAISons.Environment;
            var openAcaiUserDataDirectory = !string.IsNullOrWhiteSpace(environment.OpenAcaiUserDataDirectory)
                ? environment.OpenAcaiUserDataDirectory
                : OpenACAIPaths.UserDataDirectory;
            var openAcaiSaveDataDirectory = Combine(openAcaiUserDataDirectory, OpenACAISonsSaveData.OpenACAISavesDirectoryName);
            var openAcaiModSaveDataDirectory = Combine(openAcaiSaveDataDirectory, OpenACAISonsSaveData.OpenACAIModSaveDirectoryName);

            SafeCreateDirectory(openAcaiSaveDataDirectory);
            SafeCreateDirectory(openAcaiModSaveDataDirectory);

            return new OpenACAISonsSaveDataSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAISonsSaveData.ServiceKey,
                "openacai-userdata-per-mod",
                environment.SaveDataDirectory,
                environment.PersistentDataDirectory,
                openAcaiSaveDataDirectory,
                openAcaiModSaveDataDirectory,
                environment.LegacyUserDataDirectory,
                Directory.Exists(openAcaiSaveDataDirectory),
                Directory.Exists(openAcaiModSaveDataDirectory),
                Combine(openAcaiModSaveDataDirectory, "{normalized-mod-id}"));
        }

        internal static OpenACAISonsSaveDataSnapshot CreateUnknown()
        {
            return new OpenACAISonsSaveDataSnapshot(
                null,
                OpenACAISonsSaveData.ServiceKey,
                "openacai-userdata-per-mod",
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null);
        }

        private static string Combine(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
                ? null
                : Path.Combine(left, right);
        }

        private static void SafeCreateDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
            }
        }
    }

    public sealed class OpenACAISonsModSaveDataPath
    {
        internal OpenACAISonsModSaveDataPath(string modId, string normalizedModId, string directory, bool exists)
        {
            ModId = modId;
            NormalizedModId = normalizedModId;
            Directory = directory;
            Exists = exists;
        }

        public string ModId { get; }
        public string NormalizedModId { get; }
        public string Directory { get; }
        public bool Exists { get; }
    }
}
