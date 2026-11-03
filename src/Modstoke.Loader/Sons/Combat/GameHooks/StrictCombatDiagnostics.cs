// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons.Combat
{
    /// <summary>
    /// Logs the first unexpected exception per fail-closed site. Never throws, so it is safe
    /// inside native callbacks; the caller keeps its own failure status.
    /// </summary>
    internal static class StrictCombatDiagnostics
    {
        internal static void ReportOnce(ref int reported, string site, Exception error)
        {
            if (Interlocked.Exchange(ref reported, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Strict combat " + site + " failed: "
                    + error?.GetType().Name + ": " + error?.Message);
            }
            catch { }
        }
    }
}
