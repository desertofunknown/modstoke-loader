// SPDX-License-Identifier: GPL-3.0-or-later
// Legacy detour compatibility adapted from BepInEx's INativeDetour and BaseNativeDetour.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Hook;
using MonoMod.RuntimeDetour;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class OpenACAINativeDetourCompatibility : INativeDetour
    {
        private readonly NativeDetour _native;
        private MethodBase _trampolineMethod;

        private OpenACAINativeDetourCompatibility(NativeDetour native) => _native = native;

        internal static INativeDetour Create(IntPtr original, Delegate target) =>
            new OpenACAINativeDetourCompatibility(NativeDetourFactory.Create(original, target));

        public IntPtr OriginalMethodPtr => _native.Target;
        public IntPtr DetourMethodPtr => _native.Detour;
        public DetourConfig Config => null;

        public bool IsValid
        {
            get { lock (NativeDetourFactory.Sync) return _native.IsValid; }
        }

        public bool IsApplied
        {
            get { lock (NativeDetourFactory.Sync) return _native.IsApplied; }
        }

        public IntPtr TrampolinePtr
        {
            get { lock (NativeDetourFactory.Sync) return _native.OriginalTrampoline; }
        }

        public MethodBase GenerateTrampoline(MethodBase signature = null)
        {
            lock (NativeDetourFactory.Sync)
            {
                if (_trampolineMethod == null)
                {
                    _native.PrepareTrampoline();
                    _trampolineMethod = signature;
                }

                return _trampolineMethod;
            }
        }

        public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate =>
            _native.GenerateTrampoline<TDelegate>();

        public void Apply() => _native.Apply();
        public void Undo() => _native.Undo();
        public void Free() => _native.Dispose();
        public void Dispose() => _native.Dispose();
    }
}
