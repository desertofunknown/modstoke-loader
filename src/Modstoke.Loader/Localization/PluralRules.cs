// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Localization
{
    /// <summary>
    /// Which plural form a whole number takes in a language, by the Unicode CLDR categories ("one", "few",
    /// "many", "other"). Languages not listed have a single form.
    /// </summary>
    internal static class PluralRules
    {
        internal const string Other = "other";

        internal static bool IsCategory(string name)
            => name is "zero" or "one" or "two" or "few" or "many" or Other;

        internal static string Category(string language, long count)
        {
            var n = Math.Abs(count);
            switch (Languages.BaseOf(language))
            {
                case "en": case "de": case "es": case "it": case "nl": case "sv": case "fi": case "da": case "nb":
                case "el": case "hu": case "tr": case "bg":
                    return n == 1 ? "one" : Other;
                case "fr": case "pt":
                    return n is 0 or 1 ? "one" : Other;
                case "ru": case "uk":
                    if (n % 10 == 1 && n % 100 != 11) return "one";
                    return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
                case "pl":
                    if (n == 1) return "one";
                    return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
                case "cs": case "sk":
                    return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : Other;
                default:
                    return Other;
            }
        }
    }
}
