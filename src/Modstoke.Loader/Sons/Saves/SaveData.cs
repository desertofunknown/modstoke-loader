// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static class SaveData
    {
        public const string ServiceKey = "openacai.sons.saveData";

        internal const string OpenACAISavesDirectoryName = "Saves";
        internal const string OpenACAIModSaveDirectoryName = "Mods";
        private const string UnknownModId = "unknown-mod";

        private static readonly object SyncRoot = new object();
        private static SaveDataSnapshot _state = SaveDataSnapshot.CreateUnknown();

        public static SaveDataSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static ModSaveDataPath GetModPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.ModstokeModSaveDataDirectory, normalizedId);
            return new ModSaveDataPath(
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
            if (string.IsNullOrWhiteSpace(path.Directory))
            {
                throw new InvalidOperationException("Modstoke mod save-data directory is not available.");
            }

            Directory.CreateDirectory(path.Directory);
            Refresh();

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
                throw new InvalidOperationException("Modstoke mod save-data directory is not available.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod save-data file paths must be relative.", nameof(relativePath));
            }

            if (Path.DirectorySeparatorChar == '\\' && relativePath.Contains(':'))
            {
                throw new ArgumentException("Mod save-data file paths must not name alternate data streams.", nameof(relativePath));
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
            var snapshot = SaveDataSnapshot.Create();
            lock (SyncRoot)
            {
                _state = snapshot;
                Modding.SetService(ServiceKey, _state);
            }
        }

        internal static void RegisterMenuProvider()
        {
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK save data provider could not be registered.");
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
                throw new InvalidOperationException("Resolved save-data path escaped the Modstoke save-data root.");
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

        private static MenuContentSnapshot CreateContent()
        {
            Refresh();
            var snapshot = State;
            return new MenuContentSnapshot(
                "Sons Save Data",
                "Save-data isolation roots for mod-owned files.",
                new[]
                {
                    CreateStorageSection(snapshot),
                    CreateSdkSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateStorageSection(SaveDataSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Save Storage",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, "READY", "Public Modstoke save-data isolation service."),
                    Row("Isolation", snapshot.IsolationModel, "READY", "Mod-owned data is rooted under Modstoke UserData."),
                    PathRow("Game Saves", snapshot.GameSaveDataDirectory, false),
                    PathRow("Persistent Data", snapshot.PersistentDataDirectory, false),
                    PathRow("Modstoke Saves", snapshot.ModstokeSaveDataDirectory, snapshot.ModstokeSaveDataDirectoryExists),
                    PathRow("Mod Saves", snapshot.ModstokeModSaveDataDirectory, snapshot.ModstokeModSaveDataDirectoryExists),
                    PathRow("Legacy UserData", snapshot.LegacyUserDataDirectory, false)
                });
        }

        private static MenuSectionSnapshot CreateSdkSection(SaveDataSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Mod Pattern", ShortPath(snapshot.DefaultModDirectoryPattern), string.IsNullOrWhiteSpace(snapshot.DefaultModDirectoryPattern) ? "PENDING" : "INFO", snapshot.DefaultModDirectoryPattern),
                    Row("Normalize Mod ID", "Enabled", "READY", "Mod save directories are normalized to safe file-system ids."),
                    Row("Relative Files", "Required", "READY", "Mod file helpers reject rooted paths and path traversal."),
                    Row("Unknown Mod", UnknownModId, "INFO", "Fallback id used when a caller omits a mod id.")
                });
        }

        private static MenuRowSnapshot PathRow(string label, string path, bool knownExists)
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

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsSaveData;
            public string DisplayName => "Sons Save Data";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 719;
            public string Source => "Modstoke Loader";
            public string Description => "Show Sons save-data path isolation.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return SaveData.CreateContent();
            }
        }
    }

    public sealed class SaveDataSnapshot
    {
        private SaveDataSnapshot(
            string generatedUtc,
            string serviceKey,
            string isolationModel,
            string gameSaveDataDirectory,
            string persistentDataDirectory,
            string modstokeSaveDataDirectory,
            string modstokeModSaveDataDirectory,
            string legacyUserDataDirectory,
            bool modstokeSaveDataDirectoryExists,
            bool modstokeModSaveDataDirectoryExists,
            string defaultModDirectoryPattern)
        {
            GeneratedUtc = generatedUtc;
            ServiceKey = serviceKey;
            IsolationModel = isolationModel;
            GameSaveDataDirectory = gameSaveDataDirectory;
            PersistentDataDirectory = persistentDataDirectory;
            ModstokeSaveDataDirectory = modstokeSaveDataDirectory;
            ModstokeModSaveDataDirectory = modstokeModSaveDataDirectory;
            LegacyUserDataDirectory = legacyUserDataDirectory;
            ModstokeSaveDataDirectoryExists = modstokeSaveDataDirectoryExists;
            ModstokeModSaveDataDirectoryExists = modstokeModSaveDataDirectoryExists;
            DefaultModDirectoryPattern = defaultModDirectoryPattern;
        }

        public string GeneratedUtc { get; }
        public string ServiceKey { get; }
        public string IsolationModel { get; }
        public string GameSaveDataDirectory { get; }
        public string PersistentDataDirectory { get; }
        public string ModstokeSaveDataDirectory { get; }
        public string ModstokeModSaveDataDirectory { get; }
        public string LegacyUserDataDirectory { get; }
        public bool ModstokeSaveDataDirectoryExists { get; }
        public bool ModstokeModSaveDataDirectoryExists { get; }
        public string DefaultModDirectoryPattern { get; }

        [Obsolete("Use ModstokeSaveDataDirectory.")]
        public string OpenAcaiSaveDataDirectory => ModstokeSaveDataDirectory;
        [Obsolete("Use ModstokeModSaveDataDirectory.")]
        public string OpenAcaiModSaveDataDirectory => ModstokeModSaveDataDirectory;
        [Obsolete("Use ModstokeSaveDataDirectoryExists.")]
        public bool OpenAcaiSaveDataDirectoryExists => ModstokeSaveDataDirectoryExists;
        [Obsolete("Use ModstokeModSaveDataDirectoryExists.")]
        public bool OpenAcaiModSaveDataDirectoryExists => ModstokeModSaveDataDirectoryExists;

        internal static SaveDataSnapshot Create()
        {
            var environment = GameRuntime.Environment;
            var modstokeUserDataDirectory = !string.IsNullOrWhiteSpace(environment.ModstokeUserDataDirectory)
                ? environment.ModstokeUserDataDirectory
                : LoaderPaths.UserDataDirectory;
            var modstokeSaveDataDirectory = Combine(modstokeUserDataDirectory, SaveData.OpenACAISavesDirectoryName);
            var modstokeModSaveDataDirectory = Combine(modstokeSaveDataDirectory, SaveData.OpenACAIModSaveDirectoryName);

            SafeCreateDirectory(modstokeSaveDataDirectory);
            SafeCreateDirectory(modstokeModSaveDataDirectory);

            return new SaveDataSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                SaveData.ServiceKey,
                "openacai-userdata-per-mod",
                environment.SaveDataDirectory,
                environment.PersistentDataDirectory,
                modstokeSaveDataDirectory,
                modstokeModSaveDataDirectory,
                environment.LegacyUserDataDirectory,
                Directory.Exists(modstokeSaveDataDirectory),
                Directory.Exists(modstokeModSaveDataDirectory),
                Combine(modstokeModSaveDataDirectory, "{normalized-mod-id}"));
        }

        internal static SaveDataSnapshot CreateUnknown()
        {
            return new SaveDataSnapshot(
                null,
                SaveData.ServiceKey,
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

    public sealed class ModSaveDataPath
    {
        internal ModSaveDataPath(string modId, string normalizedModId, string directory, bool exists)
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
