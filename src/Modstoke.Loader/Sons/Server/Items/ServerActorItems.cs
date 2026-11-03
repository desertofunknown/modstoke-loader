// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Opt-in policy before the server handles giving items to, or requesting items from, actors.</summary>
    public static class ServerActorItems
    {
        public const string ServiceKey = "openacai.sons.serverActorItems";

        private static ServerActorItemState _state = new(
            ActorItemHookStatus.NotAttempted, "The give hook has not been installed.",
            ActorItemHookStatus.NotAttempted, "The request hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>Runs before the server's CoopServerCallbacks.OnEvent(PlayerGiveActorItem) body.</summary>
        public static event Action<ActorItemRequest> Giving;

        /// <summary>Runs before the server's CoopServerCallbacks.OnEvent(PlayerRequestActorItem) body.</summary>
        public static event Action<ActorItemRequest> Requesting;

        public static ServerActorItemState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(ActorItemOperation.Give, ActorItemHookStatus.Disabled, detail);
                PublishState(ActorItemOperation.Request, ActorItemHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server actor-item policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(ActorItemOperation.Give, typeof(PlayerGiveActorItem), nameof(BeforeGive), addStartupWarning);
            InstallHook(ActorItemOperation.Request, typeof(PlayerRequestActorItem), nameof(BeforeRequest), addStartupWarning);
        }

        private static void InstallHook(ActorItemOperation operation, Type eventType, string prefixName,
            Action<string> addStartupWarning)
        {
            PublishState(operation, ActorItemHookStatus.Installing, "Registering the actor-item receiver hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType })
                    ?? throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");
                var prefix = new HarmonyMethod(typeof(ServerActorItems), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-actor-items").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(operation, ActorItemHookStatus.Failed,
                    "Actor-item hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server actor-item " + operation + " policy hook failed: " + error);
                return;
            }

            PublishState(operation, ActorItemHookStatus.Installed,
                "Actor-item receiver hook registered. Live handling and policy decisions remain unverified.");
        }

        private static bool BeforeGive(PlayerGiveActorItem __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, Giving, static source => new ActorItemRequest(source));

        private static bool BeforeRequest(PlayerRequestActorItem __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, Requesting, static source => new ActorItemRequest(source));

        private static bool Dispatch<TEvent>(TEvent source, Action<ActorItemRequest> handlers,
            Func<TEvent, ActorItemRequest> readRequest) where TEvent : Bolt.Event
        {
            if (handlers == null)
                return true;

            ActorItemRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("A subscribed actor-item callback ran outside the loader's game thread.");
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
                ReportDispatchFailure("Reading actor-item context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                // Native dispatch owns the event storage at this receiver boundary.
                request?.Close();
            }
        }

        private static void PublishState(ActorItemOperation operation, ActorItemHookStatus status,
            string detail)
        {
            var previous = State;
            var state = operation == ActorItemOperation.Give
                ? new ServerActorItemState(status, detail, previous.RequestStatus, previous.RequestDetail)
                : new ServerActorItemState(previous.GiveStatus, previous.GiveDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server actor-item policy rejected a callback: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ActorItemHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Independent hook registration results, not evidence of receipt, rejection or an inventory change.</summary>
    public sealed class ServerActorItemState
    {
        internal ServerActorItemState(ActorItemHookStatus giveStatus, string giveDetail,
            ActorItemHookStatus requestStatus, string requestDetail)
        {
            GiveStatus = giveStatus;
            GiveDetail = giveDetail;
            RequestStatus = requestStatus;
            RequestDetail = requestDetail;
        }

        public ActorItemHookStatus GiveStatus { get; }
        public string GiveDetail { get; }
        public ActorItemHookStatus RequestStatus { get; }
        public string RequestDetail { get; }
    }
}
