// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in policy before the server handles zipline attachment and glider pickup/drop.</summary>
    public static class ServerTransports
    {
        public const string ServiceKey = "openacai.sons.serverTransports";

        private static ServerTransportState _state = new(
            TransportHookStatus.NotAttempted, "The zipline hook has not been installed.",
            TransportHookStatus.NotAttempted, "The glider hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(AttachItemToZiplineEvent) on the server.</summary>
        public static event Action<ZiplineItemRequest> AttachingZiplineItem;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(PickupGlider) on the server.</summary>
        public static event Action<GliderRequest> HandlingGlider;

        public static ServerTransportState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(true, TransportHookStatus.Disabled, detail);
                PublishState(false, TransportHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server transport policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(true, typeof(AttachItemToZiplineEvent), nameof(BeforeAttachZiplineItem), addStartupWarning);
            InstallHook(false, typeof(PickupGlider), nameof(BeforeHandleGlider), addStartupWarning);
        }

        private static void InstallHook(bool zipline, Type eventType, string prefixName, Action<string> addStartupWarning)
        {
            PublishState(zipline, TransportHookStatus.Installing, "Registering the transport receiver hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType })
                    ?? throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");
                var prefix = new HarmonyMethod(typeof(ServerTransports), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-transports").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(zipline, TransportHookStatus.Failed,
                    "Transport hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server " + eventType.Name + " policy hook failed: " + error);
                return;
            }

            PublishState(zipline, TransportHookStatus.Installed,
                "Transport receiver hook registered. Live handling and policy decisions remain unverified.");
        }

        private static bool BeforeAttachZiplineItem(AttachItemToZiplineEvent __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, AttachingZiplineItem, static source => new ZiplineItemRequest(source));

        private static bool BeforeHandleGlider(PickupGlider __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, HandlingGlider, static source => new GliderRequest(source));

        private static bool Dispatch<TEvent, TRequest>(TEvent source, Action<TRequest> handlers, Func<TEvent, TRequest> capture)
            where TEvent : Bolt.Event
            where TRequest : TransportRequest
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
                    ReportDispatchFailure("A subscribed transport callback ran outside the loader's game thread.");
                    return false;
                }

                request = capture(source);
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
                ReportDispatchFailure("Reading transport context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(bool zipline, TransportHookStatus status, string detail)
        {
            var previous = State;
            var state = zipline
                ? new ServerTransportState(status, detail, previous.GliderStatus, previous.GliderDetail)
                : new ServerTransportState(previous.ZiplineStatus, previous.ZiplineDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server transport policy rejected a callback: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum TransportHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Independent hook registration results, not evidence of receipt, rejection or a world change.</summary>
    public sealed class ServerTransportState
    {
        internal ServerTransportState(TransportHookStatus ziplineStatus,
            string ziplineDetail, TransportHookStatus gliderStatus, string gliderDetail)
        {
            ZiplineStatus = ziplineStatus;
            ZiplineDetail = ziplineDetail;
            GliderStatus = gliderStatus;
            GliderDetail = gliderDetail;
        }

        public TransportHookStatus ZiplineStatus { get; }
        public string ZiplineDetail { get; }
        public TransportHookStatus GliderStatus { get; }
        public string GliderDetail { get; }
    }
}
