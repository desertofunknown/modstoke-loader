// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Reflection;
using Modstoke.Localization;
using UnityEngine.Localization.Settings;

namespace Modstoke.Loader
{
    /// <summary>
    /// Connects the localization SDK to this install: the language follows the game's (or the "language"
    /// Loader setting), players' own language files live under UserData, and a mod's files are found from its
    /// package.
    /// </summary>
    internal static class LocalizationStartup
    {
        /// <summary>The owner id of the Loader's own text.</summary>
        internal const string LoaderOwner = "modstoke";

        private const string LanguageKey = "language";
        private static string _preference = "auto", _gameLanguage = Languages.Default;
        private static long _nextRefresh, _nextPreference;
        private static Strings _text;

        /// <summary>The Loader's own text.</summary>
        internal static Strings Text => _text ??= Strings.For(LoaderOwner);

        internal static void Initialize()
        {
            Languages.Warn = message => RuntimeEntryPoint.LogSource?.LogWarning(message);
            Languages.Preference = () => _preference;
            Languages.GameLanguage = () => _gameLanguage;
            Strings.OverrideRoot = Path.Combine(LoaderPaths.UserDataDirectory, "lang");
            Strings.Resolver = FindMod;
            Strings.OwnerOf = FindOwner;
            Strings.For(LoaderOwner).AddAssembly(typeof(LocalizationStartup).Assembly);
        }

        /// <summary>Follows the game's language and the Loader setting, about once a second.</summary>
        internal static void Tick()
        {
            var now = Environment.TickCount64;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 1000;
            if (now >= _nextPreference)
            {
                _nextPreference = now + 5000;
                _preference = RuntimePreferences.ReadText(LanguageKey, "auto");
            }
            ReadGameLanguage();
            Languages.Refresh();
        }

        /// <summary>The "language" console command: shows the language in use; "language reload" reads files again.</summary>
        internal static string RunCommand(string arguments)
        {
            var argument = (arguments ?? "").Trim();
            if (argument.Length > 0 && argument != "reload")
                return "Use \"language\" to see the language in use, or \"language reload\" to read language files again.";
            if (argument == "reload")
            {
                _nextPreference = 0;
                _nextRefresh = 0;
                Tick();
                Strings.ReloadAll();
            }
            var language = Languages.Describe(Languages.Current);
            var following = string.Equals(_preference, "auto", StringComparison.OrdinalIgnoreCase)
                ? "following the game"
                : "set by \"language\" in the Loader settings";
            return (argument == "reload" ? "Language files read again. " : "")
                + "Language: " + language.EnglishName + " (" + language.Code + "), " + following
                + ". The Loader has text in: " + string.Join(", ", Text.Provided()) + ".";
        }

        // The game's selected locale, once its localization has finished loading; asking earlier can block the
        // game thread on the load.
        private static void ReadGameLanguage()
        {
            try
            {
                if (!LocalizationSettings.HasSettings || !LocalizationSettings.InitializationOperation.IsDone) return;
                var locale = LocalizationSettings.SelectedLocale;
                if (locale != null) _gameLanguage = locale.Identifier.Code;
            }
            catch (Exception)
            {
                // The last known language stays in use.
            }
        }

        private static (string Folder, Assembly Assembly)? FindMod(string owner)
        {
            foreach (var candidate in ModHost.GetCandidates())
            {
                if (!string.Equals(candidate.ModId, owner, StringComparison.OrdinalIgnoreCase)) continue;
                Assembly assembly = null;
                if (candidate.AssemblyPath.Length > 0)
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                        if (string.Equals(LocationOf(loaded), candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase))
                        {
                            assembly = loaded;
                            break;
                        }
                return (candidate.PackagePath.Length > 0 ? Path.Combine(candidate.PackagePath, "lang") : null, assembly);
            }
            return null;
        }

        private static string FindOwner(Assembly assembly)
        {
            var location = LocationOf(assembly);
            if (location.Length == 0) return null;
            foreach (var candidate in ModHost.GetCandidates())
                if (string.Equals(candidate.AssemblyPath, location, StringComparison.OrdinalIgnoreCase))
                    return candidate.ModId;
            return null;
        }

        private static string LocationOf(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic ? "" : assembly.Location ?? "";
            }
            catch (NotSupportedException)
            {
                return "";
            }
        }
    }
}
