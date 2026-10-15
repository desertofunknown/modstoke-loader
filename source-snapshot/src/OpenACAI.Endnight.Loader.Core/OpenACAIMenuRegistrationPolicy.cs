// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using SUI;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIMenuRegistrationPolicy
    {
        private static readonly Func<string, bool> Filter = ShouldRegister;

        internal static void Install()
        {
            MenuButtonRegistrationPolicy.SetFilter(Filter);
        }

        internal static bool ShouldRegister(string id)
        {
            if (OpenACAILegacyRedLoaderCompatibility.IsRedLoaderModsButtonEnabled()
                || !string.Equals(id, "MODS", StringComparison.OrdinalIgnoreCase))
                return true;

            OpenACAIEndnightRuntime.TraceCompat("Suppressed legacy RedLoader MODS button registration by design: " + id);
            return false;
        }
    }
}
