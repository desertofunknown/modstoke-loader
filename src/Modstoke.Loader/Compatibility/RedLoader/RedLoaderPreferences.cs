// SPDX-License-Identifier: GPL-3.0-or-later
// Preference behavior adapted from RedLoader, derived from MelonLoader.
// Copyright (c) RedLoader and MelonLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RedLoader;
using RedLoader.Preferences;
using Tomlet;
using PreferenceFile = RedLoader.Preferences.IO.File;

namespace Modstoke.Loader;

internal static class RedLoaderPreferences
{
    internal static void Load()
    {
        var files = ConfigSystem.PrefFiles.ToArray();
        var categories = ConfigSystem.Categories.ToArray();
        var reflectiveCategories = ConfigSystem.ReflectiveCategories.ToArray();

        try
        {
            ConfigSystem.DefaultFile.LegacyLoad();
        }
        catch (Exception error)
        {
            RLog.Error($"Error while Loading Legacy Preferences from {ConfigSystem.DefaultFile.LegacyFilePath}: {error}");
            ConfigSystem.DefaultFile.WasError = true;
        }

        TryLoadFile(ConfigSystem.DefaultFile);
        foreach (var file in files)
            TryLoadFile(file);

        foreach (var category in categories)
        {
            if (category.Entries.Count == 0)
                continue;
            var file = category.File ?? ConfigSystem.DefaultFile;
            if (file.WasError)
                continue;
            LoadEntries(category, file);
        }

        foreach (var category in reflectiveCategories)
        {
            var file = category.File ?? ConfigSystem.DefaultFile;
            if (file.WasError)
                continue;
            if (LoadReflectiveCategory(category, file, out var hadTable) && hadTable)
                ConfigSystem.OnPreferencesLoaded.Invoke(file.FilePath);
        }

        RLog.Msg("Preferences Loaded!");
    }

    internal static void Save()
    {
        var files = ConfigSystem.PrefFiles.ToArray();
        var categories = ConfigSystem.Categories.ToArray();
        var reflectiveCategories = ConfigSystem.ReflectiveCategories.ToArray();
        var failedFiles = new HashSet<PreferenceFile>();

        foreach (var category in categories)
        {
            var file = category.File ?? ConfigSystem.DefaultFile;
            if (file.WasError || failedFiles.Contains(file))
                continue;
            try { WriteEntries(category, file, false); }
            catch (Exception error)
            {
                failedFiles.Add(file);
                ReportCategoryFailure(file, category.Identifier, "Saving", error);
            }
        }
        foreach (var category in reflectiveCategories)
        {
            var file = category.File ?? ConfigSystem.DefaultFile;
            if (file.WasError || failedFiles.Contains(file))
                continue;
            try { file.document.PutValue(category.Identifier, category.Save()); }
            catch (Exception error)
            {
                failedFiles.Add(file);
                ReportCategoryFailure(file, category.Identifier, "Saving", error);
            }
        }

        if (!failedFiles.Contains(ConfigSystem.DefaultFile))
            TrySaveFile(ConfigSystem.DefaultFile);

        foreach (var file in files)
        {
            // Do not publish a partially serialized file or report it as saved.
            if (failedFiles.Contains(file) || !TrySaveFile(file))
                continue;
            ConfigSystem.OnPreferencesSaved.Invoke(file.FilePath);
        }

        RLog.Msg("Preferences Saved!");
    }

    internal static void Reload(PreferenceFile file, bool printMessage)
    {
        file ??= ConfigSystem.DefaultFile;
        var categories = ConfigSystem.Categories.ToArray();
        var reflectiveCategories = ConfigSystem.ReflectiveCategories.ToArray();
        if (!TryLoadFile(file))
            return;

        foreach (var category in categories)
        {
            var current = category.File ?? ConfigSystem.DefaultFile;
            if (current != file || category.Entries.Count == 0)
                continue;
            if (!LoadEntries(category, current))
                return;
        }

        foreach (var category in reflectiveCategories)
        {
            if ((category.File ?? ConfigSystem.DefaultFile) != file)
                continue;
            if (!LoadReflectiveCategory(category, file, out _))
                return;
        }

        if (printMessage)
            RLog.MsgDirect($"MelonPreferences Loaded from {file.FilePath}");
        ConfigSystem.OnPreferencesLoaded.Invoke(file.FilePath);
    }

    internal static void SaveCategory(ConfigCategory category, bool printMessage)
    {
        var file = category.File ?? ConfigSystem.DefaultFile;
        WriteEntries(category, file, true);
        SaveCategoryFile(file, printMessage);
    }

    internal static void SaveReflectiveCategory(ReflectiveConfigCategory category, bool printMessage)
    {
        var file = category.File ?? ConfigSystem.DefaultFile;
        file.document.PutValue(category.Identifier, category.Save());
        SaveCategoryFile(file, printMessage);
    }

    private static void WriteEntries(ConfigCategory category, PreferenceFile file, bool useCategoryInline)
    {
        foreach (var entry in category.Entries.ToArray())
        {
            var value = entry.GetValueAsString();
            if (!(entry.DontSaveDefault && value == entry.GetDefaultValueAsString()) && value != null)
                file.InsertIntoDocument(category.Identifier, entry.Identifier, entry.Save(),
                    useCategoryInline && category.IsInlined);
        }
    }

