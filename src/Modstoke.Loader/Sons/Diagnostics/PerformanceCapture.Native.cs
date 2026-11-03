// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Construction;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Save;
using global::Sons.Gameplay;
using global::Sons.TerrainDetail;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class PerformanceCapture
    {
        private static readonly ConcurrentDictionary<MethodBase, NativeTiming> NativeTimings = new();
        private static HarmonyLib.Harmony _captureHarmony;
        private static long _nativeInterval;

        private static void InitializeNativeTimings(HarmonySupportSnapshot support)
        {
            if (support == null || !support.SafeForHarmonyPatches || !GameBuild.IsRecognized)
            {
                Emit(new { kind = "native-hooks", status = "unavailable" });
                return;
            }
            _captureHarmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".performance-capture");
            RegisterTiming(typeof(SaveGameManager), nameof(SaveGameManager.Load), typeof(string), typeof(SaveGameType));
            RegisterTiming(typeof(SaveGameManager), nameof(SaveGameManager.ReadJsonZip), typeof(string),
                typeof(SaveGameManager), typeof(SaveGameType));
            RegisterTiming(typeof(SaveGameManager), nameof(SaveGameManager.ReadJsonFromFiles), typeof(string),
                typeof(SaveGameManager), typeof(SaveGameType));
            if (_schedulerTreeCapture)
            {
                RegisterTiming(typeof(WorkScheduler), nameof(WorkScheduler.ProcessArea), typeof(Vector3),
                    typeof(long), typeof(bool), typeof(bool));
                RegisterTiming(typeof(WorkScheduler), nameof(WorkScheduler.ProcessDormantCalls),
                    typeof(Il2CppSystem.Collections.Generic.HashSet<WorkSchedulerBatch>));
                RegisterTiming(typeof(TreeManager), nameof(TreeManager.GetClosestTreeInSphere),
                    typeof(Vector3), typeof(Vector3), typeof(float));
                RegisterTiming(typeof(TreeManager), nameof(TreeManager.GetTreesInSphere),
                    typeof(Il2CppSystem.Collections.Generic.List<WorldObjectLocator>), typeof(Vector3), typeof(float));
                RegisterTiming(typeof(TreeManager), nameof(TreeManager.GetStumpsInSphere),
                    typeof(Il2CppSystem.Collections.Generic.List<WorldObjectLocator>), typeof(Vector3), typeof(float));
                RegisterTiming(typeof(TreeManager), nameof(TreeManager.GetTreeCountInSphere), typeof(Vector3), typeof(float));
                RegisterTiming(typeof(Robby), nameof(Robby.FindChopTreeStimuli), typeof(Vector3), typeof(float), typeof(bool));
                RegisterTiming(typeof(Robby), nameof(Robby.FindClearStumpStimuli), typeof(Vector3), typeof(float), typeof(bool));
                return;
            }
            RegisterTiming(typeof(RuntimeStructureDatabase), nameof(RuntimeStructureDatabase.LoadStructuresFromData),
                typeof(Il2CppReferenceArray<Il2CppSystem.Collections.Generic.List<RuntimeStructureDatabase.StructureData>>),
                typeof(Il2CppReferenceArray<Il2CppReferenceArray<Structure>>).MakeByRefType(), typeof(bool));
            RegisterTiming(typeof(RuntimeStructureDatabase), nameof(RuntimeStructureDatabase.ExecutePendingActions));
        }

        private static void RegisterTiming(Type type, string name, params Type[] parameters)
        {
            string label = type.FullName + "." + name;
            try
            {
                var target = AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(label);
                NativeTimings[target] = new NativeTiming(label);
                _captureHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PerformanceCapture), nameof(BeforeNative)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PerformanceCapture), nameof(AfterNative)) { priority = Priority.Last });
                Emit(new { kind = "native-hook", name = label, status = "registered-not-yet-observed" });
            }
            catch (Exception error)
            {
                Emit(new { kind = "native-hook", name = label, status = "registration-failed", error = error.GetType().Name });
            }
        }

        private static void BeforeNative(out long __state)
        {
            __state = _active ? Stopwatch.GetTimestamp() : 0;
        }

        private static void AfterNative(MethodBase __originalMethod, long __state, Exception __exception)
        {
            if (!_active || __state == 0) return;
            try
            {
                if (NativeTimings.TryGetValue(__originalMethod, out var timing))
                    timing.Record(Milliseconds(Stopwatch.GetTimestamp() - __state), __exception != null,
                        Environment.CurrentManagedThreadId == _captureThread);
            }
            catch { } // Diagnostics must never replace a game's result or exception.
        }

        private static object[] TakeNativeTimings()
        {
            var timings = NativeTimings.Values;
            var result = new object[timings.Count];
            int index = 0;
            foreach (var timing in timings) result[index++] = timing.Take();
            return result;
        }

        private static object[] TakeNativeInterval(long interval)
        {
            var result = new List<object>(NativeTimings.Count);
            foreach (var timing in NativeTimings.Values)
            {
                object sample = timing.TakeInterval(interval);
                if (sample != null) result.Add(sample);
            }
            return result.ToArray();
        }

        private static void StopNativeTimings()
        {
            try { _captureHarmony?.UnpatchSelf(); }
            catch (Exception error)
            {
                Emit(new { kind = "native-hooks", status = "inactive-unpatch-failed", error = error.GetType().Name });
            }
        }

        private sealed class NativeTiming
        {
            private readonly object _sync = new();
            private readonly string _name;
            private readonly Statistics _values = new();
            private readonly Statistics _captureThreadValues = new(), _otherThreadValues = new();
            private readonly NativeInterval[] _intervals = { new(), new() };
            private int _exceptions;
            private long _totalCalls;
            internal NativeTiming(string name) { _name = name; }
            internal void Record(double ms, bool exception, bool captureThread)
            {
                lock (_sync)
                {
                    _values.Add(ms);
                    (captureThread ? _captureThreadValues : _otherThreadValues).Add(ms);
                    _totalCalls++;
                    if (exception) _exceptions++;
                    if (_schedulerTreeCapture)
                    {
                        // Two buckets retain the just-closed interval while worker calls enter the next one.
                        long interval = Interlocked.Read(ref _nativeInterval);
                        var bucket = _intervals[interval & 1];
                        bucket.Record(interval, ms, captureThread);
                    }
                }
            }
            internal object Take()
            {
                lock (_sync)
                {
                    var result = new { name = _name, inclusiveMs = _values.Take(), exceptions = _exceptions,
                        captureThreadMs = _captureThreadValues.Take(), otherThreadsMs = _otherThreadValues.Take(),
                        observedCalls = _totalCalls };
                    _exceptions = 0;
                    return result;
                }
            }

            internal object TakeInterval(long interval)
            {
                lock (_sync)
                {
                    var bucket = _intervals[interval & 1];
                    return bucket.Interval == interval ? bucket.Snapshot(_name) : null;
                }
            }
        }

        private sealed class NativeInterval
        {
            internal long Interval = -1;
            private int _captureCalls, _otherCalls;
            private double _captureMs, _otherMs, _captureMaxMs, _otherMaxMs;

            internal void Record(long interval, double ms, bool captureThread)
            {
                if (Interval != interval)
                {
                    Interval = interval;
                    _captureCalls = _otherCalls = 0;
                    _captureMs = _otherMs = _captureMaxMs = _otherMaxMs = 0;
                }
                if (captureThread)
                {
                    _captureCalls++;
                    _captureMs += ms;
                    _captureMaxMs = Math.Max(_captureMaxMs, ms);
                }
                else
                {
                    _otherCalls++;
                    _otherMs += ms;
                    _otherMaxMs = Math.Max(_otherMaxMs, ms);
                }
            }

            internal object Snapshot(string name) => new { name,
                captureThread = new { calls = _captureCalls, inclusiveMs = _captureMs, maxMs = _captureMaxMs },
                otherThreads = new { calls = _otherCalls, inclusiveMs = _otherMs, maxMs = _otherMaxMs } };
        }
    }
}
