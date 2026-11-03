// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using SUI;

namespace Modstoke.Loader
{
    internal static class MenuRegistrationPolicy
    {
        private static readonly Func<string, bool> Filter = ShouldRegister;

        internal static void Install()
        {
            MenuButtonRegistrationPolicy.SetFilter(Filter);
        }

        internal static bool ShouldRegister(string id)
        {
            if (LegacyRedLoaderCompatibility.IsRedLoaderModsButtonEnabled()
                || !string.Equals(id, "MODS", StringComparison.OrdinalIgnoreCase))
                return true;

            RuntimeEntryPoint.TraceCompat("Suppressed legacy RedLoader MODS button registration by design: " + id);
            return false;
        }
    }
}