    private static bool LoadEntries(ConfigCategory category, PreferenceFile file)
    {
        try
        {
            foreach (var entry in category.Entries.ToArray())
                file.SetupEntryFromRawValue(entry);
            return true;
        }
        catch (Exception error)
        {
            ReportCategoryFailure(file, category.Identifier, "Loading", error);
            return false;
        }
    }

    private static bool LoadReflectiveCategory(ReflectiveConfigCategory category, PreferenceFile file, out bool hadTable)
    {
        hadTable = false;
        try
        {
            var table = file.TryGetCategoryTable(category.Identifier);
            hadTable = table != null;
            if (hadTable)
                category.Load(table);
            else
                category.LoadDefaults();
            return true;
        }
        catch (Exception error)
        {
            ReportCategoryFailure(file, category.Identifier, "Loading", error);
            return false;
        }
    }

    private static void ReportCategoryFailure(PreferenceFile file, string category, string operation, Exception error)
    {
        file.WasError = true;
        try { RLog.Error($"Error while {operation} Preferences category {category} in {file.FilePath}: {error}"); }
        catch { /* A reported conversion failure must not stop other files. */ }
    }

    private static bool TryLoadFile(PreferenceFile file)
    {
        try
        {
            file.Load();
            return !file.WasError;
        }
        catch (Exception error)
        {
            RLog.Error($"Error while Loading Preferences from {file.FilePath}: {error}");
            file.WasError = true;
            return false;
        }
    }

    private static bool TrySaveFile(PreferenceFile file)
    {
        try
        {
            file.Save();
            // Legacy notifications treat an intentionally skipped write as a completed call.
            return true;
        }
        catch (Exception error)
        {
            RLog.Error($"Error while Saving Preferences to {file.FilePath}: {error}");
            file.WasError = true;
            return false;
        }
    }

    private static void SaveCategoryFile(PreferenceFile file, bool printMessage)
    {
        TrySaveFile(file);

        // Category saves historically notify even after a skipped or failed write.
        if (printMessage)
            RLog.Msg($"MelonPreferences Saved to {file.FilePath}");
        ConfigSystem.OnPreferencesSaved.Invoke(file.FilePath);
    }

    internal static void ReadLegacyFile(PreferenceFile file)
    {
        if (string.IsNullOrEmpty(file.LegacyFilePath) || !System.IO.File.Exists(file.LegacyFilePath))
            return;
        var contents = System.IO.File.ReadAllText(file.LegacyFilePath);
        string category = null;
        foreach (var line in contents.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith(";", StringComparison.Ordinal))
                continue;
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)
                && !trimmed.Contains('='))
            {
                category = trimmed.Substring(1, trimmed.Length - 2).Replace(" ", "");
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0 || string.IsNullOrEmpty(category))
                continue;
            var key = line.Substring(0, equals).Trim();
            var value = line.Substring(equals + 1).TrimEnd('\r');
            if (key.Length == 0 || value.Length == 0)
                continue;
            if (bool.TryParse(value, out var boolean))
                file.InsertIntoDocument(category, key, TomletMain.ValueFrom(boolean));
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                file.InsertIntoDocument(category, key, TomletMain.ValueFrom(integer));
            else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                file.InsertIntoDocument(category, key, TomletMain.ValueFrom(number));
            else
                file.InsertIntoDocument(category, key, TomletMain.ValueFrom(value));
        }
    }

    internal static void ReadFile(PreferenceFile file)
    {
        if (!System.IO.File.Exists(file.FilePath))
            return;
        try
        {
            var document = TomlParser.ParseFile(file.FilePath);
            file.document = document;
            file.WasError = false;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static void WriteFile(PreferenceFile file)
    {
        if (file.WasError || !file.ShouldSave)
            return;
        lock (file)
        {
            var path = Path.GetFullPath(file.FilePath);
            var contents = file.document.SerializedValue;
            var original = ReadExistingText(path);
            if (!string.Equals(original, contents, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                file.IsSaving = true;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
                            writer.Write(contents);
                        stream.Flush(flushToDisk: true);
                    }
                    if (!string.Equals(ReadExistingText(path), original, StringComparison.Ordinal))
                        throw new IOException("The preferences file changed on disk. Reload before saving.");
                    if (original == null)
                        System.IO.File.Move(temporary, path);
                    else
                        System.IO.File.Replace(temporary, path, null);
                }
                finally
                {
                    file.IsSaving = false;
                    try { System.IO.File.Delete(temporary); }
                    catch (Exception) { /* Preserve the original write failure. */ }
                }
            }
            if (!string.IsNullOrEmpty(file.LegacyFilePath)
                && !string.Equals(Path.GetFullPath(file.LegacyFilePath), path, StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(file.LegacyFilePath))
                System.IO.File.Delete(file.LegacyFilePath);
        }
    }

    private static string ReadExistingText(string path)
    {
        try { return System.IO.File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
