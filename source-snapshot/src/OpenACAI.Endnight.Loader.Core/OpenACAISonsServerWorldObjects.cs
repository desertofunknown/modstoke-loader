// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Policy before native server callbacks open loot containers or break network objects.</summary>
    public static class OpenACAISonsServerWorldObjects
    {
        public const string ServiceKey = "openacai.sons.serverWorldObjects";

        private static OpenACAISonsServerWorldObjectState _state = new(
            OpenACAISonsWorldObjectHookStatus.NotAttempted, "The container-open hook has not been installed.",
            OpenACAISonsWorldObjectHookStatus.NotAttempted, "The object-break hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the matching CoopServerCallbacks.OnEvent body.
        /// Rejection skips only that callback, not prior client changes or other world-state messages.
        /// </summary>
        public static event Action<OpenACAISonsWorldObjectRequest> Receiving;

        public static OpenACAISonsServerWorldObjectState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsWorldObjectOperation.OpenContainer, OpenACAISonsWorldObjectHookStatus.Disabled, detail);
                PublishState(OpenACAISonsWorldObjectOperation.BreakObject, OpenACAISonsWorldObjectHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server world-object policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(OpenACAISonsWorldObjectOperation.OpenContainer, typeof(StorageContainerOpened),
                nameof(BeforeOpen), addStartupWarning);
            InstallHook(OpenACAISonsWorldObjectOperation.BreakObject, typeof(BreakableObjectBroken),
                nameof(BeforeBreak), addStartupWarning);
        }

        private static void InstallHook(OpenACAISonsWorldObjectOperation operation, Type eventType, string prefixName,
            Action<string> addStartupWarning)
        {
            PublishState(operation, OpenACAISonsWorldObjectHookStatus.Installing, "Registering the world-object hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { eventType });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerWorldObjects), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-world-objects");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(operation, OpenACAISonsWorldObjectHookStatus.Failed,
                    "World-object hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server world-object " + operation + " policy hook failed: " + error);
                return;
            }

            PublishState(operation, OpenACAISonsWorldObjectHookStatus.Installed,
                "World-object hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeOpen(StorageContainerOpened __0) => BeforeRequest(__0);
        private static bool BeforeBreak(BreakableObjectBroken __0) => BeforeRequest(__0);

        private static bool BeforeRequest(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The world-object callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsWorldObjectRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = ReadRequest(source, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsWorldObjectRequest> handler in handlers.GetInvocationList())
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
                ReportDispatchFailure("Reading world-object context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static OpenACAISonsWorldObjectRequest ReadRequest(Bolt.Event source, uint? connectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
        {
            // Read wire IDs directly so policy can inspect unresolved targets before native entity lookup.
            if (source is StorageContainerOpened container)
                return new OpenACAISonsWorldObjectRequest(OpenACAISonsWorldObjectOperation.OpenContainer,
                    container.GetContainerEntityNetworkIdValue().PackedValue, container.ContentsSeed,
                    connectionId, registeredSteamId, sourceSession);

            var broken = (BreakableObjectBroken)source;
            return new OpenACAISonsWorldObjectRequest(OpenACAISonsWorldObjectOperation.BreakObject,
                broken.GetEntityNetworkIdValue().PackedValue, null, connectionId, registeredSteamId, sourceSession);
        }

        private static void PublishState(OpenACAISonsWorldObjectOperation operation,
            OpenACAISonsWorldObjectHookStatus status, string detail)
        {
            var previous = State;
            var state = operation == OpenACAISonsWorldObjectOperation.OpenContainer
                ? new OpenACAISonsServerWorldObjectState(status, detail, previous.BreakStatus, previous.BreakDetail)
                : new OpenACAISonsServerWorldObjectState(previous.OpenStatus, previous.OpenDetail, status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server world-object policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsWorldObjectOperation
    {
        OpenContainer,
        BreakObject
    }

    public enum OpenACAISonsWorldObjectHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerWorldObjectState
    {
        internal OpenACAISonsServerWorldObjectState(OpenACAISonsWorldObjectHookStatus openStatus, string openDetail,
            OpenACAISonsWorldObjectHookStatus breakStatus, string breakDetail)
        {
            OpenStatus = openStatus;
            OpenDetail = openDetail;
            BreakStatus = breakStatus;
            BreakDetail = breakDetail;
        }

        public OpenACAISonsWorldObjectHookStatus OpenStatus { get; }
        public string OpenDetail { get; }
        public OpenACAISonsWorldObjectHookStatus BreakStatus { get; }
        public string BreakDetail { get; }
    }

    /// <summary>Unvalidated native request fields copied into managed values without retaining game objects.</summary>
    public sealed class OpenACAISonsWorldObjectRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsWorldObjectRequest(OpenACAISonsWorldObjectOperation operation, ulong requestedTargetNetworkId,
            int? requestedContentsSeed, uint? sourceConnectionId, ulong? registeredSteamId,
            OpenACAISonsServerSession sourceSession)
        {
            Operation = operation;
            RequestedTargetNetworkId = requestedTargetNetworkId;
            RequestedContentsSeed = requestedContentsSeed;
            SourceConnectionId = sourceConnectionId;
            RegisteredSteamId = registeredSteamId;
            SourceSession = sourceSession;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        public OpenACAISonsWorldObjectOperation Operation { get; }
        /// <summary>Raw requested network ID, including zero or an unresolved ID; not proof of target access.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Unmodified container seed, including negative values; null for an object-break request.</summary>
        public int? RequestedContentsSeed { get; }
        /// <summary>Immediate transport source ID, or null for a local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not an independent authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Exact current source lifetime when resolvable. Its metadata can change after this callback.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this native callback only. Does not undo client prediction or stop separate locator-state messages.
        /// Returns false after synchronous dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;

            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close()
        {
            _acceptingDecisions = false;
        }
    }
}
