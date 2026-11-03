// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in policy before the server handles drying-food placement and carried-actor drops.</summary>
    public static class ServerWorldPlacements
    {
        public const string ServiceKey = "openacai.sons.serverWorldPlacements";

        private static ServerWorldPlacementState _state = new(
            WorldPlacementHookStatus.NotAttempted, "The drying-food hook has not been installed.",
            WorldPlacementHookStatus.NotAttempted, "The actor-drop hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(PlaceDryingFood) on the server.</summary>
        public static event Action<DryingFoodPlacementRequest> PlacingDryingFood;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(DropVailActorEvent) on the server.</summary>
        public static event Action<ActorDropRequest> DroppingActor;

        public static ServerWorldPlacementState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(true, WorldPlacementHookStatus.Disabled, detail);
                PublishState(false, WorldPlacementHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server world-placement policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(true, typeof(PlaceDryingFood), nameof(BeforePlaceDryingFood), addStartupWarning);
            InstallHook(false, typeof(DropVailActorEvent), nameof(BeforeDropActor), addStartupWarning);
        }

        private static void InstallHook(bool dryingFood, Type eventType, string prefixName, Action<string> addStartupWarning)
        {
            PublishState(dryingFood, WorldPlacementHookStatus.Installing, "Registering the world-placement receiver hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType })
                    ?? throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");
                var prefix = new HarmonyMethod(typeof(ServerWorldPlacements), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-world-placements").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(dryingFood, WorldPlacementHookStatus.Failed,
                    "World-placement hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server " + eventType.Name + " policy hook failed: " + error);
                return;
            }

            PublishState(dryingFood, WorldPlacementHookStatus.Installed,
                "World-placement receiver hook registered. Live handling and policy decisions remain unverified.");
        }

        private static bool BeforePlaceDryingFood(PlaceDryingFood __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, PlacingDryingFood, static source => new DryingFoodPlacementRequest(source));

        private static bool BeforeDropActor(DropVailActorEvent __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, DroppingActor, static source => new ActorDropRequest(source));

        private static bool Dispatch<TEvent, TRequest>(TEvent source, Action<TRequest> handlers, Func<TEvent, TRequest> capture)
            where TEvent : Bolt.Event
            where TRequest : WorldPlacementRequest
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
                    ReportDispatchFailure("A subscribed world-placement callback ran outside the loader's game thread.");
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
                ReportDispatchFailure("Reading world-placement context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(bool dryingFood, WorldPlacementHookStatus status, string detail)
        {
            var previous = State;
            var state = dryingFood
                ? new ServerWorldPlacementState(status, detail, previous.ActorDropStatus, previous.ActorDropDetail)
                : new ServerWorldPlacementState(previous.DryingFoodStatus, previous.DryingFoodDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server world-placement policy rejected a callback: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum WorldPlacementHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Independent hook registration results, not evidence of receipt, rejection or a world change.</summary>
    public sealed class ServerWorldPlacementState
    {
        internal ServerWorldPlacementState(WorldPlacementHookStatus dryingFoodStatus,
            string dryingFoodDetail, WorldPlacementHookStatus actorDropStatus, string actorDropDetail)
        {
            DryingFoodStatus = dryingFoodStatus;
            DryingFoodDetail = dryingFoodDetail;
            ActorDropStatus = actorDropStatus;
            ActorDropDetail = actorDropDetail;
        }

        public WorldPlacementHookStatus DryingFoodStatus { get; }
        public string DryingFoodDetail { get; }
        public WorldPlacementHookStatus ActorDropStatus { get; }
        public string ActorDropDetail { get; }
    }
}
