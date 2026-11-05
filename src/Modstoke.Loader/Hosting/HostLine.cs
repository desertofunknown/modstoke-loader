// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Loader
{
    /// <summary>Splits a host request line into its optional <c>#id</c>, <c>confirm</c> and command text.</summary>
    internal static class HostLine
    {
        internal const int MaxLength = 2048;
        private const string ConfirmPrefix = "confirm ";

        internal static bool TryParse(string raw, out string text, out bool confirmed, out string requestId,
            out string error)
        {
            text = "";
            confirmed = false;
            requestId = null;
            error = null;
            var line = raw?.Trim() ?? "";
            if (line.Length > MaxLength)
            {
                error = "too-large";
                return false;
            }
            if (line.StartsWith('#'))
            {
                var end = line.IndexOfAny(new[] { ' ', '\t' });
                var id = end < 0 ? line[1..] : line[1..end];
                if (!IsId(id))
                {
                    error = "invalid-id";
                    return false;
                }
                requestId = id;
                line = end < 0 ? "" : line[(end + 1)..].TrimStart();
                if (line.Length == 0)
                {
                    error = "invalid-arguments";
                    return false;
                }
            }
            if (line.StartsWith(ConfirmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                confirmed = true;
                line = line[ConfirmPrefix.Length..].TrimStart();
            }
            text = line;
            return true;
        }

        internal static bool IsId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
                return false;
            foreach (var c in value)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-'))
                    return false;
            return true;
        }
    }
}
