// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text;

namespace Modstoke.Loader
{
    /// <summary>Normalizes free text from a native manifest before it is shown or published.</summary>
    internal static class ModManifestText
    {
        internal const int MaxDescriptionLength = 1024;

        internal static string Description(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var text = new StringBuilder(Math.Min(value.Length, MaxDescriptionLength));
            foreach (var c in value.Trim())
            {
                if (text.Length == MaxDescriptionLength)
                    break;
                // Line breaks become spaces; other control characters are dropped.
                if (c is '\r' or '\n' or '\t')
                    text.Append(' ');
                else if (!char.IsControl(c))
                    text.Append(c);
            }
            return text.Length == 0 ? null : text.ToString();
        }
    }

    /// <summary>
    /// Checks a manifest's <c>minLoaderVersion</c>. Versions are major.minor.patch with an optional -prerelease
    /// and +build. A minimum without a prerelease ignores the Loader's own prerelease tag, so "0.4.3" accepts
    /// "0.4.3-dev"; a minimum with one follows SemVer precedence.
    /// </summary>
    internal static class LoaderVersionRequirement
    {
        internal static bool TryCheck(string minimum, string loader, out string issueCode, out string issue)
        {
            issueCode = null;
            issue = null;
            if (string.IsNullOrWhiteSpace(minimum))
                return true;
            if (!TryParse(minimum, out var required))
            {
                issueCode = "loader-version-invalid";
                issue = "minLoaderVersion '" + minimum + "' is not a version like 0.4.2.";
                return false;
            }
            if (!TryParse(loader, out var current))
                return true;
            var core = Compare(current.Core, required.Core);
            var satisfied = core > 0 || core == 0
                && (required.Prerelease == null || ComparePrerelease(current.Prerelease, required.Prerelease) >= 0);
            if (!satisfied)
            {
                issueCode = "loader-too-old";
                issue = "Needs Modstoke Loader " + minimum.Trim() + " or newer; this is " + loader + ".";
            }
            return satisfied;
        }

        /// <summary>
        /// Whether a mod's version meets a dependency's: the same text, or "&gt;=" and a version, met by that version
        /// and every later one under the minLoaderVersion rules, so "&gt;=0.3.0" accepts 0.3.0, 0.3.0-dev and 0.4.1.
        /// </summary>
        internal static bool Satisfies(string version, string requirement)
        {
            if (string.Equals(version, requirement, StringComparison.Ordinal))
                return true;
            if (!IsMinimum(requirement) || !TryParse(requirement[2..], out var required) || !TryParse(version, out var current))
                return false;
            var core = Compare(current.Core, required.Core);
            return core > 0 || core == 0 && (required.Prerelease == null || ComparePrerelease(current.Prerelease, required.Prerelease) >= 0);
        }

        internal static bool IsMinimum(string requirement) => requirement != null && requirement.StartsWith(">=", StringComparison.Ordinal);

        internal static bool IsValidMinimum(string requirement) => IsMinimum(requirement) && TryParse(requirement[2..], out _);

        private static bool TryParse(string text, out (ulong[] Core, string Prerelease) version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            var plus = text.IndexOf('+');
            if (plus >= 0)
                text = text[..plus];
            var dash = text.IndexOf('-');
            var prerelease = dash >= 0 ? text[(dash + 1)..] : null;
            var parts = (dash >= 0 ? text[..dash] : text).Split('.');
            if (parts.Length != 3 || prerelease is { Length: 0 })
                return false;
            var core = new ulong[3];
            for (int i = 0; i < 3; i++)
            {
                if (!ulong.TryParse(parts[i], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out core[i]))
                    return false;
            }
            version = (core, prerelease);
            return true;
        }

        private static int Compare(ulong[] left, ulong[] right)
        {
            for (int i = 0; i < 3; i++)
            {
                if (left[i] != right[i])
                    return left[i].CompareTo(right[i]);
            }
            return 0;
        }

        // SemVer 2.0 precedence: a release outranks any prerelease, then identifiers compare left to right.
        private static int ComparePrerelease(string left, string right)
        {
            if (left == null)
                return right == null ? 0 : 1;
            if (right == null)
                return -1;
            var a = left.Split('.');
            var b = right.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var aNumeric = IsDigits(a[i]);
                var bNumeric = IsDigits(b[i]);
                // Numeric identifiers rank below text ones; digit strings of any length compare by value.
                int result = aNumeric && bNumeric ? CompareDigits(a[i], b[i])
                    : aNumeric ? -1
                    : bNumeric ? 1
                    : string.CompareOrdinal(a[i], b[i]);
                if (result != 0)
                    return result;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static bool IsDigits(string value)
        {
            if (value.Length == 0)
                return false;
            foreach (var c in value)
            {
                if (c is < '0' or > '9')
                    return false;
            }
            return true;
        }

        private static int CompareDigits(string left, string right)
        {
            left = left.TrimStart('0');
            right = right.TrimStart('0');
            return left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
        }
    }
}
