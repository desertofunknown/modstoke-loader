// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class AssemblyPatcherDiagnostics
    {
        internal static void Write(Action write)
        {
            try
            {
                write();
            }
            catch
            {
                // Formatting and log listeners must not change patching or cleanup outcomes.
            }
        }
    }
}
