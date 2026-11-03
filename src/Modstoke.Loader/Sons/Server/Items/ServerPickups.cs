// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in server policy at the native pickup cleanup receivers.</summary>
    public static class ServerPickups
    {
        public const string ServiceKey = "openacai.sons.serverPickups";

        private static ServerPickupState _state = new(
            PickupHookStatus.NotAttempted, "The destroy hook has not been installed.",
            PickupHookStatus.NotAttempted, "The disable hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs before CoopPlayerCallbacks.OnEvent(DestroyPickUp) on the server.
        /// Rejection skips this receiver, not earlier inventory changes or other listeners.
        /// </summary>
        public static event Action<PickupDestroyRequest> Destroying;

        /// <summary>Runs before the server's CoopPlayerCallbacks.OnEvent(DisablePickup) body.</summary>
        public static event Action<PickupDisableRequest> Disabling;

        public static ServerPickupState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(true, PickupHookStatus.Disabled, detail);
                PublishState(false, PickupHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server pickup policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(true, typeof(DestroyPickUp), nameof(BeforeDestroy), addStartupWarning);
            InstallHook(false, typeof(DisablePickup), nameof(BeforeDisable), addStartupWarning);
        }

        private static void InstallHook(bool destroy, Type eventType, string prefixName, Action<string> addStartupWarning)
        {
            PublishState(destroy, PickupHookStatus.Installing, "Registering the pickup receiver hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopPlayerCallbacks), nameof(CoopPlayerCallbacks.OnEvent), new[] { eventType })
                    ?? throw new MissingMethodException(typeof(CoopPlayerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");
                var prefix = new HarmonyMethod(typeof(ServerPickups), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-pickups").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(destroy, PickupHookStatus.Failed,
                    "Pickup hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server pickup " + eventType.Name + " policy hook failed: " + error);
                return;
            }

            PublishState(destroy, PickupHookStatus.Installed,
                "Pickup receiver hook registered. Live handling and policy decisions remain unverified.");
        }

        private static bool BeforeDestroy(DestroyPickUp __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, Destroying, static source => new PickupDestroyRequest(source));

        private static bool BeforeDisable(DisablePickup __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, Disabling, static source => new PickupDisableRequest(source));

        private static bool Dispatch<TEvent, TRequest>(TEvent source, Action<TRequest> handlers,
            Func<TEvent, TRequest> readRequest) where TEvent : Bolt.Event where TRequest : global::Modstoke.Sons.PickupRequest
        {
            if (handlers == null)
                return true;

            TRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("A subscribed pickup callback ran outside the loader's game thread.");
                    return false;
                }

                request = readRequest(source);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading pickup context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                // This is a receiver callback; native dispatch still owns the event storage.
                request?.Close();
            }
        }

        private static void PublishState(bool destroy, PickupHookStatus status, string detail)
        {
            var previous = State;
            var state = destroy
                ? new ServerPickupState(status, detail, previous.DisableStatus, previous.DisableDetail)
                : new ServerPickupState(previous.DestroyStatus, previous.DestroyDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server pickup policy rejected a callback: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum PickupHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Independent hook registration results, not evidence of received or rejected requests.</summary>
    public sealed class ServerPickupState
    {
        internal ServerPickupState(PickupHookStatus destroyStatus, string destroyDetail,
            PickupHookStatus disableStatus, string disableDetail)
        {
            DestroyStatus = destroyStatus;
            DestroyDetail = destroyDetail;
            DisableStatus = disableStatus;
            DisableDetail = disableDetail;
        }

        public PickupHookStatus DestroyStatus { get; }
        public string DestroyDetail { get; }
        public PickupHookStatus DisableStatus { get; }
        public string DisableDetail { get; }
    }
}
