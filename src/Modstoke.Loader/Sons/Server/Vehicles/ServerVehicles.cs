// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in server policy before native routing of sled and golf-cart control requests.</summary>
    public static class ServerVehicles
    {
        public const string ServiceKey = "openacai.sons.serverVehicles";

        private static VehicleHookState _state = new(VehicleHookStatus.NotAttempted,
            "The vehicle request hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;

        public static event Action<SledGrabRequest> SledGrabbing;
        public static event Action<SledReleaseRequest> SledReleasing;
        public static event Action<GolfCartShoveRequest> GolfCartShoving;
        public static event Action<GolfCartSlotRequest> GolfCartSlotChanging;

        public static VehicleHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(VehicleHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server vehicle policy hook was not installed: " + detail);
                return;
            }
            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(VehicleHookStatus.Installing, "Registering the vehicle request routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.Vehicles);
            }
            catch (Exception error)
            {
                PublishState(VehicleHookStatus.Failed,
                    "Vehicle request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server vehicle policy hook failed: " + error);
                return;
            }
            PublishState(VehicleHookStatus.Installed,
                "Vehicle request hook registered. Live routing and policy handling remain unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            var grab = SledGrabbing;
            if (grab != null && source.TryCast<SledGrab>() is { } grabEvent)
                return Dispatch(grabEvent, grab, static value => new SledGrabRequest((SledGrab)value));
            var release = SledReleasing;
            if (release != null && source.TryCast<SledRelease>() is { } releaseEvent)
                return Dispatch(releaseEvent, release, static value => new SledReleaseRequest((SledRelease)value));
            var shove = GolfCartShoving;
            if (shove != null && source.TryCast<GolfCartShoveEvent>() is { } shoveEvent)
                return Dispatch(shoveEvent, shove, static value => new GolfCartShoveRequest((GolfCartShoveEvent)value));
            var slot = GolfCartSlotChanging;
            if (slot != null && source.TryCast<GolfCartConnectSlotEvent>() is { } slotEvent)
                return Dispatch(slotEvent, slot, static value => new GolfCartSlotRequest((GolfCartConnectSlotEvent)value));
            return DispatchVerdict.Forward;
        }

        private static DispatchVerdict Dispatch<TRequest>(Bolt.Event source, Action<TRequest> handlers,
            Func<Bolt.Event, TRequest> readRequest) where TRequest : VehicleRequest
        {
            TRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportFailure("A subscribed vehicle request ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }
                request = readRequest(source);
                foreach (Action<TRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return DispatchVerdict.Reject;
                }
                return DispatchVerdict.Forward;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportFailure("Reading vehicle context or running a policy handler failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportFailure("Releasing rejected vehicle request storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(VehicleHookStatus status, string detail)
        {
            var state = new VehicleHookState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server vehicle policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum VehicleHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    public sealed class VehicleHookState
    {
        internal VehicleHookState(VehicleHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public VehicleHookStatus Status { get; }
        public string Detail { get; }
    }
}
