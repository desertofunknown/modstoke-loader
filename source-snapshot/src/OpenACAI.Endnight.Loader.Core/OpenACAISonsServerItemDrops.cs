// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Server policy before the native item-drop callback changes world objects.</summary>
    public static partial class OpenACAISonsServerItemDrops
    {
        public const string ServiceKey = "openacai.sons.serverItemDrops";

        private static OpenACAISonsServerItemDropState _state = new(
            OpenACAISonsServerItemDropHookStatus.NotAttempted, "The item-drop hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before CoopServerCallbacks.OnEvent(DropItem).
        /// Rejection or a handler exception skips that body, without refunding client inventory.
        /// </summary>
        public static event Action<OpenACAISonsItemDropRequest> Receiving;

        public static OpenACAISonsServerItemDropState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsServerItemDropHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server item-drop policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsServerItemDropHookStatus.Installing, "Registering the server item-drop hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { typeof(DropItem) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(DropItem)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerItemDrops), nameof(BeforeDrop))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-item-drops");
                harmony.Patch(target, prefix: prefix,
                    postfix: new HarmonyMethod(typeof(OpenACAISonsServerItemDrops), nameof(AfterDrop)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(OpenACAISonsServerItemDrops), nameof(FinishDrop)) { priority = Priority.Last });
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsServerItemDropHookStatus.Failed, "Item-drop hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server item-drop policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsServerItemDropHookStatus.Installed,
                "Item-drop hook registration succeeded. This is not evidence of a received or blocked request.");
            InstallObservationHook(addStartupWarning);
        }

        private static bool BeforeDrop(DropItem __0, out DropScope __state)
        {
            BeginDropObservation(__0, out __state);
            var allowed = ApplyDropPolicy(__0);
            if (__state != null)
            {
                __state.PolicyAllowed = allowed;
                __state.Collecting = allowed;
            }
            return allowed;
        }

        private static bool ApplyDropPolicy(DropItem source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The item-drop callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsItemDropRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                request = CaptureRequest(source);
                foreach (Action<OpenACAISonsItemDropRequest> handler in handlers.GetInvocationList())
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
                ReportDispatchFailure("Reading item-drop context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static OpenACAISonsItemDropRequest CaptureRequest(DropItem source)
        {
            var connection = source.RaisedBy;
            uint? connectionId = connection == null ? null : connection.ConnectionId;
            ulong? registeredSteamId = null;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;
            return new OpenACAISonsItemDropRequest(source, connectionId, registeredSteamId,
                OpenACAISonsServerSessions.ResolveSource(connection));
        }

        private static void PublishState(OpenACAISonsServerItemDropHookStatus status, string detail)
        {
            var previous = State;
            var state = new OpenACAISonsServerItemDropState(status, detail,
                previous.ObservationStatus, previous.ObservationDetail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // Log a broken policy once rather than once per player request.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning(
                    "Server item-drop policy rejected a request: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsServerItemDropHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerItemDropState
    {
        internal OpenACAISonsServerItemDropState(OpenACAISonsServerItemDropHookStatus status, string detail,
            OpenACAISonsServerItemDropHookStatus observationStatus = OpenACAISonsServerItemDropHookStatus.NotAttempted,
            string observationDetail = "Item-drop result observation has not been installed.")
        {
            Status = status;
            Detail = detail;
            ObservationStatus = observationStatus;
            ObservationDetail = observationDetail;
        }

        public OpenACAISonsServerItemDropHookStatus Status { get; }
        public string Detail { get; }
        public OpenACAISonsServerItemDropHookStatus ObservationStatus { get; }
        public string ObservationDetail { get; }
    }

    /// <summary>
    /// Managed copies of a drop request, with no retained native objects. Requested values are
    /// untrusted payload; they do not establish item ownership, quantity or valid world placement.
    /// </summary>
    public sealed class OpenACAISonsItemDropRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsItemDropRequest(DropItem source, uint? sourceConnectionId, ulong? registeredSteamId,
            OpenACAISonsServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedPrefabId = source.PrefabId.Value;
            RequestedItemCount = source.ItemCount;
            var position = source.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            var velocity = source.Velocity;
            RequestedVelocity = new Vector3(velocity.x, velocity.y, velocity.z);
            var torque = source.Torque;
            RequestedTorque = new Vector3(torque.x, torque.y, torque.z);
            var preSpawned = source.PreSpawned;
            RequestedPreSpawnedNetworkId = preSpawned == null ? null : preSpawned.networkId.PackedValue;
            RequestedRawPreSpawnedNetworkId = source.GetPreSpawnedNetworkIdValue().PackedValue;
            RequestedAvoidImpacts = source.AvoidImpacts;
            RequestedShouldDespawn = source.ShouldDespawn;
            RequestedIsKinematic = source.IsKinematic;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public OpenACAISonsServerSession SourceSession { get; }

        /// <summary>
        /// Steam ID registered by the game for the source connection, when available.
        /// This is not a separate authentication or permission verdict.
        /// </summary>
        public ulong? RegisteredSteamId { get; }

        /// <summary>Bolt prefab ID requested by the sender, not an inventory item ID.</summary>
        public int RequestedPrefabId { get; }

        /// <summary>Requested bundle count, not a verified inventory quantity.</summary>
        public int RequestedItemCount { get; }
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
        public Vector3 RequestedVelocity { get; }
        public Vector3 RequestedTorque { get; }

        /// <summary>Sender-selected existing entity, if resolved. This does not establish control over it.</summary>
        public ulong? RequestedPreSpawnedNetworkId { get; }

        /// <summary>Unresolved wire value, including zero. A nonzero value need not resolve to an existing entity.</summary>
        public ulong RequestedRawPreSpawnedNetworkId { get; }

        public bool RequestedAvoidImpacts { get; }
        public bool RequestedShouldDespawn { get; }
        public bool RequestedIsKinematic { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this callback's world changes. Does not restore client inventory or remove existing objects.
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
