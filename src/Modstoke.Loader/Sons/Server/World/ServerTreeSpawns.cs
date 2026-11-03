// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server policy before the native cut-tree spawn callback changes world objects.</summary>
    public static class ServerTreeSpawns
    {
        /// <summary>Service key for the current tree-spawn hook registration state.</summary>
        public const string ServiceKey = "openacai.sons.serverTreeSpawns";

        private static TreeSpawnHookState _state = new(
            TreeSpawnHookStatus.NotAttempted, "The tree-spawn hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before CoopServerCallbacks.OnEvent(SpawnCutTree) on the server.
        /// Rejection or a handler exception skips only that callback, without rollback or refund.
        /// </summary>
        public static event Action<TreeSpawnRequest> Receiving;

        /// <summary>Current registration state, not evidence of received or blocked requests.</summary>
        public static TreeSpawnHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(TreeSpawnHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server tree-spawn policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(TreeSpawnHookStatus.Installing, "Registering the server tree-spawn hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { typeof(SpawnCutTree) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(SpawnCutTree)");

                var prefix = new HarmonyMethod(typeof(ServerTreeSpawns), nameof(BeforeSpawn))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-tree-spawns");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(TreeSpawnHookStatus.Failed, "Tree-spawn hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server tree-spawn policy hook failed: " + error);
                return;
            }

            PublishState(TreeSpawnHookStatus.Installed,
                "Tree-spawn hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeSpawn(SpawnCutTree __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            var handlers = Receiving;
            if (handlers == null)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The tree-spawn callback ran outside the loader's game thread.");
                return false;
            }

            TreeSpawnRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                request = CaptureRequest(__0);
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
                ReportDispatchFailure("Reading tree-spawn context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static TreeSpawnRequest CaptureRequest(SpawnCutTree source)
        {
            var connection = source.RaisedBy;
            uint? connectionId = connection == null ? null : connection.ConnectionId;
            ulong? registeredSteamId = null;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;
            return new TreeSpawnRequest(source, connectionId, registeredSteamId,
                ServerSessions.ResolveSource(connection));
        }

        private static void PublishState(TreeSpawnHookStatus status, string detail)
        {
            var state = new TreeSpawnHookState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // Log a broken policy once rather than once per player request.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Server tree-spawn policy rejected a request: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Registration status of the server tree-spawn policy hook.</summary>
    public enum TreeSpawnHookStatus
    {
        /// <summary>Registration has not been attempted.</summary>
        NotAttempted,
        /// <summary>Harmony support did not permit registration.</summary>
        Disabled,
        /// <summary>Hook registration is in progress.</summary>
        Installing,
        /// <summary>Registration succeeded; request dispatch and enforcement are not thereby verified.</summary>
        Installed,
        /// <summary>Registration failed and the native patch state may be uncertain.</summary>
        Failed
    }

    /// <summary>Immutable registration state of the server tree-spawn policy hook.</summary>
    public sealed class TreeSpawnHookState
    {
        internal TreeSpawnHookState(TreeSpawnHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Current hook registration status.</summary>
        public TreeSpawnHookStatus Status { get; }

        /// <summary>Registration detail, including any known limitation or failure.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Managed copies of a cut-tree spawn request, with no retained native objects.
    /// Requested values are untrusted payload, not proof of identity, valid placement or tree state.
    /// </summary>
    public sealed class TreeSpawnRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal TreeSpawnRequest(SpawnCutTree source, uint? sourceConnectionId, ulong? registeredSteamId,
            ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedCutPrefabId = source.CutPrefabId;
            var position = source.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            var force = source.Force;
            RequestedForce = new Vector3(force.x, force.y, force.z);
            var forcePosition = source.ForcePosition;
            RequestedForcePosition = new Vector3(forcePosition.x, forcePosition.y, forcePosition.z);
            RequestedUniformScale = source.UniformScale;

            // Native GUID access can fail independently of the remaining payload.
            string rawGuid = null;
            var guidReadSucceeded = false;
            try
            {
                rawGuid = source.Guid.ToString();
                guidReadSucceeded = true;
            }
            catch (Exception) { }
            RequestedRawGuid = rawGuid;
            RequestedGuidReadSucceeded = guidReadSucceeded;
            RequestedGuidParsed = System.Guid.TryParse(rawGuid, out var parsedGuid);
            RequestedGuid = RequestedGuidParsed ? parsedGuid.ToString("D") : null;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public ServerSession SourceSession { get; }

        /// <summary>
        /// Steam ID registered by the game for the source connection, when available.
        /// This is not a separate authentication or permission verdict.
        /// </summary>
        public ulong? RegisteredSteamId { get; }

        /// <summary>Cut-tree prefab ID requested by the sender, not a verified prefab or tree identity.</summary>
        public int RequestedCutPrefabId { get; }

        /// <summary>Sender-requested position, copied without validation or modification.</summary>
        public Vector3 RequestedPosition { get; }

        /// <summary>Sender-requested rotation, copied without validation or normalization.</summary>
        public Quaternion RequestedRotation { get; }

        /// <summary>Sender-requested force, copied without validation or modification.</summary>
        public Vector3 RequestedForce { get; }

        /// <summary>Sender-requested force application position, copied without validation or modification.</summary>
        public Vector3 RequestedForcePosition { get; }

        /// <summary>Sender-requested uniform scale, copied without validation or modification.</summary>
        public float RequestedUniformScale { get; }

        /// <summary>Native GUID string as returned by ToString, or null if that read failed or returned null.</summary>
        public string RequestedRawGuid { get; }

        /// <summary>True only when both the native GUID getter and its ToString call returned successfully.</summary>
        public bool RequestedGuidReadSucceeded { get; }

        /// <summary>Whether the raw GUID string parsed as a managed GUID. Guid.Empty is a valid parse.</summary>
        public bool RequestedGuidParsed { get; }

        /// <summary>Parsed GUID in D format, or null if parsing failed. This does not establish tree validity.</summary>
        public string RequestedGuid { get; }

        /// <summary>UTC time when the managed request snapshot was captured.</summary>
        public DateTimeOffset ReceivedAt { get; }

        /// <summary>Whether this request has been irrevocably rejected.</summary>
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips only this callback. Does not roll back prior tree-state changes, refund or remove objects.
        /// Returns false outside synchronous dispatch or on another thread. Rejection cannot be undone.
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
