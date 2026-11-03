// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerCompanions
    {
        private static ServerCompanionState _observationState = new(CompanionHookStatus.NotAttempted,
            "Companion observations have not been installed.");
        private static int _observationInstallationAttempted;
        private static int _observationThread;
        private static int _reportedObservationFailure;
        [ThreadStatic] private static bool _observing;

        /// <summary>
        /// Copied before/after observations of native Kelvin methods that returned on the server.
        /// Observers cannot reject these transitions. An ended order is not proof of success or delivery.
        /// Keep handlers bounded and avoid native actions; observations caused by observers are suppressed.
        /// </summary>
        public static event Action<CompanionObservation> Observed;

        public static ServerCompanionState ObservationState => Volatile.Read(ref _observationState);

        internal static void InstallObservationPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _observationInstallationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                SetObservationState(CompanionHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                return;
            }
            _observationThread = Environment.CurrentManagedThreadId;
            SetObservationState(CompanionHookStatus.Installing, "Registering native companion observations.");
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-companion-observations");
                Patch(nameof(Robby.OnGiveOrderServer), new[] { typeof(Robby.OrderType), typeof(int), typeof(int), typeof(Transform) }, nameof(AfterOrderSelection));
                Patch(nameof(Robby.RunOrder), new[] { typeof(Robby.RobbyOrder), typeof(Transform), typeof(int) }, nameof(AfterOrderRun));
                Patch(nameof(Robby.StartFollowOrder), new[] { typeof(Transform) }, nameof(AfterFollowOrder));
                Patch(nameof(Robby.OnOrderCompleted), new[] { typeof(bool) }, nameof(AfterCompletion));
                Patch(nameof(Robby.OnOrderRefused), new[] { typeof(Transform) }, nameof(AfterRefusal));

                void Patch(string methodName, Type[] arguments, string postfix)
                {
                    var method = AccessTools.Method(typeof(Robby), methodName, arguments)
                        ?? throw new MissingMethodException(typeof(Robby).FullName, methodName);
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ServerCompanions), nameof(BeforeTransition)) { priority = Priority.Last },
                        postfix: new HarmonyMethod(typeof(ServerCompanions), postfix) { priority = Priority.First });
                }
            }
            catch (Exception error)
            {
                SetObservationState(CompanionHookStatus.Failed, "Companion observation registration failed; partial observations remain inactive.");
                addStartupWarning?.Invoke("Companion observation hooks failed: " + error);
                return;
            }
            SetObservationState(CompanionHookStatus.Installed,
                "Native companion observations registered. They do not establish completed work, resource delivery or cancellation.");
        }

        private sealed class ObservationCapture
        {
            internal BoltEntity Entity;
            internal Bolt.Entity Attachment;
            internal Robby Companion;
            internal CompanionSnapshot Before;
        }

        private static void BeforeTransition(Robby __instance, bool __runOriginal, out ObservationCapture __state)
        {
            __state = null;
            if (!__runOriginal || _observing || Observed == null
                || ObservationState.Status != CompanionHookStatus.Installed) return;
            try
            {
                if (CheckObservationContext() != CompanionReadStatus.Success
                    || __instance == null || __instance.WasCollected || !__instance) return;
                var entity = __instance.GetComponentInParent<BoltEntity>();
                if (entity == null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner) return;
                var attachment = entity._entity;
                if (attachment == null || attachment.WasCollected) return;
                var captured = new ObservationCapture { Entity = entity, Attachment = attachment, Companion = __instance };
                if (TrySnapshot(captured, out var snapshot) != CompanionReadStatus.Success) return;
                captured.Before = snapshot;
                __state = captured;
            }
            catch (Exception error) { ReportObservationFailure(error); }
        }

        private static void AfterOrderSelection(ObservationCapture __state, bool __runOriginal) =>
            PublishObservation(__state, __runOriginal, CompanionObservationKind.OrderSelectionReturned, null);
        private static void AfterOrderRun(ObservationCapture __state, bool __runOriginal) =>
            PublishObservation(__state, __runOriginal, CompanionObservationKind.OrderRunReturned, null);
        private static void AfterFollowOrder(ObservationCapture __state, bool __runOriginal) =>
            PublishObservation(__state, __runOriginal, CompanionObservationKind.FollowOrderReturned, null);
        private static void AfterCompletion(bool __0, ObservationCapture __state, bool __runOriginal) =>
            PublishObservation(__state, __runOriginal, CompanionObservationKind.CompletionReturned, __0);
        private static void AfterRefusal(ObservationCapture __state, bool __runOriginal) =>
            PublishObservation(__state, __runOriginal, CompanionObservationKind.RefusalReturned, null);

        private static void PublishObservation(ObservationCapture captured, bool ranOriginal,
            CompanionObservationKind kind, bool? restartAfterRest)
        {
            var handlers = Observed;
            if (!ranOriginal || captured == null || handlers == null || _observing
                || Environment.CurrentManagedThreadId != _observationThread
                || ObservationState.Status != CompanionHookStatus.Installed) return;
            _observing = true;
            try
            {
                var status = TrySnapshot(captured, out var after);
                var observation = new CompanionObservation(kind, captured.Before, after, status, restartAfterRest);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportObservationFailure(error); }
                }
            }
            catch (Exception error) { ReportObservationFailure(error); }
            finally { _observing = false; }
        }

        private static CompanionReadStatus CheckObservationContext()
        {
            if (_observationThread == 0) return CompanionReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != _observationThread) return CompanionReadStatus.WrongThread;
            return BoltNetwork.isRunning && BoltNetwork.isServer
                ? CompanionReadStatus.Success : CompanionReadStatus.NotServer;
        }

        private static CompanionReadStatus TrySnapshot(ObservationCapture captured, out CompanionSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                var status = CheckObservationContext();
                if (status != CompanionReadStatus.Success) return status;
                var entity = captured.Entity;
                var robby = captured.Companion;
                if (entity == null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner
                    || robby == null || robby.WasCollected || !robby)
                    return CompanionReadStatus.TargetUnavailable;
                var attachment = entity._entity;
                var parent = robby.GetComponentInParent<BoltEntity>();
                if (attachment == null || attachment.WasCollected || captured.Attachment.WasCollected
                    || attachment.Pointer != captured.Attachment.Pointer || parent == null || parent.WasCollected
                    || !parent || parent.Pointer != entity.Pointer
                    || (captured.Before != null && entity.networkId.PackedValue != captured.Before.NetworkId))
                    return CompanionReadStatus.ContextChanged;

                CompanionOrderSnapshot orderSnapshot = null;
                var running = robby._activeOrder;
                if (running != null)
                {
                    if (running.WasCollected) return CompanionReadStatus.TargetUnavailable;
                    var order = running._order;
                    if (order == null || order.WasCollected) return CompanionReadStatus.TargetUnavailable;
                    var name = order._itemName ?? string.Empty;
                    if (name.Length > 128) name = name.Substring(0, 128);
                    orderSnapshot = new CompanionOrderSnapshot(
                        new CompanionOrderReference(entity, attachment, robby, running), OrderKind(order), name,
                        running._startTime, running._receivedCompleted, (int)running._dropLocationType);
                }
                var actor = robby._actor;
                var interactor = robby._interactingPlayer;
                var follow = robby._followTransform;
                var activeOverride = robby._activeOverride;
                var orderOverride = robby._currentOrderOverride;
                bool active = activeOverride != null && !activeOverride.WasCollected && activeOverride;
                bool requested = orderOverride != null && !orderOverride.WasCollected && orderOverride;
                snapshot = new CompanionSnapshot(entity.networkId.PackedValue, orderSnapshot, (int)robby._injuredState,
                    actor != null && !actor.WasCollected && actor,
                    interactor != null && !interactor.WasCollected && interactor,
                    follow != null && !follow.WasCollected && follow, active, requested,
                    active && requested && activeOverride.Pointer == orderOverride.Pointer);
                return CompanionReadStatus.Success;
            }
            catch (Exception error)
            {
                ReportObservationFailure(error);
                return CompanionReadStatus.Failed;
            }
        }

        private static CompanionOrderKind OrderKind(Robby.RobbyOrder order)
        {
            if (order.TryCast<Robby.RobbyGetOrder>() != null) return CompanionOrderKind.Get;
            if (order.TryCast<Robby.RobbyBuildOrder>() != null) return CompanionOrderKind.Build;
            if (order.TryCast<Robby.RobbyStayOrder>() != null) return CompanionOrderKind.Stay;
            if (order.TryCast<Robby.RobbyClearOrder>() != null) return CompanionOrderKind.ClearArea;
            if (order.TryCast<Robby.RobbyMaintainOrder>() != null) return CompanionOrderKind.MaintainBase;
            return CompanionOrderKind.Other;
        }

        private static void SetObservationState(CompanionHookStatus status, string detail) =>
            Volatile.Write(ref _observationState, new ServerCompanionState(status, detail));

        private static void ReportObservationFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedObservationFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Companion observation failed (" + error.GetType().FullName
                + "). Native handling was not changed; further observation failures will not be logged."); }
            catch (Exception) { }
        }
    }
}
