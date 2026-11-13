// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Modstoke.Localization
{
    /// <summary>A language by its code, its own name for itself, and its English name.</summary>
    public sealed record LanguageInfo(string Code, string NativeName, string EnglishName);

    /// <summary>
    /// The language Modstoke and its mods show text in. It follows the game's language unless the player picks
    /// one in the Loader settings ("language = ru"; "auto" follows the game again).
    /// </summary>
    public static class Languages
    {
        /// <summary>The language every text falls back to, and the one new text is written in first.</summary>
        public const string Default = "en";

        // The sixteen languages the game ships come first, in its order; a mod may ship any other code as well.
        private static readonly LanguageInfo[] Table =
        {
            new("en", "English", "English"),
            new("de", "Deutsch", "German"),
            new("fr", "Français", "French"),
            new("es", "Español", "Spanish"),
            new("it", "Italiano", "Italian"),
            new("pt", "Português", "Portuguese"),
            new("pl", "Polski", "Polish"),
            new("cs", "Čeština", "Czech"),
            new("ru", "Русский", "Russian"),
            new("tr", "Türkçe", "Turkish"),
            new("sv", "Svenska", "Swedish"),
            new("fi", "Suomi", "Finnish"),
            new("ja", "日本語", "Japanese"),
            new("ko", "한국어", "Korean"),
            new("zh-Hans", "简体中文", "Chinese (Simplified)"),
            new("zh-Hant", "繁體中文", "Chinese (Traditional)"),
            new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
            new("uk", "Українська", "Ukrainian"),
            new("nl", "Nederlands", "Dutch"),
            new("hu", "Magyar", "Hungarian"),
            new("th", "ไทย", "Thai"),
            new("vi", "Tiếng Việt", "Vietnamese")
        };

        private static readonly object Sync = new();
        private static readonly Dictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);
        private static string _current = Default;
        private static string[] _chain = { Default };

        /// <summary>The language in use, such as "en", "ru" or "zh-Hans".</summary>
        public static string Current => _current;

        /// <summary>Raised on the game thread after <see cref="Current"/> changes; redraw text then.</summary>
        public static event Action Changed;

        /// <summary>The languages the Loader has names for. A mod may ship any other code as well.</summary>
        public static IReadOnlyList<LanguageInfo> Known => Table;

        /// <summary>The name of a language for menus; unknown codes are shown as written.</summary>
        public static LanguageInfo Describe(string code)
        {
            var normal = Normalize(code);
            return Table.FirstOrDefault(language => string.Equals(language.Code, normal, StringComparison.OrdinalIgnoreCase))
                ?? new LanguageInfo(normal, normal, normal);
        }

        /// <summary>
        /// The code Modstoke uses for a language however the game, Steam or a file names it: the game's "zh"
        /// and "zh-CN" become "zh-Hans", "zh-TW" becomes "zh-Hant", "en-US" becomes "en". Brazilian Portuguese
        /// keeps "pt-BR", which falls back to "pt".
        /// </summary>
        public static string Normalize(string code)
        {
            var text = (code ?? "").Trim().Replace('_', '-');
            if (text.Length == 0) return Default;
            var parts = text.Split('-');
            var language = parts[0].ToLowerInvariant();
            var rest = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (language)
            {
                case "zh":
                    return rest is "hant" or "tw" or "hk" or "mo" ? "zh-Hant" : "zh-Hans";
                case "pt":
                    return rest is "br" ? "pt-BR" : "pt";
                default:
                    // Other regional variants share their language's file.
                    return language;
            }
        }

        // What decides the language; the Loader fills these in at startup.
        internal static Func<string> Preference = () => "auto";
        internal static Func<string> GameLanguage = () => Default;
        internal static Action<string> Warn = _ => { };

        /// <summary>The languages a text is looked up in, in order: the current one, its base, then English.</summary>
        internal static IReadOnlyList<string> Chain => _chain;

        /// <summary>Follows the setting and the game's language; called about once a second on the game thread.</summary>
        internal static void Refresh()
        {
            string wanted;
            try
            {
                var preference = (Preference() ?? "").Trim();
                wanted = Normalize(preference.Length == 0 || preference.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? GameLanguage()
                    : preference);
            }
            catch (Exception error)
            {
                Warn("The language could not be read (" + error.Message + "); text stays in " + Describe(_current).EnglishName + ".");
                return;
            }
            if (string.Equals(wanted, _current, StringComparison.OrdinalIgnoreCase)) return;
            Use(wanted);
            RaiseChanged();
        }

        internal static void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                Warn("A language change handler failed: " + error.Message);
            }
        }

        internal static void Use(string code)
        {
            var chain = new List<string> { code };
            var basic = BaseOf(code);
            // Chinese scripts are not each other's fallback; other regional codes fall back to their language.
            if (basic != "zh" && !chain.Contains(basic, StringComparer.OrdinalIgnoreCase)) chain.Add(basic);
            if (!chain.Contains(Default, StringComparer.OrdinalIgnoreCase)) chain.Add(Default);
            _chain = chain.ToArray();
            _current = code;
        }

        internal static string BaseOf(string code)
        {
            var dash = (code ?? "").IndexOf('-');
            return (dash < 0 ? code ?? "" : code.Substring(0, dash)).ToLowerInvariant();
        }

        /// <summary>The number and date formats of a language, or neutral ones when the system has none.</summary>
        internal static CultureInfo CultureOf(string code)
        {
            lock (Sync)
            {
                if (Cultures.TryGetValue(code, out var known)) return known;
                CultureInfo culture;
                try { culture = CultureInfo.GetCultureInfo(code); }
                catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
                return Cultures[code] = culture;
            }
        }
    }
}
