// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Il2CppInterop.Common.XrefScans;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropScanState
    {
        internal bool HasObfuscatedMethods { get; set; }
        internal HashSet<long> LiveMethods { get; private set; } = new();
        internal IDictionary<long, List<XrefInstance>> Callers { get; private set; } = new Dictionary<long, List<XrefInstance>>();

        internal long MetadataInitRva { get; private set; }

        internal void Publish(IDictionary<long, List<XrefInstance>> callerMap, HashSet<long> reachableMethods, long metadataInitRva)
        {
            Callers = callerMap;
            LiveMethods = reachableMethods;
            MetadataInitRva = metadataInitRva;
        }

        internal void Clear(ref ExceptionDispatchInfo failure)
        {
            MetadataInitRva = 0;
            InteropConversion.Cleanup(ref failure, "clear interop obfuscated-method flag",
                () => HasObfuscatedMethods = false);
            InteropConversion.Cleanup(ref failure, "clear interop caller map",
                () => Callers = new Dictionary<long, List<XrefInstance>>());
            InteropConversion.Cleanup(ref failure, "clear interop live-method set",
                () => LiveMethods = new HashSet<long>());
        }
    }
}
