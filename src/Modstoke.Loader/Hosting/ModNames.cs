// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text;

namespace Modstoke.Loader
{
    /// <summary>
    /// How players see a mod: a short name and a plain version, such as "Optics 0.0.45". The short name is the
    /// manifest's <c>displayName</c>, or its <c>name</c> without a leading "Modstoke"; a mod known only by its id
    /// (one a server needs that this game lacks) gets its id tidied. Versions drop build labels and commit hashes.
    /// </summary>
    public static class ModNames
    {
        private static readonly string[] NamePrefixes = { "Modstoke ", "OpenACAI " };
        private static readonly string[] IdPrefixes = { "com.openacai.sotf.", "modstoke.", "openacai." };

        /// <summary>The short name from a manifest's displayName, name and id, in that order of preference.</summary>
        public static string Short(string displayName, string name, string id)
        {
            if (!string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Tidy(id);
            var trimmed = name.Trim();
            foreach (var prefix in NamePrefixes)
                if (trimmed.Length > prefix.Length && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(prefix.Length).Trim();
            return trimmed;
        }

        /// <summary>The short name of the installed mod with this id, or the id tidied when it is not installed.</summary>
        public static string ForId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Unknown mod";
            var wanted = id.Trim();
            var entries = LoaderState.CurrentInventory?.Entries;
            if (entries != null)
                foreach (var entry in entries)
                    if (entry != null && string.Equals(entry.Id, wanted, StringComparison.OrdinalIgnoreCase))
                        return entry.DisplayName;
            return Tidy(wanted);
        }

        /// <summary>The version a player reads: "0.4.3" for "v0.4.3-20261114-gear-1+86f1223".</summary>
        public static string Version(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            var text = version.Trim();
            if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1])) text = text.Substring(1);
            int cut = text.IndexOfAny(new[] { '-', '+' });
            return cut > 0 ? text.Substring(0, cut) : text;
        }

        /// <summary>"Optics 0.0.45"; just the name when the version is unknown.</summary>
        public static string Label(string shortName, string version)
        {
            var name = string.IsNullOrWhiteSpace(shortName) ? "Unknown mod" : shortName.Trim();
            var plain = Version(version);
            return plain.Length == 0 ? name : name + " " + plain;
        }

        /// <summary>The label of the mod with this id: its installed short name, or its id tidied.</summary>
        public static string LabelForId(string id, string version) => Label(ForId(id), version);

        /// <summary>The label of the installed mod with this id, with its installed version.</summary>
        public static string LabelForId(string id) => Label(ForId(id), InstalledVersion(id));

        private static string InstalledVersion(string id)
        {
            var entries = LoaderState.CurrentInventory?.Entries;
            if (entries == null || string.IsNullOrWhiteSpace(id)) return "";
            foreach (var entry in entries)
                if (entry != null && string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                    return entry.Version ?? "";
            return "";
        }

        // "modstoke.optics" reads "Optics", "com.openacai.sotf.admin" "Admin", "author.cool_mod" "Cool Mod".
        private static string Tidy(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Unknown mod";
            var text = id.Trim();
            foreach (var prefix in IdPrefixes)
                if (text.Length > prefix.Length && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(prefix.Length);
                    break;
                }
            int dot = text.LastIndexOf('.');
            if (dot >= 0 && dot < text.Length - 1) text = text.Substring(dot + 1);
            var words = text.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return id.Trim();
            var result = new StringBuilder();
            foreach (var word in words)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
            }
            return result.ToString();
        }
    }
}
