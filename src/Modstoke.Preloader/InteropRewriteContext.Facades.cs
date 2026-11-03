// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropRewriteContext
    {
        private Dictionary<string, string> netstandardForwarders;

        internal InteropTypeContext TryGetTypeByName(string assemblyName, string fullName)
        {
            if (fullName == null) return null;
            var direct = TryGetAssemblyByName(assemblyName)?.TryGetTypeByName(fullName);
            if (direct != null || assemblyName != "netstandard") return direct;

            // The catalog contains netstandard 2.0 legacy forwarders, not guessed library owners.
            // A forwarder never supplies missing native identity or a replacement CLR implementation.
            if (!GetNetstandardForwarders().TryGetValue(fullName, out var implementation)) return null;
            var forwarded = TryGetAssemblyByName(implementation)?.TryGetTypeByName(fullName);
            return forwarded?.OriginalType == null ? null : forwarded;
        }

        private Dictionary<string, string> GetNetstandardForwarders()
        {
            if (netstandardForwarders != null) return netstandardForwarders;
            using var source = typeof(InteropRewriteContext).Assembly.GetManifestResourceStream(
                "Modstoke.Preloader.InteropNetstandardForwarders.json")
                ?? throw new InvalidDataException("The native facade forwarding catalog is missing.");
            return netstandardForwarders = JsonSerializer.Deserialize<Dictionary<string, string>>(source)
                ?? throw new InvalidDataException("The native facade forwarding catalog is empty.");
        }
    }
}
