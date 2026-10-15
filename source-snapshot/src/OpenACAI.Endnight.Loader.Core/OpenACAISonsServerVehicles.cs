// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    /// <summary>Opt-in server policy before native routing of sled and golf-cart control requests.</summary>
    public static class OpenACAISonsServerVehicles
    {
        public const string ServiceKey = "openacai.sons.serverVehicles";

        private static OpenACAISonsVehicleHookState _state = new(OpenACAISonsVehicleHookStatus.NotAttempted,
            "The vehicle request hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;

        public static event Action<OpenACAISonsSledGrabRequest> SledGrabbing;
        public static event Action<OpenACAISonsSledReleaseRequest> SledReleasing;
        public static event Action<OpenACAISonsGolfCartShoveRequest> GolfCartShoving;
        public static event Action<OpenACAISonsGolfCartSlotRequest> GolfCartSlotChanging;

        public static OpenACAISonsVehicleHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsVehicleHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server vehicle policy hook was not installed: " + detail);
                return;
            }
            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsVehicleHookStatus.Installing, "Registering the vehicle request routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) })
                    ?? throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");
                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerVehicles), nameof(BeforeDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-vehicles").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsVehicleHookStatus.Failed,
                    "Vehicle request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server vehicle policy hook failed: " + error);
                return;
            }
            PublishState(OpenACAISonsVehicleHookStatus.Installed,
                "Vehicle request hook registered. Live routing and policy handling remain unverified.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var grab = SledGrabbing;
            if (grab != null && __0?.TryCast<SledGrab>() is { } grabEvent)
                return Dispatch(grabEvent, grab, static source => new OpenACAISonsSledGrabRequest((SledGrab)source));
            var release = SledReleasing;
            if (release != null && __0?.TryCast<SledRelease>() is { } releaseEvent)
                return Dispatch(releaseEvent, release, static source => new OpenACAISonsSledReleaseRequest((SledRelease)source));
            var shove = GolfCartShoving;
            if (shove != null && __0?.TryCast<GolfCartShoveEvent>() is { } shoveEvent)
                return Dispatch(shoveEvent, shove, static source => new OpenACAISonsGolfCartShoveRequest((GolfCartShoveEvent)source));
            var slot = GolfCartSlotChanging;
            if (slot != null && __0?.TryCast<GolfCartConnectSlotEvent>() is { } slotEvent)
                return Dispatch(slotEvent, slot, static source => new OpenACAISonsGolfCartSlotRequest((GolfCartConnectSlotEvent)source));
            return true;
        }

        private static bool Dispatch<TRequest>(Bolt.Event source, Action<TRequest> handlers,
            Func<Bolt.Event, TRequest> readRequest) where TRequest : OpenACAISonsVehicleRequest
        {
            var forward = false;
            TRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportFailure("A subscribed vehicle request ran outside the loader's game thread.");
                    return false;
                }
                request = readRequest(source);
                foreach (Action<TRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }
                forward = true;
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportFailure("Reading vehicle context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Skipped dispatch never transfers storage to native routing or listeners.
                    try { source.FreeStorage(); }
                    catch (Exception error) { ReportFailure("Releasing rejected vehicle request storage failed (" + error.GetType().FullName + ")."); }
                }
            }
        }

        private static void PublishState(OpenACAISonsVehicleHookStatus status, string detail)
        {
            var state = new OpenACAISonsVehicleHookState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server vehicle policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsVehicleHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    public sealed class OpenACAISonsVehicleHookState
    {
        internal OpenACAISonsVehicleHookState(OpenACAISonsVehicleHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsVehicleHookStatus Status { get; }
        public string Detail { get; }
    }
}
