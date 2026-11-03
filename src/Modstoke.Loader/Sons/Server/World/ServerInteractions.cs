// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in policy before server structure activation and native debug-command handling.</summary>
    public static class ServerInteractions
    {
        public const string ServiceKey = "openacai.sons.serverInteractions";

        private static ServerInteractionState _state = new(
            InteractionHookStatus.NotAttempted, "The item-plating activation hook has not been installed.",
            InteractionHookStatus.NotAttempted, "The artifact activation hook has not been installed.",
            InteractionHookStatus.NotAttempted, "The debug-command hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(ItemPlatingStructureActivateEvent) on the server.</summary>
        public static event Action<ItemPlatingActivationRequest> ActivatingItemPlatingStructure;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(ArtifactStructureActivateEvent) on the server.</summary>
        public static event Action<ArtifactActivationRequest> ActivatingArtifactStructure;

        /// <summary>Runs synchronously before CoopServerCallbacks.OnEvent(debugCommand) on the server.</summary>
        public static event Action<DebugCommandRequest> ExecutingDebugCommand;

        public static ServerInteractionState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(InteractionKind.ItemPlating, InteractionHookStatus.Disabled, detail);
                PublishState(InteractionKind.Artifact, InteractionHookStatus.Disabled, detail);
                PublishState(InteractionKind.DebugCommand, InteractionHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server interaction policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(InteractionKind.ItemPlating, typeof(ItemPlatingStructureActivateEvent),
                nameof(BeforeActivateItemPlating), addStartupWarning);
            InstallHook(InteractionKind.Artifact, typeof(ArtifactStructureActivateEvent),
                nameof(BeforeActivateArtifact), addStartupWarning);
            InstallHook(InteractionKind.DebugCommand, typeof(debugCommand),
                nameof(BeforeExecuteDebugCommand), addStartupWarning);
        }

        private static void InstallHook(InteractionKind kind, Type eventType, string prefixName,
            Action<string> addStartupWarning)
        {
            PublishState(kind, InteractionHookStatus.Installing, "Registering the interaction receiver hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType })
                    ?? throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");
                var prefix = new HarmonyMethod(typeof(ServerInteractions), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-interactions").Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(kind, InteractionHookStatus.Failed,
                    "Interaction hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server " + eventType.Name + " policy hook failed: " + error);
                return;
            }

            PublishState(kind, InteractionHookStatus.Installed,
                "Interaction receiver hook registered. Live handling and policy decisions remain unverified.");
        }

        private static bool BeforeActivateItemPlating(ItemPlatingStructureActivateEvent __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, ActivatingItemPlatingStructure,
                static source => new ItemPlatingActivationRequest(source));

        private static bool BeforeActivateArtifact(ArtifactStructureActivateEvent __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, ActivatingArtifactStructure,
                static source => new ArtifactActivationRequest(source));

        private static bool BeforeExecuteDebugCommand(debugCommand __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, ExecutingDebugCommand,
                static source => new DebugCommandRequest(source));

        private static bool Dispatch<TEvent, TRequest>(TEvent source, Action<TRequest> handlers, Func<TEvent, TRequest> capture)
            where TEvent : Bolt.Event
            where TRequest : InteractionRequest
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
                    ReportDispatchFailure("A subscribed interaction callback ran outside the loader's game thread.");
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
                ReportDispatchFailure("Reading interaction context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(InteractionKind kind, InteractionHookStatus status, string detail)
        {
            var previous = State;
            var state = new ServerInteractionState(
                kind == InteractionKind.ItemPlating ? status : previous.ItemPlatingStatus,
                kind == InteractionKind.ItemPlating ? detail : previous.ItemPlatingDetail,
                kind == InteractionKind.Artifact ? status : previous.ArtifactStatus,
                kind == InteractionKind.Artifact ? detail : previous.ArtifactDetail,
                kind == InteractionKind.DebugCommand ? status : previous.DebugCommandStatus,
                kind == InteractionKind.DebugCommand ? detail : previous.DebugCommandDetail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server interaction policy rejected a callback: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }

        private enum InteractionKind { ItemPlating, Artifact, DebugCommand }
    }

    public enum InteractionHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Independent hook registration results, not evidence of receipt, rejection or a world change.</summary>
    public sealed class ServerInteractionState
    {
        internal ServerInteractionState(InteractionHookStatus itemPlatingStatus,
            string itemPlatingDetail, InteractionHookStatus artifactStatus, string artifactDetail,
            InteractionHookStatus debugCommandStatus, string debugCommandDetail)
        {
            ItemPlatingStatus = itemPlatingStatus;
            ItemPlatingDetail = itemPlatingDetail;
            ArtifactStatus = artifactStatus;
            ArtifactDetail = artifactDetail;
            DebugCommandStatus = debugCommandStatus;
            DebugCommandDetail = debugCommandDetail;
        }

        public InteractionHookStatus ItemPlatingStatus { get; }
        public string ItemPlatingDetail { get; }
        public InteractionHookStatus ArtifactStatus { get; }
        public string ArtifactDetail { get; }
        public InteractionHookStatus DebugCommandStatus { get; }
        public string DebugCommandDetail { get; }
    }
}
