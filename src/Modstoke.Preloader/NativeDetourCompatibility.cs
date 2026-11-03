// SPDX-License-Identifier: GPL-3.0-or-later
// Legacy detour compatibility adapted from BepInEx's INativeDetour and BaseNativeDetour.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Hook;
using MonoMod.RuntimeDetour;

namespace Modstoke.Preloader
{
    internal sealed class NativeDetourCompatibility : INativeDetour
    {
        private readonly NativeDetour _native;
        private MethodBase _trampolineMethod;

        private NativeDetourCompatibility(NativeDetour native) => _native = native;

        internal static INativeDetour Create(IntPtr original, Delegate target) =>
            new NativeDetourCompatibility(NativeDetourFactory.Create(original, target));

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
                _native.PrepareTrampoline();
                _trampolineMethod ??= signature;
                return _trampolineMethod;
            }
        }

        public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate =>
            _native.GenerateTrampoline<TDelegate>();

        public void Apply() => _native.Apply();
        public void Undo() => _native.Undo();
        public void Free() => Dispose();
        public void Dispose()
        {
            lock (NativeDetourFactory.Sync)
            {
                _native.Dispose();
                _trampolineMethod = null;
            }
        }
    }
}
