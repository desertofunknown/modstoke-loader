// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Construction;
using HarmonyLib;
using Modstoke.Loader;
using Modstoke.Sons;
using NativeCollections = Il2CppSystem.Collections.Generic;

namespace Modstoke.Loader
{
    internal static class ConstructionDirtyPatch
    {
        private const int MinimumIndexedCount = 64;
        private static int _installationAttempted;
        private static int _ownerThread;
        private static bool _enabled;
        private static NativeCollections.List<IStructure> _indexedList;
        private static NativeCollections.HashSet<IStructure> _index;
        private static int _indexedVersion;

        internal static void InstallHarmonyPatch(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Construction dirty-cache optimization is unavailable: Harmony support is not ready.");
                return;
            }

            try
            {
                if (!GameBuild.IsRecognized)
                {
                    // The dedicated-server startup warning already covers client-pinned fixes.
                    if (!GameBuild.IsDedicatedServer)
                        warning?.Invoke("Construction dirty-cache optimization skipped for an unrecognized game build.");
                    return;
                }

                var target = AccessTools.Method(typeof(RuntimeStructureDatabase), nameof(RuntimeStructureDatabase.SetStructureDirty),
                    new[] { typeof(IStructure), typeof(bool) })
                    ?? throw new MissingMethodException(nameof(RuntimeStructureDatabase.SetStructureDirty));
                var clear = AccessTools.Method(typeof(RuntimeStructureDatabase), nameof(RuntimeStructureDatabase.ClearDirtiedStructuresCache))
                    ?? throw new MissingMethodException(nameof(RuntimeStructureDatabase.ClearDirtiedStructuresCache));
                var lastUpdate = AccessTools.Method(typeof(RuntimeStructureDatabase), nameof(RuntimeStructureDatabase.Endnight_Animation_ILastUpdateProxyReceiver_LastUpdate))
                    ?? throw new MissingMethodException(nameof(RuntimeStructureDatabase.Endnight_Animation_ILastUpdateProxyReceiver_LastUpdate));
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-construction-dirty-cache");
                var cleanup = new HarmonyMethod(typeof(ConstructionDirtyPatch), nameof(AfterCacheCleared));
                harmony.Patch(clear, postfix: cleanup);
                // LastUpdate inlines the native list clear, bypassing ClearDirtiedStructuresCache.
                harmony.Patch(lastUpdate, postfix: cleanup);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ConstructionDirtyPatch), nameof(BeforeSetDirty))
                {
                    priority = Priority.Last
                });
                _ownerThread = Environment.CurrentManagedThreadId;
                GameRuntime.SceneChanged += _ => AfterCacheCleared();
                _enabled = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Construction dirty-cache optimization registered for the recognized game build.");
            }
            catch (Exception error)
            {
                _enabled = false;
                warning?.Invoke("Construction dirty-cache optimization registration did not complete: " + error);
            }
        }

        private static bool BeforeSetDirty(IStructure __0, ref bool __1, bool __runOriginal)
        {
            if (!__runOriginal || !__1 || !_enabled || Environment.CurrentManagedThreadId != _ownerThread)
                return true;

            try
            {
                if (RuntimeStructureDatabase.IsLoadingSave)
                    return true;
                var list = RuntimeStructureDatabase.DirtiedStructureCache;
                if (list is null || list.WasCollected)
                    return true;
                int count = list.Count;
                if (count < MinimumIndexedCount)
                {
                    AfterCacheCleared();
                    return true;
                }

                int version = list._version;
                if (_indexedList is null || _indexedList.Pointer != list.Pointer || _indexedVersion != version)
                {
                    _index ??= new NativeCollections.HashSet<IStructure>();
                    _index.Clear();
                    for (int i = 0; i < count; i++)
                        _index.Add(list[i]);
                    _indexedList = list;
                    _indexedVersion = version;
                }

                // Native HashSet uses the same native default equality as List.Contains.
                // Keep the original list authoritative for other callers and inline clears.
                if (!_index.Add(__0))
                    return false;
                list.Add(__0);
                __1 = false;
                _indexedVersion = list._version;
                return true;
            }
            catch (Exception error)
            {
                Disable(error);
                // If the list addition succeeded, __1 is already false and the original
                // still dispatches this notification without repeating its membership test.
                return true;
            }
        }

        private static void AfterCacheCleared()
        {
            if (!_enabled || Environment.CurrentManagedThreadId != _ownerThread || _indexedList is null)
                return;
            try
            {
                _index.Clear();
                _indexedList = null;
            }
            catch (Exception error)
            {
                Disable(error);
            }
        }

        private static void Disable(Exception error)
        {
            _enabled = false;
            _indexedList = null;
            _index = null;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Construction dirty-cache optimization disabled; native processing retained: "
                    + error);
            }
            catch { }
        }
    }
}
