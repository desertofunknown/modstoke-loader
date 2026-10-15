// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropGenerationDiagnostics
    {
        internal static string Describe(Exception error)
        {
            try { return error.ToString(); }
            catch { return error.GetType().FullName ?? error.GetType().Name; }
        }

        internal static string Message(Exception error)
        {
            try { return error.Message ?? ""; }
            catch { return "Exception message unavailable (" + error.GetType().Name + ")."; }
        }

        internal static void Log(LogLevel level, string message, Exception error = null, ManualLogSource source = null)
        {
            try
            {
                var detail = error == null ? message : message + Environment.NewLine + error;
                (source ?? OpenACAIPreloaderLog.Log).Log(level, detail);
            }
            catch
            {
                // Diagnostics must not change conversion, recovery or cleanup outcomes.
            }
        }
    }
}
