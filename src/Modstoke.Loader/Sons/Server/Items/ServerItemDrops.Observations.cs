// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerItemDrops
    {
        [ThreadStatic] private static DropScope _drop;
        [ThreadStatic] private static bool _publishingDrop;
        private static int _reportedObservationFailure;

        /// <summary>
        /// Observes a server drop callback and matching prefab-factory returns within its dynamic scope.
        /// This is not a general client-spawn event or proof of persistence, replication or permission.
        /// </summary>
        public static event Action<ItemDropObservation> Handled;

        private static void InstallObservationHook(Action<string> addStartupWarning)
        {
            PublishObservationState(ServerItemDropHookStatus.Installing, "Registering item-drop result observation.");
            try
            {
                var target = AccessTools.Method(typeof(BoltNetwork), nameof(BoltNetwork.Instantiate),
                    new[] { typeof(Bolt.PrefabId), typeof(Vector3), typeof(Quaternion) })
                    ?? throw new MissingMethodException(typeof(BoltNetwork).FullName, "Instantiate(PrefabId, Vector3, Quaternion)");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-item-drop-results");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ServerItemDrops), nameof(AfterDropFactory))
                    { priority = Priority.Last });
                PublishObservationState(ServerItemDropHookStatus.Installed,
                    "Item-drop result observation registered. Live correlation remains unverified.");
            }
            catch (Exception error)
            {
                PublishObservationState(ServerItemDropHookStatus.Failed,
                    "Item-drop observation registration failed. Collection is disabled; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server item-drop observation hook failed: " + error);
            }
        }

        private static void PublishObservationState(ServerItemDropHookStatus status, string detail)
        {
            var previous = State;
            var state = new ServerItemDropState(previous.Status, previous.Detail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void BeginDropObservation(out DropScope scope)
        {
            scope = null;
            if (Environment.CurrentManagedThreadId != _ownerThreadId
                || State.Status != ServerItemDropHookStatus.Installed
                || State.ObservationStatus != ServerItemDropHookStatus.Installed
                || (_drop == null && (Handled == null || _publishingDrop)))
                return;

            // A nested callback must shadow its parent even when it has no observers or readable context.
            scope = new DropScope { Parent = _drop };
            _drop = scope;
            try
            {
                if (_publishingDrop || !BoltNetwork.isServer)
                    return;
                scope.Handlers = Handled;
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void CaptureDropSource(DropItem source, ItemDropRequest policyRequest, DropScope scope)
        {
            if (scope.Handlers == null)
                return;
            try
            {
                // Reuse the closed policy capture; it was read before any policy handler ran.
                var request = policyRequest;
                if (request == null)
                {
                    request = CaptureRequest(source);
                    request.Close();
                }
                scope.Source = new ItemDropSource(request);
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void AfterDropFactory(Bolt.PrefabId __0, Vector3 __1, Quaternion __2,
            BoltEntity __result, bool __runOriginal)
        {
            var scope = _drop;
            var source = scope?.Source;
            if (source == null || scope.Handlers == null || !scope.Collecting || _publishingDrop || !__runOriginal)
                return;
            try
            {
                if (__0.Value != source.RequestedPrefabId
                    || !new System.Numerics.Vector3(__1.x, __1.y, __1.z).Equals(source.RequestedPosition)
                    || !new System.Numerics.Quaternion(__2.x, __2.y, __2.z, __2.w).Equals(source.RequestedRotation))
                    return;
                var live = IsLiveDropEntity(__result);
                var attached = live && __result.isAttached;
                scope.Entities.Add(new FactoryReturn
                {
                    Entity = live ? __result : null,
                    PrefabId = live ? __result.prefabId.Value : null,
                    NetworkId = attached ? __result.networkId.PackedValue : null,
                    WasAttached = attached
                });
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void AfterDrop(bool __runOriginal, DropScope __state)
        {
            if (__state != null)
            {
                __state.Collecting = false;
                __state.Outcome = __runOriginal ? ItemDropCallOutcome.Returned : ItemDropCallOutcome.Skipped;
            }
        }

        private static Exception FinishDrop(Exception __exception, DropScope __state)
        {
            if (__state == null)
                return __exception;
            _drop = __state.Parent;
            var publishing = _publishingDrop;
            _publishingDrop = true;
            try
            {
                if (__state.Handlers == null)
                    return __exception;
                var results = new ItemDropEntityResult[__state.Entities.Count];
                for (var i = 0; i < results.Length; i++)
                    results[i] = ReadDropResult(__state.Entities[i], __state);
                var observation = new ItemDropObservation(__state.Source,
                    __exception == null ? __state.Outcome : ItemDropCallOutcome.Faulted,
                    __state.PolicyAllowed, results, __state.HasReadFailures);
                __state.Entities.Clear();
                foreach (var handler in Delegate.EnumerateInvocationList(__state.Handlers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportObservationFailure(error); }
                }
            }
            catch (Exception error) { ReportObservationFailure(error); }
            finally
            {
                __state.Entities.Clear();
                _publishingDrop = publishing;
            }
            return __exception;
        }

        private static ItemDropEntityResult ReadDropResult(FactoryReturn entry, DropScope scope)
        {
            try
            {
                var entity = entry.Entity;
                if (!IsLiveDropEntity(entity) || entity.prefabId.Value != entry.PrefabId
                    || entity.isAttached != entry.WasAttached)
                    return new(ItemDropResultReadStatus.Unavailable, entry.PrefabId, entry.WasAttached, entry.NetworkId);
                if (entry.WasAttached)
                {
                    var id = entity.networkId.PackedValue;
                    var current = id == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(id));
                    if (id != entry.NetworkId || !IsLiveDropEntity(current) || current.Pointer != entity.Pointer)
                        return new(ItemDropResultReadStatus.Unavailable, entry.PrefabId, true, entry.NetworkId);
                }
                return new(ItemDropResultReadStatus.Available, entry.PrefabId, entry.WasAttached, entry.NetworkId);
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
                return new(ItemDropResultReadStatus.Failed, entry.PrefabId, entry.WasAttached, entry.NetworkId);
            }
        }

        private static bool IsLiveDropEntity(BoltEntity entity) => entity is not null && !entity.WasCollected && entity;

        private static void ReportObservationFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedObservationFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server item-drop observation failed ("
                    + error.GetType().FullName + "). Further observation failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class DropScope
        {
            internal DropScope Parent;
            internal Action<ItemDropObservation> Handlers;
            internal ItemDropSource Source;
            internal readonly List<FactoryReturn> Entities = new();
            internal bool Collecting;
            internal bool PolicyAllowed;
            internal bool HasReadFailures;
            internal ItemDropCallOutcome Outcome;
        }

        private sealed class FactoryReturn
        {
            internal BoltEntity Entity;
            internal int? PrefabId;
            internal ulong? NetworkId;
            internal bool WasAttached;
        }
    }
}
